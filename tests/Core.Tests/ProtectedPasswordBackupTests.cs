using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ProtectedPasswordBackupTests : IDisposable
{
    private const string ArchivePassword = "synthetic-archive-secret";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ProtectedPasswords_" + Guid.NewGuid().ToString("N"));
    private readonly List<PasswordKeyMaterial> _keys = [];
    private BraveBackupSelection[] _scope = [];
    private string Destination => Path.Combine(_root, "output", "backup.bravebackup");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private void Prepare(string os = "linux", bool empty = false)
    {
        _scope = Enumerable.Range(0, 2).Select(index =>
        {
            var data = Path.Combine(_root, "browser" + index); var profile = Path.Combine(data, "Default");
            Directory.CreateDirectory(profile);
            var bookmarks = Path.Combine(profile, "Bookmarks");
            File.Copy(Path.Combine(Fixtures, "Bookmarks.sample.json"), bookmarks);
            File.Copy(Path.Combine(Fixtures, "Passwords", "LoginData.v43." + (empty ? "empty" : os) + ".sqlite"), Path.Combine(profile, "Login Data"));
            if (os == "windows") File.WriteAllText(Path.Combine(data, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
            var app = new BraveAppInfo(BraveAppChannel.Stable, "synthetic", Path.Combine(_root, "browser"), data);
            return new BraveBackupSelection(app, [new BraveProfileInfo("Synthetic", profile, bookmarks)]);
        }).ToArray();
    }
    private PasswordBackupOptions Options(string os = "linux", bool protect = true) => new()
    {
        ProtectPasswords = protect,
        GetProvider = (_, _) => os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
        ObserveBrowserState = () => ProcessPresence.Stopped,
        Unlock = (_, _, _) =>
        {
            using var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
            var key = new PasswordKeyMaterial(os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
                Convert.FromHexString(vector.RootElement.GetProperty(os + "KeyHex").GetString()!));
            _keys.Add(key); return key;
        }
    };
    private Task<BackupResult> Create(PasswordBackupOptions? options, string? password = ArchivePassword,
        string[]? categories = null, Func<string, Task<bool>>? overwrite = null, IProgress<BackupProgress>? progress = null,
        CancellationToken token = default) => BraveArchiveBackup.CreateAsync(_scope, categories ?? ["passwords"], Destination,
        password, "balanced", _scope.Select(s => s.Installation.UserDataDirectory).ToArray(), "synthetic",
        overwrite ?? (_ => Task.FromResult(false)), progress, token, new ExtensionBackupOptions(),
        new SessionBackupOptions(), new TabManagerPlusBackupOptions(), options);

    [Theory]
    [InlineData("windows", false)]
    [InlineData("linux", false)]
    [InlineData("linux", true)]
    public async Task WritesValidatedEncryptedV6ForEveryProfileWithoutPlaintextStaging(string os, bool mixed)
    {
        Prepare(os);
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        var result = await Create(Options(os), categories: mixed ? ["bookmarks", "passwords"] : ["passwords"]);
        using var input = File.OpenRead(Destination);
        Assert.True(ArchiveEncryption.IsEncrypted(input));
        var manifest = BraveSettingsBackup.ValidateArchive(input, ArchivePassword);
        Assert.Equal(6, manifest.FormatVersion);
        Assert.Equal(mixed ? new[] { "bookmarks-v1", "passwords-v1" } : new[] { "passwords-v1" }, manifest.RequiredCapabilities);
        using var decrypted = ArchiveEncryption.OpenReader(input, ArchivePassword);
        using var zip = new ZipArchive(decrypted, ZipArchiveMode.Read);
        foreach (var profile in manifest.Installations.SelectMany(i => i.Profiles))
        {
            var file = profile.Categories.Single(c => c.Id == "passwords").Files.Single();
            using var entry = zip.GetEntry(file.Path)!.Open();
            using var bytes = new MemoryStream(); entry.CopyTo(bytes);
            using var document = PasswordPayloadFormat.Decode(bytes.ToArray());
            var login = Assert.Single(Assert.Single(document.Stores).Logins);
            Assert.Equal("fixture-user", login.Username); Assert.Single(login.Notes);
        }
        Assert.Equal(2, _keys.Count);
        foreach (var key in _keys) Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], key));
        Assert.Contains("credentials: 2; notes: 2", result.PasswordsSummary);
        Assert.DoesNotContain("fixture-user", result.Report); Assert.DoesNotContain("fixture-password", result.Report);
        Assert.DoesNotContain("example.invalid", result.Report); Assert.DoesNotContain(ArchivePassword, result.Report);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
        Assert.False(File.ReadAllBytes(Destination).AsSpan().IndexOf("fixture-password"u8) >= 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ProtectedPasswordsRequireEncryptionBeforeAnyProviderAccess(string? password)
    {
        Prepare(); var calls = 0;
        var options = new PasswordBackupOptions { GetProvider = (_, _) => { calls++; throw new Exception("Must not resolve"); } };
        Assert.Equal("passwords-protection-required", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(options, password))).Code);
        Assert.Equal(0, calls); Assert.False(Directory.Exists(Path.GetDirectoryName(Destination)));
    }

    [Fact]
    public async Task UnprotectedChoiceOrMissingProviderIsNotSilentlyReinterpreted()
    {
        Prepare();
        Assert.Equal("passwords-mode-unsupported", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(Options(protect: false)))).Code);
        Assert.Equal("passwords-provider-required", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(new PasswordBackupOptions()))).Code);
        Assert.Equal("unsupported-category", (await Assert.ThrowsAsync<BackupOperationException>(() => Create(null))).Code);
        Assert.Empty(_keys);
    }

    [Fact]
    public async Task EmptyCaptureIsProtectedAndDoesNotUnlock()
    {
        Prepare(empty: true);
        var result = await Create(Options());
        Assert.Empty(_keys); Assert.Contains("credentials: 0", result.PasswordsSummary);
        using var input = File.OpenRead(Destination); Assert.Equal(6, BackupArchive.Validate(input, ArchivePassword).FormatVersion);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("source-change")]
    [InlineData("cancel")]
    [InlineData("destination-change")]
    public async Task FailurePreservesDestinationAndCleansEncryptedTemporary(string failure)
    {
        Prepare(); Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        File.WriteAllText(Destination, "synthetic-original");
        if (failure == "corrupt") File.WriteAllBytes(Path.Combine(_scope[1].Profiles[0].ProfileDirectory, "Login Data"), "corrupt"u8.ToArray());
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(stage =>
        {
            if (stage.Stage != "Verifying") return;
            if (failure == "source-change") File.AppendAllText(Path.Combine(_scope[0].Profiles[0].ProfileDirectory, "Login Data"), "changed");
            if (failure == "cancel") cancellation.Cancel();
            if (failure == "destination-change") File.WriteAllText(Destination, "synthetic-concurrent");
        });
        var error = await Record.ExceptionAsync(() => Create(Options(), overwrite: _ => Task.FromResult(true), progress: progress, token: cancellation.Token));
        Assert.NotNull(error);
        Assert.Equal(failure == "destination-change" ? "synthetic-concurrent" : "synthetic-original", File.ReadAllText(Destination));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
        foreach (var key in _keys) Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], key));
    }

    [Fact]
    public async Task DecliningOverwriteNeverUnlocksCredentials()
    {
        Prepare(); Directory.CreateDirectory(Path.GetDirectoryName(Destination)!); File.WriteAllText(Destination, "synthetic-original");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(Options()));
        Assert.Empty(_keys); Assert.Equal("synthetic-original", File.ReadAllText(Destination));
    }

    private PasswordBackupOptions RawOptions(Func<PasswordRawFallbackWarning, CancellationToken, Task<bool>> choice)
    {
        var original = Options();
        return new PasswordBackupOptions { GetProvider = original.GetProvider, ObserveBrowserState = original.ObserveBrowserState,
            Unlock = original.Unlock, ChooseRawPreservation = choice };
    }

    [Fact]
    public async Task ExplicitRawChoiceWritesMixedV7WithTruthfulStatusAndExactFiles()
    {
        Prepare();
        var profile = _scope[1].Profiles[0].ProfileDirectory;
        File.WriteAllBytes(Path.Combine(profile, "Login Data"), "synthetic-corrupt-password-file"u8.ToArray());
        File.WriteAllBytes(Path.Combine(profile, "Login Data-journal"), "synthetic-journal"u8.ToArray());
        File.WriteAllText(Path.Combine(profile, "Local State"), "must not include");
        File.WriteAllText(Path.Combine(profile, "Cookies"), "must not include");
        var calls = 0;
        var result = await Create(RawOptions((warning, _) =>
        { calls++; Assert.Equal(profile, warning.ProfileDirectory); return Task.FromResult(true); }));
        Assert.Equal(1, calls);
        using var input = File.OpenRead(Destination);
        var manifest = BraveSettingsBackup.ValidateArchive(input, ArchivePassword);
        Assert.Equal(7, manifest.FormatVersion); Assert.Equal(new[] { "passwords-v2" }, manifest.RequiredCapabilities);
        var categories = manifest.Installations.SelectMany(i => i.Profiles).SelectMany(p => p.Categories).ToArray();
        Assert.All(categories, c => Assert.Equal(2, c.Version));
        Assert.Contains(categories, c => c.Files.Any(f => f.Role == "portable-passwords"));
        var raw = Assert.Single(categories.Where(c => c.Files.Any(f => f.Role == "raw-password-index")));
        using var decrypted = ArchiveEncryption.OpenReader(input, ArchivePassword);
        using var zip = new ZipArchive(decrypted, ZipArchiveMode.Read);
        foreach (var descriptor in raw.Files.Where(f => f.Role == "native-password-file"))
        {
            using var stream = zip.GetEntry(descriptor.Path)!.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes);
            Assert.Equal(File.ReadAllBytes(Path.Combine(profile, descriptor.Path.Split('/').Last())), bytes.ToArray());
        }
        Assert.DoesNotContain(zip.Entries, entry => entry.Name is "Local State" or "Cookies");
        Assert.Contains("Raw/native preservation profiles: 1", result.PasswordsSummary);
        Assert.Contains("Raw credential counts are unknown", result.Report);
        Assert.DoesNotContain("synthetic-corrupt-password-file", result.Report);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RawDeclineOrCancellationPreservesDestination(bool cancel)
    {
        Prepare(); File.WriteAllBytes(Path.Combine(_scope[0].Profiles[0].ProfileDirectory, "Login Data"), "corrupt"u8.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!); File.WriteAllText(Destination, "synthetic-original");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(RawOptions((_, _) =>
        { if (cancel) cancellation.Cancel(); return Task.FromResult(cancel); }), overwrite: _ => Task.FromResult(true), token: cancellation.Token));
        Assert.Equal("synthetic-original", File.ReadAllText(Destination)); Assert.Single(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    [Fact]
    public async Task RawSourceChangeAfterCaptureBlocksPublication()
    {
        Prepare(); var source = Path.Combine(_scope[0].Profiles[0].ProfileDirectory, "Login Data"); File.WriteAllBytes(source, "corrupt"u8.ToArray());
        await Assert.ThrowsAsync<BackupOperationException>(() => Create(RawOptions((_, _) => Task.FromResult(true)),
            progress: new InlineProgress(stage => { if (stage.Stage == "Verifying") File.AppendAllText(source, "changed"); })));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!));
    }

    private sealed class InlineProgress(Action<BackupProgress> callback) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => callback(value); }
    public void Dispose() { foreach (var key in _keys) key.Dispose(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
