using System.IO.Compression;
using System.Text.Json;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class CsvPasswordBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CsvBackup_" + Guid.NewGuid().ToString("N"));
    private BraveBackupSelection[] _scope = [];
    private int _unlocks;
    private string Destination => Path.Combine(_root, "output", "backup.bravebackup");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private void Prepare(bool empty = false, string version = "1.102.13")
    {
        var data = Path.Combine(_root, "data"); var profile = Path.Combine(data, "Default");
        Directory.CreateDirectory(profile);
        File.Copy(Path.Combine(Fixtures, "Passwords", empty ? "LoginData.v43.empty.sqlite" : "LoginData.v43.linux.sqlite"), Path.Combine(profile, "Login Data"));
        File.Copy(Path.Combine(Fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(profile, BraveAffiliationSnapshot.DatabaseFileName));
        if (!empty)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(profile, "Login Data"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE password_notes SET key=''"; command.ExecuteNonQuery();
        }
        _scope = [new(new BraveAppInfo(BraveAppChannel.Stable, version, "synthetic", data),
            [new BraveProfileInfo("Synthetic", profile, Path.Combine(profile, "Bookmarks"))])];
    }

    private PasswordBackupOptions Options(Func<PasswordCsvArchiveWarning, CancellationToken, Task<bool>>? confirm,
        PasswordCsvLineEnding? ending = PasswordCsvLineEnding.LinuxLf) => new()
    {
        ProtectPasswords = false, ConfirmCsvArchiveSave = confirm, CsvLineEnding = ending,
        GetProvider = (_, _) => PasswordEncryptionProvider.LinuxKeyringV11,
        ObserveBrowserState = () => ProcessPresence.Stopped,
        Unlock = (_, _, _) =>
        {
            _unlocks++;
            using var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
            return new PasswordKeyMaterial(PasswordEncryptionProvider.LinuxKeyringV11,
                Convert.FromHexString(vector.RootElement.GetProperty("linuxKeyHex").GetString()!));
        }
    };
    private Task<BackupResult> Create(PasswordBackupOptions options, string? password = null,
        IProgress<BackupProgress>? progress = null, CancellationToken token = default) =>
        BraveArchiveBackup.CreateAsync(_scope, ["passwords"], Destination, password, "balanced", [], "synthetic",
            _ => Task.FromResult(true), progress, token, new ExtensionBackupOptions(), new SessionBackupOptions(),
            new TabManagerPlusBackupOptions(), options);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCsvChoiceWarnsBeforeWritesAndCreatesValidV8(bool encrypted)
    {
        Prepare(); var before = Directory.GetFiles(Path.Combine(_root, "data"), "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var calls = 0;
        var result = await Create(Options((warning, _) =>
        {
            calls++;
            Assert.Equal(Destination, warning.Destination); Assert.Equal(encrypted, warning.ArchiveEncrypted);
            Assert.Equal(1, warning.ProfileCount); Assert.False(warning.ReplacesExistingFile);
            Assert.Contains("readable by anyone with file access", warning.Message);
            Assert.False(Directory.Exists(Path.GetDirectoryName(Destination))); Assert.Equal(0, _unlocks);
            return Task.FromResult(true);
        }), encrypted ? "synthetic-archive-password" : null);
        Assert.Equal(1, calls); Assert.Equal(1, _unlocks);
        using var input = File.OpenRead(Destination);
        Assert.Equal(encrypted, ArchiveEncryption.IsEncrypted(input));
        var manifest = BraveSettingsBackup.ValidateArchive(input, encrypted ? "synthetic-archive-password" : null);
        Assert.Equal(8, manifest.FormatVersion); Assert.Equal(new[] { "passwords-v3" }, manifest.RequiredCapabilities);
        using var decrypted = encrypted ? ArchiveEncryption.OpenReader(input, "synthetic-archive-password") : null;
        input.Position = 0;
        using var zip = new ZipArchive(decrypted ?? input, ZipArchiveMode.Read);
        var file = Assert.Single(Assert.Single(Assert.Single(manifest.Installations).Profiles).Categories).Files.Single();
        Assert.Equal("plaintext-password-csv", file.Role);
        using var entry = zip.GetEntry(file.Path)!.Open(); using var bytes = new MemoryStream(); entry.CopyTo(bytes);
        Assert.Equal(1, PasswordCsvValidation.Validate(bytes.ToArray()));
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
        var expected = "name,url,username,password,note\nexample.invalid,https://example.invalid/login,fixture-user,\"" +
            vectors.RootElement.GetProperty("passwordUtf8").GetString()!.Replace("\"", "\"\"") + "\",\"" +
            vectors.RootElement.GetProperty("noteUtf8").GetString()!.Replace("\"", "\"\"") + "\"\n";
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        Assert.Contains("CSV rows: 1", result.PasswordsSummary); Assert.DoesNotContain("fixture-user", result.Report);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
        foreach (var source in before) Assert.Equal(source.Value, File.ReadAllBytes(source.Key));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("decline")]
    [InlineData("cancel")]
    public async Task MissingDeclinedOrCancelledConsentDoesNotWriteOrUnlock(string action)
    {
        Prepare(); using var cancellation = new CancellationTokenSource();
        var options = Options(action == "missing" ? null : (_, _) =>
        { if (action == "cancel") cancellation.Cancel(); return Task.FromResult(action == "cancel"); });
        var error = await Record.ExceptionAsync(() => Create(options, token: cancellation.Token));
        Assert.NotNull(error); Assert.Equal(0, _unlocks);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Destination)));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("cache")]
    [InlineData("destination")]
    [InlineData("cancel")]
    public async Task FailureAfterStagingPreservesExistingDestinationAndCleansTemporary(string failure)
    {
        Prepare(); Directory.CreateDirectory(Path.GetDirectoryName(Destination)!); File.WriteAllText(Destination, "synthetic-original");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value =>
        {
            if (value.Stage != "Verifying") return;
            if (failure == "source") File.AppendAllText(Path.Combine(_scope[0].Profiles[0].ProfileDirectory, "Login Data"), "changed");
            if (failure == "cache") File.AppendAllText(Path.Combine(_scope[0].Profiles[0].ProfileDirectory, BraveAffiliationSnapshot.DatabaseFileName), "changed");
            if (failure == "destination") File.WriteAllText(Destination, "synthetic-concurrent");
            if (failure == "cancel") cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<Exception>(() => Create(Options((warning, _) =>
        { Assert.True(warning.ReplacesExistingFile); return Task.FromResult(true); }), progress: progress, token: cancellation.Token));
        Assert.Equal(failure == "destination" ? "synthetic-concurrent" : "synthetic-original", File.ReadAllText(Destination));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task ChangedDestinationDuringConsentFailsBeforeUnlockAndEveryRetryWarnsFreshly()
    {
        Prepare(empty: true); var calls = 0;
        var options = Options((_, _) =>
        {
            calls++; Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
            File.WriteAllText(Destination, "synthetic-changed-" + calls); return Task.FromResult(true);
        });
        for (var index = 0; index < 2; index++)
            Assert.Equal("destination-changed", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(options))).Code);
        Assert.Equal(2, calls); Assert.Equal(0, _unlocks);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task UnsupportedVersionOrMissingRecordSeparatorsFailBeforeConsent()
    {
        Prepare(version: "1.0.0"); var calls = 0;
        Task<bool> Confirm(PasswordCsvArchiveWarning _, CancellationToken __) { calls++; return Task.FromResult(true); }
        Assert.Equal("passwords-csv-version-unsupported", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(Options(Confirm)))).Code);
        Assert.Equal("passwords-csv-line-ending", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(Options(Confirm, null)))).Code);
        Assert.Equal(0, calls); Assert.Equal(0, _unlocks);
    }

    [Fact]
    public async Task MixedCategoriesAndProfilesIncludeEmptyCsvWithoutExtraUnlock()
    {
        Prepare();
        var original = _scope[0]; var emptyDirectory = Path.Combine(original.Installation.UserDataDirectory, "Profile 1");
        Directory.CreateDirectory(emptyDirectory);
        File.Copy(Path.Combine(Fixtures, "Passwords", "LoginData.v43.empty.sqlite"), Path.Combine(emptyDirectory, "Login Data"));
        var empty = new BraveProfileInfo("Empty", emptyDirectory, Path.Combine(emptyDirectory, "Bookmarks"));
        var profiles = new[] { original.Profiles[0], empty };
        foreach (var profile in profiles) File.Copy(Path.Combine(Fixtures, "Bookmarks.sample.json"), profile.BookmarksPath);
        var calls = 0;
        var options = Options((warning, _) => { calls++; Assert.Equal(2, warning.ProfileCount); return Task.FromResult(true); }, PasswordCsvLineEnding.WindowsCrLf);
        var result = await BraveArchiveBackup.CreateAsync([new(original.Installation, profiles)], ["bookmarks", "passwords"],
            Destination, null, "balanced", [], "synthetic", _ => Task.FromResult(false), null, default,
            new ExtensionBackupOptions(), new SessionBackupOptions(), new TabManagerPlusBackupOptions(), options);
        Assert.Equal(1, calls); Assert.Equal(1, _unlocks); Assert.Contains("CSV profiles: 2", result.PasswordsSummary);
        using var input = File.OpenRead(Destination);
        var manifest = BraveSettingsBackup.ValidateArchive(input);
        Assert.Equal(new[] { "bookmarks-v1", "passwords-v3" }, manifest.RequiredCapabilities);
        input.Position = 0; using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        var category = manifest.Installations[0].Profiles.Single(profile => profile.DisplayName == "Empty").Categories.Single(c => c.Id == "passwords");
        using var entry = zip.GetEntry(category.Files[0].Path)!.Open(); using var reader = new StreamReader(entry);
        Assert.Equal("name,url,username,password,note\r\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task MissingAffiliationCacheFailsWholeArchiveWithoutPublishingPartialCsv()
    {
        Prepare(); File.Delete(Path.Combine(_scope[0].Profiles[0].ProfileDirectory, BraveAffiliationSnapshot.DatabaseFileName));
        await Assert.ThrowsAsync<BackupOperationException>(() => Create(Options((_, _) => Task.FromResult(true))));
        Assert.False(File.Exists(Destination)); Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class InlineProgress(Action<BackupProgress> report) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => report(value); }
}
