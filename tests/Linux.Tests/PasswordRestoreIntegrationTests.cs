using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordRestoreIntegrationTests : IDisposable
{
    private const string ArchivePassword = "synthetic-source-protection";
    private const string SafetyPassword = "synthetic-safety-protection";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"PasswordRestore-{Guid.NewGuid():N}");
    private static byte[] Lookup(string application, CancellationToken token)
    {
        Assert.Equal("brave", application);
        token.ThrowIfCancellationRequested();
        return "synthetic-keyring-secret"u8.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerifiedPortableRestorePublishesEncryptedSafetyAndRollsBackExactOriginal(bool mixed)
    {
        if (!OperatingSystem.IsLinux()) return;
        var challenge = new PasswordRestoreChallenge();
        var (app, profile, database) = Setup(challenge);
        var archive = CreateArchive(mixed);
        var originalPreferences = new Dictionary<string, byte[]>();
        if (mixed)
        {
            var second = Directory.CreateDirectory(Path.Combine(app.UserDataDirectory, "Profile 1")).FullName;
            File.WriteAllText(Path.Combine(second, "Preferences"), """{"unrelated":"keep"}""");
            foreach (var directory in new[] { profile.ProfileDirectory, second })
            {
                var preferences = Path.Combine(directory, "Preferences");
                originalPreferences.Add(preferences, File.ReadAllBytes(preferences));
            }
        }
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "Recovery")).FullName;
        var original = File.ReadAllBytes(database);
        var verified = BravePasswordRestoreTarget.Review(profile.ProfileDirectory, challenge, default, Lookup);
        var mappings = new List<BraveRestoreMapping>
        {
            new(archive.ProfileId, app, profile, "passwords") { Passwords = verified }
        };
        if (mixed)
        {
            var second = Path.Combine(app.UserDataDirectory, "Profile 1");
            mappings.Add(new(archive.ProfileId, app, profile, "settings"));
            mappings.Add(new(archive.SettingsProfileId, app,
                new("Profile 1", second, Path.Combine(second, "Bookmarks")), "settings"));
        }
        using var session = BraveProfileRestorePlan.Open(archive.Path, archive.Id, ArchivePassword, recovery,
            SafetyPassword, mappings, [app.UserDataDirectory], Guid.NewGuid(), null,
            () => ProcessPresence.Stopped, passwordKeyLookup: Lookup);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Empty(Directory.GetFiles(recovery));

        session.Transaction.Prepare();
        Assert.True(session.Transaction.HasPublishedSafety);
        var prepared = RestoreArtifactStorage.InspectRestart(session.Artifacts.RestartRecordPath, session.Artifacts.SafetyPath,
            session.Artifacts.JournalPath, SafetyPassword);
        Assert.Equal(RestoreTransactionState.Prepared, prepared.Journal.State);
        Assert.All(prepared.Journal.Entries, entry => Assert.Equal(RestoreEntryState.Untouched, entry.State));
        Assert.Equal(profile.ProfileDirectory, prepared.Record.Targets[0].ProfileDirectory);
        Assert.Equal("Login Data", prepared.Record.Targets[0].RelativeTarget);
        Assert.StartsWith("linux-chain-v1:", prepared.Record.Targets[0].DirectoryEvidence);
        Assert.Single(Directory.GetFiles(recovery, "*.restart.encrypted"));
        using (var file = File.OpenRead(session.Artifacts.SafetyPath))
        using (var reader = ArchiveEncryption.OpenReader(file, SafetyPassword))
        using (var bytes = new MemoryStream())
        {
            reader.CopyTo(bytes);
            using var snapshot = RestoreSafetyFormat.Decode(bytes.ToArray());
            Assert.Equal(mixed ? 3 : 1, snapshot.Entries.Count);
            var item = Assert.Single(snapshot.Entries, entry => entry.Category == "passwords");
            Assert.Equal(original, item.Original.Content.Bytes.ToArray());
        }
        Assert.Equal(original, File.ReadAllBytes(database));

        session.Transaction.Run();
        Assert.Equal(RestoreTransactionState.Completed, session.Transaction.State);
        foreach (var preferences in originalPreferences.Keys)
            Assert.Contains("https://settings.example/", File.ReadAllText(preferences));
        if (mixed)
            Assert.Contains("keep", File.ReadAllText(Path.Combine(app.UserDataDirectory, "Profile 1", "Preferences")));
        using var restored = BravePasswordDatabase.Read(File.ReadAllBytes(database));
        var row = Assert.Single(restored.Rows);
        Assert.Equal("restored", row.Username);
        using var key = BraveLinuxPasswordKey.Unlock(Lookup);
        using var password = BravePasswordCrypto.Decode(row.EncryptedValue, key);
        Assert.Equal("synthetic-restored-password"u8.ToArray(), password.Utf8.ToArray());

        var sidecar = database + "-wal";
        File.WriteAllText(sidecar, "synthetic pending transaction");
        session.Transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, session.Transaction.State);
        var inspection = RestoreArtifactStorage.InspectRestart(session.Artifacts.RestartRecordPath, session.Artifacts.SafetyPath,
            session.Artifacts.JournalPath, SafetyPassword).Journal;
        Assert.Equal(session.Transaction.OperationId, inspection.OperationId);
        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, inspection.State);
        Assert.Equal(RestoreEntryState.Applied, Assert.Single(inspection.Entries, entry => entry.Category == "passwords").State);
        Assert.False(original.AsSpan().SequenceEqual(File.ReadAllBytes(database)));
        foreach (var (preferences, bytes) in originalPreferences)
            Assert.Equal(bytes, File.ReadAllBytes(preferences));
        if (mixed)
            Assert.Equal(2, session.Transaction.EntryStates.Count(entry => entry == RestoreEntryState.Recovered));
        File.Delete(sidecar);

        session.Transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, session.Transaction.State);
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Theory]
    [InlineData("wrong-profile", "mapped profile")]
    [InlineData("basic-provider", "v11")]
    [InlineData("sync-seed", "Sync chain")]
    [InlineData("pending-sidecar", "nonempty SQLite sidecar")]
    public void UnsupportedDestinationDoesNotAskForAKey(string scenario, string message)
    {
        if (!OperatingSystem.IsLinux()) return;
        var challenge = new PasswordRestoreChallenge();
        var (_, profile, database) = Setup(challenge, v11: scenario != "basic-provider");
        if (scenario == "sync-seed")
            File.WriteAllText(Path.Combine(profile.ProfileDirectory, "Preferences"),
                """{"brave_sync_v2":{"seed":"synthetic-seed"}}""");
        if (scenario == "pending-sidecar")
            File.WriteAllText(database + "-journal", "synthetic journal");
        var supplied = scenario == "wrong-profile" ? new PasswordRestoreChallenge() : challenge;
        var original = File.ReadAllBytes(database);

        var error = Assert.Throws<BackupOperationException>(() => BravePasswordRestoreTarget.Review(
            profile.ProfileDirectory, supplied, default, (_, _) => throw new InvalidOperationException("Unexpected key lookup.")));
        Assert.Contains(message, error.Message);
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Fact]
    public void ChangedDatabaseAfterRecheckBlocksPlanBeforeSafetyPublication()
    {
        if (!OperatingSystem.IsLinux()) return;
        var challenge = new PasswordRestoreChallenge();
        var (app, profile, database) = Setup(challenge);
        var archive = CreateArchive();
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "Recovery")).FullName;
        var verified = BravePasswordRestoreTarget.Review(profile.ProfileDirectory, challenge, default, Lookup);
        Execute(database, "INSERT INTO stats VALUES ('https://changed.example/','fixture',1,0)");
        var changed = File.ReadAllBytes(database);

        var error = Assert.Throws<BackupOperationException>(() => BraveProfileRestorePlan.Open(
            archive.Path, archive.Id, ArchivePassword, recovery, SafetyPassword,
            [new(archive.ProfileId, app, profile, "passwords") { Passwords = verified }], [app.UserDataDirectory],
            Guid.NewGuid(), null, () => ProcessPresence.Stopped, passwordKeyLookup: Lookup));
        Assert.Contains("changed after Recheck", error.Message);
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Equal(changed, File.ReadAllBytes(database));
    }

    [Theory]
    [InlineData("prepare")]
    [InlineData("preflight")]
    [InlineData("publication")]
    public void BlockedOrChangedRestartRecordPreventsProfileWrites(string phase)
    {
        if (!OperatingSystem.IsLinux()) return;
        var challenge = new PasswordRestoreChallenge();
        var (app, profile, database) = Setup(challenge);
        var archive = CreateArchive();
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "Recovery")).FullName;
        var verified = BravePasswordRestoreTarget.Review(profile.ProfileDirectory, challenge, default, Lookup);
        var original = File.ReadAllBytes(database);
        var operation = Guid.NewGuid();
        var recordPath = Path.Combine(recovery, operation.ToString("N") + ".restart.encrypted");
        var intercepted = false;
        ProcessPresence Observe()
        {
            if (phase == "publication" && !intercepted &&
                Directory.EnumerateFiles(profile.ProfileDirectory, ".restore-*.tmp").Any())
            {
                intercepted = true;
                File.AppendAllText(recordPath, "external change during publication");
            }
            return ProcessPresence.Stopped;
        }

        using var session = BraveProfileRestorePlan.Open(archive.Path, archive.Id, ArchivePassword, recovery, SafetyPassword,
            [new(archive.ProfileId, app, profile, "passwords") { Passwords = verified }], [app.UserDataDirectory],
            operation, null, Observe, passwordKeyLookup: Lookup);

        if (phase == "prepare")
        {
            File.WriteAllText(session.Artifacts.RestartRecordPath, "existing file");
            Assert.ThrowsAny<IOException>(() => session.Transaction.Prepare());
            Assert.Equal(RestoreTransactionState.Failed, session.Transaction.State);
            Assert.Equal("existing file", File.ReadAllText(session.Artifacts.RestartRecordPath));
            Assert.False(File.Exists(session.Artifacts.JournalPath));
        }
        else if (phase == "preflight")
        {
            session.Transaction.Prepare();
            File.AppendAllText(recordPath, "external change");
            Assert.Throws<InvalidDataException>(() => session.Transaction.Run());
            Assert.Equal(RestoreTransactionState.Prepared, session.Transaction.State);
        }
        else
        {
            session.Transaction.Prepare();
            Assert.Throws<IOException>(() => session.Transaction.Run());
            Assert.True(intercepted);
            Assert.Equal(RestoreTransactionState.Failed, session.Transaction.State);
            Assert.Empty(Directory.EnumerateFiles(profile.ProfileDirectory, ".restore-*.tmp"));
        }

        Assert.True(File.Exists(session.Artifacts.SafetyPath));
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    [Fact]
    public void RestartRollbackWaitsForPasswordSidecarAndCanRetry()
    {
        if (!OperatingSystem.IsLinux()) return;
        var challenge = new PasswordRestoreChallenge();
        var (app, profile, database) = Setup(challenge);
        if (!RecoveryDirectoryIdentityTests.SupportsRestartEvidence(profile.ProfileDirectory)) return;
        var archive = CreateArchive();
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "Recovery")).FullName;
        var verified = BravePasswordRestoreTarget.Review(profile.ProfileDirectory, challenge, default, Lookup);
        var original = File.ReadAllBytes(database);
        RestoreArtifactScope artifacts;
        using (var live = BraveProfileRestorePlan.Open(archive.Path, archive.Id, ArchivePassword, recovery, SafetyPassword,
            [new(archive.ProfileId, app, profile, "passwords") { Passwords = verified }], [app.UserDataDirectory],
            Guid.NewGuid(), null, () => ProcessPresence.Stopped, passwordKeyLookup: Lookup))
        {
            artifacts = live.Artifacts;
            live.Transaction.Prepare();
            live.Transaction.Run();
        }
        File.Delete(archive.Path);

        var replacement = File.ReadAllBytes(database);
        var sidecar = database + "-wal";
        File.WriteAllText(sidecar, "synthetic pending transaction");
        using (var blocked = LinuxRestartRollback.Open(artifacts.RestartRecordPath, SafetyPassword,
            [(app, profile)], () => ProcessPresence.Stopped))
        {
            Assert.Equal(RestoreRollbackDecision.Conflict, Assert.Single(blocked.Review.Targets).Decision);
            Assert.Equal(RestoreTransactionState.RecoveryIncomplete, blocked.Rollback(blocked.Review));
            Assert.Equal(replacement, File.ReadAllBytes(database));
        }

        File.Delete(sidecar);
        using var retried = LinuxRestartRollback.Open(artifacts.RestartRecordPath, SafetyPassword,
            [(app, profile)], () => ProcessPresence.Stopped);
        Assert.Equal(RestoreTransactionState.RolledBack, retried.Rollback(retried.Review));
        Assert.Equal(original, File.ReadAllBytes(database));
    }

    private (BraveAppInfo App, BraveProfileInfo Profile, string Database) Setup(PasswordRestoreChallenge challenge, bool v11 = true)
    {
        var data = Path.Combine(_root, "User Data");
        var directory = Directory.CreateDirectory(Path.Combine(data, "Default")).FullName;
        File.WriteAllText(Path.Combine(directory, "Preferences"), "{}");
        var database = Path.Combine(directory, "Login Data");
        Execute(database, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "schema-v43-brave-154.sql")));
        var raw = Rfc2898DeriveBytes.Pbkdf2("synthetic-keyring-secret"u8, "saltysalt"u8, 1, HashAlgorithmName.SHA1, 16);
        using var aes = Aes.Create();
        aes.Key = raw;
        CryptographicOperations.ZeroMemory(raw);
        var encrypted = (v11 ? "v11"u8.ToArray() : "v10"u8.ToArray())
            .Concat(aes.EncryptCbc(Encoding.ASCII.GetBytes(challenge.Password), "                "u8, PaddingMode.PKCS7)).ToArray();
        Execute(database, """
            INSERT INTO logins(origin_url, username_value, password_value, signon_realm, date_created,
                blacklisted_by_user, scheme, federation_url)
            VALUES ($site, $user, $value, $site, 0, 0, 0, '')
            """, command =>
        {
            command.Parameters.AddWithValue("$site", PasswordRestoreChallenge.Site);
            command.Parameters.AddWithValue("$user", challenge.Username);
            command.Parameters.AddWithValue("$value", encrypted);
        });
        return (new(BraveAppChannel.Stable, "1.96.61", "synthetic", data),
            new("Default", directory, Path.Combine(directory, "Bookmarks")), database);
    }

    private (string Path, Guid Id, Guid ProfileId, Guid SettingsProfileId) CreateArchive(bool mixed = false)
    {
        var (installation, profile, archive) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var settingsProfile = Guid.NewGuid();
        var path = Path.Combine(_root, "source.bravebackup");
        using var source = new PortablePasswordDocument([new(PortablePasswordStoreKind.Profile, 0, 0,
            [new(1, "https://restored.example/", "https://restored.example/", "restored", null,
                new SensitiveUtf8("synthetic-restored-password"u8), [])])]);
        using var payload = PasswordPayloadFormat.Encode(source);
        using var plain = new MemoryStream();
        using (var zip = new ZipArchive(plain, ZipArchiveMode.Create, leaveOpen: true))
        {
            var passwordCategory = Passwords(profile);
            string[] capabilities = mixed ? ["settings-v1", "passwords-v1"] : ["passwords-v1"];
            ArchiveProfile[] profiles = mixed
                ? [new(profile, "Source", "Default", [passwordCategory, Settings(profile)]),
                   new(settingsProfile, "Settings source", "Profile 1", [Passwords(settingsProfile), Settings(settingsProfile)])]
                : [new(profile, "Source", "Default", [passwordCategory])];
            BackupArchive.WriteManifest(zip, new("BraveBackupArchive", 6, archive, DateTimeOffset.UtcNow, "synthetic",
                capabilities, "balanced", [new(installation, "Browser", "Stable", "1.96.61", profiles)]));

            ArchiveCategory Passwords(Guid target)
            {
                var entry = BackupArchive.WriteFile(zip, $"installations/{installation:D}/profiles/{target:D}/passwords/portable-v1.bin",
                    "portable-passwords", payload.Bytes.ToArray(), "balanced");
                return new("passwords", 1, [entry]);
            }

            ArchiveCategory Settings(Guid target)
            {
                var contents = BraveSettingsBackup.Extract("""{"homepage":"https://settings.example/"}"""u8.ToArray());
                var file = BackupArchive.WriteFile(zip,
                    $"installations/{installation:D}/profiles/{target:D}/settings/allowlisted-v1.json",
                    "portable-settings", contents, "balanced");
                return new("settings", 1, [file]);
            }
        }
        using (var file = File.Create(path))
        using (var writer = ArchiveEncryption.CreateWriter(file, ArchivePassword))
            writer.Write(plain.GetBuffer().AsSpan(0, (int)plain.Length));
        return (path, archive, profile, settingsProfile);
    }

    private static void Execute(string database, string sql, Action<SqliteCommand>? bind = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
