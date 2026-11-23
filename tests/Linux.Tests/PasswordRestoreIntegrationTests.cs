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

    [Fact]
    public void VerifiedPortableRestorePublishesEncryptedSafetyAndRollsBackExactOriginal()
    {
        if (!OperatingSystem.IsLinux()) return;
        var challenge = new PasswordRestoreChallenge();
        var (app, profile, database) = Setup(challenge);
        var archive = CreateArchive();
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "Recovery")).FullName;
        var original = File.ReadAllBytes(database);
        var verified = BravePasswordRestoreTarget.Review(profile.ProfileDirectory, challenge, default, Lookup);
        var mapping = new BraveRestoreMapping(archive.ProfileId, app, profile, "passwords") { Passwords = verified };
        using var session = BraveProfileRestorePlan.Open(archive.Path, archive.Id, ArchivePassword, recovery,
            SafetyPassword, [mapping], [app.UserDataDirectory], Guid.NewGuid(), null,
            () => ProcessPresence.Stopped, passwordKeyLookup: Lookup);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Empty(Directory.GetFiles(recovery));

        session.Transaction.Prepare();
        Assert.True(session.Transaction.HasPublishedSafety);
        using (var file = File.OpenRead(session.Artifacts.SafetyPath))
        using (var reader = ArchiveEncryption.OpenReader(file, SafetyPassword))
        using (var bytes = new MemoryStream())
        {
            reader.CopyTo(bytes);
            using var snapshot = RestoreSafetyFormat.Decode(bytes.ToArray());
            var item = Assert.Single(snapshot.Entries);
            Assert.Equal("passwords", item.Category);
            Assert.Equal(original, item.Original.Content.Bytes.ToArray());
        }
        Assert.Equal(original, File.ReadAllBytes(database));

        session.Transaction.Run();
        Assert.Equal(RestoreTransactionState.Completed, session.Transaction.State);
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
        Assert.False(original.AsSpan().SequenceEqual(File.ReadAllBytes(database)));
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

    private (string Path, Guid Id, Guid ProfileId) CreateArchive()
    {
        var (installation, profile, archive) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var path = Path.Combine(_root, "source.bravebackup");
        using var source = new PortablePasswordDocument([new(PortablePasswordStoreKind.Profile, 0, 0,
            [new(1, "https://restored.example/", "https://restored.example/", "restored", null,
                new SensitiveUtf8("synthetic-restored-password"u8), [])])]);
        using var payload = PasswordPayloadFormat.Encode(source);
        using var plain = new MemoryStream();
        using (var zip = new ZipArchive(plain, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = BackupArchive.WriteFile(zip, $"installations/{installation:D}/profiles/{profile:D}/passwords/portable-v1.bin",
                "portable-passwords", payload.Bytes.ToArray(), "balanced");
            BackupArchive.WriteManifest(zip, new("BraveBackupArchive", 6, archive, DateTimeOffset.UtcNow, "synthetic",
                ["passwords-v1"], "balanced",
                [new(installation, "Browser", "Stable", "1.96.61", [new(profile, "Source", "Default", [new("passwords", 1, [entry])])])]));
        }
        using (var file = File.Create(path))
        using (var writer = ArchiveEncryption.CreateWriter(file, ArchivePassword))
            writer.Write(plain.GetBuffer().AsSpan(0, (int)plain.Length));
        return (path, archive, profile);
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
