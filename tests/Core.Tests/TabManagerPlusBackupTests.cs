using System.IO.Compression;
using System.Security.Cryptography;
using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class TabManagerPlusBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TmpArchive_" + Guid.NewGuid().ToString("N"));
    private readonly BraveBackupSelection[] _scope;
    private string Target => Path.Combine(_root, "output", "backup.bravebackup");
    private string Profile => _scope[0].Profiles[0].ProfileDirectory;

    public TabManagerPlusBackupTests()
    {
        _scope = Enumerable.Range(0, 2).Select(index =>
        {
            var data = Path.Combine(_root, "browser" + index);
            var profile = Path.Combine(data, "Default");
            PrepareProfile(profile);
            return new BraveBackupSelection(new BraveAppInfo(BraveAppChannel.Stable, "fixture",
                Path.Combine(_root, "brave"), data), new[] { new BraveProfileInfo("Synthetic profile", profile,
                Path.Combine(profile, "Bookmarks")) });
        }).ToArray();
    }

    internal static void PrepareProfile(string profile)
    {
        var package = Path.Combine(profile, "Extensions", SavedWindows.OfficialExtensionId, "7.0.0_0");
        var storage = Path.Combine(profile, "Local Extension Settings", SavedWindows.OfficialExtensionId);
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(storage);
        File.WriteAllText(Path.Combine(package, "manifest.json"), "{\"name\":\"Fixture\",\"version\":\"7.0.0\",\"manifest_version\":3}");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TabManagerPlus", "storage", "table-and-wal")))
            File.Copy(file, Path.Combine(storage, Path.GetFileName(file)), overwrite: true);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), Path.Combine(profile, "Bookmarks"), overwrite: true);
        File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");
        Directory.CreateDirectory(Path.Combine(profile, "Sessions"));
        File.WriteAllBytes(Path.Combine(profile, "Sessions", "Session_10"), BraveSessionDecoderTests.Fixture());
    }

    private Task<BackupResult> Create(string? password = "fixture", string compression = "balanced",
        string[]? categories = null, Func<string, Task<bool>>? consent = null,
        Func<ProcessPresence>? state = null, IProgress<BackupProgress>? progress = null, CancellationToken token = default) =>
        BraveArchiveBackup.CreateAsync(_scope, categories ?? new[] { "tabmanagerplus" }, Target, password, compression,
            _scope.Select(item => item.Installation.UserDataDirectory).ToArray(), "fixture", _ => Task.FromResult(true), progress, token,
            new ExtensionBackupOptions { ObserveBrowserState = state ?? (() => ProcessPresence.Stopped), ConfirmUnencryptedData = _ => Task.FromResult(true) },
            new SessionBackupOptions(), new TabManagerPlusBackupOptions
                { ObserveBrowserState = state ?? (() => ProcessPresence.Stopped), ConfirmUnencryptedData = consent }, null);

    [Theory]
    [InlineData(false, "none")]
    [InlineData(false, "fast")]
    [InlineData(false, "balanced")]
    [InlineData(false, "high")]
    [InlineData(true, "none")]
    [InlineData(true, "fast")]
    [InlineData(true, "balanced")]
    [InlineData(true, "high")]
    public async Task MixedCategoriesRoundTripAndPreserveSources(bool encrypted, string compression)
    {
        var before = SourceHashes();
        var prompts = 0;
        var password = encrypted ? "fixture" : null;
        var result = await Create(password, compression,
            new[] { "sessions", "tabmanagerplus", "extensions", "settings", "bookmarks" }, path =>
            { prompts++; Assert.Equal(Target, path); return Task.FromResult(true); });
        Assert.Equal(encrypted ? 0 : 1, prompts);
        Assert.Contains("profiles: 2; windows: 2; tabs: 2", result.TabManagerPlusSummary);
        Assert.Contains("Restore is not available", result.TabManagerPlusSummary);
        Assert.DoesNotContain("Synthetic work", result.Report);
        Assert.DoesNotContain("example.invalid", result.Report);
        Assert.DoesNotContain(_root, result.Report);
        using var input = File.OpenRead(Target);
        var manifest = BraveSettingsBackup.ValidateArchive(input, password);
        Assert.Equal(5, manifest.FormatVersion);
        Assert.Equal(new[] { "bookmarks-v1", "settings-v1", "extensions-v1", "sessions-v1", "tabmanagerplus-v1" }, manifest.RequiredCapabilities);
        input.Position = 0;
        using var plain = encrypted ? ArchiveEncryption.OpenReader(input, password!) : null;
        using var zip = new ZipArchive(plain ?? input, ZipArchiveMode.Read, true);
        foreach (var category in manifest.Installations.SelectMany(item => item.Profiles).SelectMany(item => item.Categories).Where(item => item.Id == "tabmanagerplus"))
        {
            var file = Assert.Single(category.Files);
            Assert.Equal("portable-extension-windows", file.Role);
            using var memory = new MemoryStream();
            using (var payload = zip.GetEntry(file.Path)!.Open()) payload.CopyTo(memory);
            var parsed = SavedWindowPayload.Parse(memory.ToArray());
            Assert.Equal("https://example.invalid/current", Assert.Single(Assert.Single(parsed.Data.Windows).Tabs).Url);
        }
        Assert.Equal(before.OrderBy(pair => pair.Key), SourceHashes().OrderBy(pair => pair.Key));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Fact]
    public async Task SavedWindowsOnlyDoesNotRequireOtherCategorySources()
    {
        foreach (var profile in _scope.SelectMany(item => item.Profiles))
        {
            File.Delete(profile.BookmarksPath);
            File.Delete(Path.Combine(profile.ProfileDirectory, "Preferences"));
            Directory.Delete(Path.Combine(profile.ProfileDirectory, "Sessions"), true);
        }
        await Create();
        using var input = File.OpenRead(Target);
        Assert.Equal(new[] { "tabmanagerplus-v1" }, BraveSettingsBackup.ValidateArchive(input, "fixture").RequiredCapabilities);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoPlaintextSaveWithoutExplicitPerOperationConsent(bool callback)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(password: null,
            consent: callback ? _ => Task.FromResult(false) : null));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Target)));
    }

    [Theory]
    [InlineData("Reading/writing saved windows")]
    [InlineData("Verifying")]
    [InlineData("Finalizing")]
    public async Task CancellationPreservesExistingDestinationAndCleansTemporaryFile(string stage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "existing");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(token: cancel.Token,
            progress: new Immediate(value => { if (value.Stage == stage) cancel.Cancel(); })));
        Assert.Equal("existing", File.ReadAllText(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Fact]
    public async Task FinalSourceHashCheckRejectsSameSizeChangeWithRestoredTimestamp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "existing");
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create(progress: new Immediate(value =>
        {
            if (value.Stage != "Finalizing") return;
            var path = Path.Combine(Profile, "Local Extension Settings", SavedWindows.OfficialExtensionId, "000004.log");
            var timestamp = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, timestamp);
        })));
        Assert.Equal("tabmanagerplus-source-changed", failure.Code);
        Assert.Equal("existing", File.ReadAllText(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Fact]
    public async Task BrowserStartingBeforePublicationBlocksSave()
    {
        var state = ProcessPresence.Stopped;
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create(state: () => state,
            progress: new Immediate(value => { if (value.Stage == "Finalizing") state = ProcessPresence.Running; })));
        Assert.Equal("tabmanagerplus-browser-not-stopped", failure.Code);
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Fact]
    public async Task UnsupportedSecondProfileNeverPublishesPartialBackup()
    {
        var path = Path.Combine(_scope[1].Profiles[0].ProfileDirectory, "Extensions", SavedWindows.OfficialExtensionId);
        Directory.Delete(path, true);
        await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    private Dictionary<string, string> SourceHashes() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .Where(path => !path.StartsWith(Path.Combine(_root, "output"), StringComparison.Ordinal))
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    private sealed class Immediate(Action<BackupProgress> report) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => report(value); }
    public void Dispose() => Directory.Delete(_root, true);
}
