using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Core;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ExtensionBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ExtBackup_" + Guid.NewGuid().ToString("N"));
    private readonly BraveBackupSelection[] _scope;
    private const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Orphan = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly byte[] Binary = { 0, 255, 128, 13, 10, 0, 5 };
    private string Target => Path.Combine(_root, "out", "archive.bravebackup");
    private string Profile => _scope[0].Profiles[0].ProfileDirectory;

    public ExtensionBackupTests()
    {
        _scope = Enumerable.Range(0, 2).Select(index =>
        {
            var data = Path.Combine(_root, "browser" + index);
            var profile = Path.Combine(data, "Default");
            Directory.CreateDirectory(profile);
            var bookmarks = Path.Combine(profile, "Bookmarks");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), bookmarks);
            File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");
            foreach (var version in new[] { "9.0", "10.0" })
            {
                var directory = Path.Combine(profile, "Extensions", Id, version + "_0");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "manifest.json"),
                    JsonSerializer.Serialize(new { name = "__MSG_name__", version, manifest_version = 3 }));
                File.WriteAllText(Path.Combine(directory, "script.js"), "// synthetic, never executed");
                Directory.CreateDirectory(Path.Combine(directory, "empty"));
            }

            var local = Path.Combine(profile, "Local Extension Settings", Id);
            Directory.CreateDirectory(local);
            File.WriteAllBytes(Path.Combine(local, "opaque-test.bin"), Binary);
            Directory.CreateDirectory(Path.Combine(profile, "Sync Extension Settings", Id));
            var orphan = Path.Combine(profile, "Local Extension Settings", Orphan);
            Directory.CreateDirectory(orphan);
            File.WriteAllBytes(Path.Combine(orphan, "retained-test.bin"), Binary);
            Directory.CreateDirectory(Path.Combine(profile, "IndexedDB"));
            File.WriteAllText(Path.Combine(profile, "IndexedDB", "excluded-test.bin"), "outside approved scope");
            return new BraveBackupSelection(
                new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), data),
                new[] { new BraveProfileInfo("Same fixture name", profile, bookmarks) });
        }).ToArray();
    }

    private Task<BackupResult> Create(string[]? categories = null, string? password = "fixture-key",
        string compression = "balanced",
        Func<ProcessPresence>? state = null, Func<string, Task<bool>>? consent = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken token = default) => BraveArchiveBackup.CreateAsync(_scope,
        categories ?? new[] { "extensions" },
        Target, password, compression, _scope.Select(item => item.Installation.UserDataDirectory).ToArray(), "fixture",
        _ => Task.FromResult(true), progress, token,
        new ExtensionBackupOptions
            { ObserveBrowserState = state ?? (() => ProcessPresence.Stopped), ConfirmUnencryptedData = consent });

    [Theory]
    [InlineData(false, "none")]
    [InlineData(false, "fast")]
    [InlineData(false, "balanced")]
    [InlineData(false, "high")]
    [InlineData(true, "none")]
    [InlineData(true, "fast")]
    [InlineData(true, "balanced")]
    [InlineData(true, "high")]
    public async Task AllVersionsBinaryStorageAndOrphanData_RoundTripWithoutSourceWrites(bool encrypted,
        string compression)
    {
        var original = SourceHashes();
        var consentCount = 0;
        var password = encrypted ? "fixture-key" : null;
        var result = await Create(password: password, compression: compression, consent: _ =>
        {
            consentCount++;
            return Task.FromResult(true);
        });
        Assert.Equal(encrypted ? 0 : 1, consentCount);
        Assert.Contains("packages: 2; versions: 4; storage-only identities: 2", result.ExtensionsSummary);
        Assert.DoesNotContain(Id, result.Report);
        Assert.DoesNotContain("__MSG_name__", result.Report);
        Assert.DoesNotContain(_root, result.Report);
        using var file = File.OpenRead(Target);
        var manifest = BraveSettingsBackup.ValidateArchive(file, password);
        Assert.Equal(3, manifest.FormatVersion);
        Assert.Equal(new[] { "extensions-v1" }, manifest.RequiredCapabilities);
        file.Position = 0;
        using var decoded = encrypted ? ArchiveEncryption.OpenReader(file, password!) : null;
        using var zip = new ZipArchive(decoded ?? file, ZipArchiveMode.Read, true);
        var inventories = zip.Entries.Where(entry => entry.FullName.EndsWith("inventory-v1.json")).ToArray();
        Assert.Equal(2, inventories.Length);
        foreach (var entry in inventories)
        {
            using var input = entry.Open();
            var inventory = JsonSerializer.Deserialize<ExtensionSnapshotInventory>(input, ArchiveManifest.JsonOptions)!;
            var package = inventory.Extensions.Single(item => item.Id == Id);
            Assert.Equal(new[] { "10.0", "9.0" }, package.Versions.Select(version => version.Version).ToArray());
            Assert.All(package.Versions, version => Assert.Equal("__MSG_name__", version.Name));
            Assert.Equal("present", package.LocalStorage);
            Assert.Equal("empty", package.SyncStorage);
            Assert.Contains("package/9.0_0/empty", package.Directories);
            var orphan = inventory.Extensions.Single(item => item.Id == Orphan);
            Assert.Empty(orphan.Versions);
            Assert.Equal("absent", orphan.SyncStorage);
        }

        foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith("test.bin")))
        {
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            input.CopyTo(bytes);
            Assert.Equal(Binary, bytes.ToArray());
        }

        Assert.DoesNotContain(zip.Entries, entry => entry.FullName.Contains("IndexedDB"));
        Assert.Equal(original.OrderBy(pair => pair.Key), SourceHashes().OrderBy(pair => pair.Key));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Theory]
    [InlineData("bookmarks")]
    [InlineData("settings")]
    [InlineData("both")]
    public async Task Extensions_CanBeCombinedWithExistingCategories(string other)
    {
        var categories = other == "both"
            ? new[] { "extensions", "settings", "bookmarks" }
            : new[] { "extensions", other };
        await Create(categories);
        using var file = File.OpenRead(Target);
        var manifest = BraveSettingsBackup.ValidateArchive(file, "fixture-key");
        Assert.Equal(3, manifest.FormatVersion);
        Assert.Equal(categories.Length, manifest.RequiredCapabilities.Length);
    }

    [Theory]
    [InlineData(ProcessPresence.Running)]
    [InlineData(ProcessPresence.Unknown)]
    public async Task RunningOrUncertainBrowser_BlocksBeforeAnyLocalOutput(ProcessPresence state)
    {
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create(state: () => state));
        Assert.StartsWith("extensions-browser-", failure.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Target)));
    }

    [Fact]
    public async Task UnencryptedData_RequiresFreshExplicitConfirmationForEveryAttempt()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(password: null));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Target)));
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(password: null, consent: path =>
        {
            Assert.Equal(Target, path);
            calls++;
            return Task.FromResult(false);
        }));
        Assert.Equal(1, calls);
        Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData("Verifying")]
    [InlineData("Finalizing")]
    public async Task BrowserRestartAfterCapture_NeverPublishes(string restartAt)
    {
        var state = ProcessPresence.Stopped;
        await Assert.ThrowsAsync<BackupOperationException>(() => Create(state: () => state,
            progress: new InlineProgress(value =>
            {
                if (value.Stage == restartAt) state = ProcessPresence.Running;
            })));
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("added")]
    [InlineData("deleted")]
    public async Task SourceChangesIncludingSameSizeAndRestoredMtime_AreDetected(string change)
    {
        var path = Path.Combine(Profile, "Local Extension Settings", Id, "opaque-test.bin");
        await Assert.ThrowsAsync<BackupOperationException>(() => Create(progress: new InlineProgress(value =>
        {
            if (value.Stage != "Finalizing") return;
            if (change == "added") File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "new.bin"), "changed");
            else if (change == "deleted") File.Delete(path);
            else
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                File.WriteAllBytes(path, new byte[Binary.Length]);
                File.SetLastWriteTimeUtc(path, stamp);
            }
        })));
        Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("wrong-version")]
    public async Task BadManifest_FailsEntireArchiveRatherThanFallingBack(string fault)
    {
        var path = Path.Combine(Profile, "Extensions", Id, "9.0_0", "manifest.json");
        if (fault == "missing") File.Delete(path);
        else
            File.WriteAllText(path,
                fault == "invalid" ? "{}" : "{\"name\":\"fixture\",\"version\":\"09.0\",\"manifest_version\":3}");
        await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task EmptySupportedTrees_ReportZeroAndDoNotRequireBookmarksOrSettings()
    {
        foreach (var profile in _scope.SelectMany(item => item.Profiles))
        {
            Directory.Delete(Path.Combine(profile.ProfileDirectory, "Extensions"), true);
            Directory.Delete(Path.Combine(profile.ProfileDirectory, "Local Extension Settings"), true);
            Directory.Delete(Path.Combine(profile.ProfileDirectory, "Sync Extension Settings"), true);
            File.Delete(profile.BookmarksPath);
            File.Delete(Path.Combine(profile.ProfileDirectory, "Preferences"));
        }

        var result = await Create();
        Assert.Contains("packages: 0; versions: 0", result.ExtensionsSummary);
        using var file = File.OpenRead(Target);
        Assert.Equal(3, BraveSettingsBackup.ValidateArchive(file, "fixture-key").FormatVersion);
    }

    [Fact]
    public async Task SymlinkAndOversizedRegularFiles_AreRejected()
    {
        var path = Path.Combine(Profile, "Local Extension Settings", Id, "opaque-test.bin");
        File.Delete(path);
        File.CreateSymbolicLink(path, Path.Combine(Profile, "Preferences"));
        await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.False(File.Exists(Target));
        File.Delete(path);
        using (var file = File.Create(path)) file.SetLength(ArchiveLimits.MaxBookmarkBytes + 1L);
        await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task Cancellation_PreservesExistingOutputAndCleansIncompleteArchive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "original");
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(progress: new InlineProgress(value =>
        {
            if (value.Stage == "Reading/writing extensions") cancel.Cancel();
        }), token: cancel.Token));
        Assert.Equal("original", File.ReadAllText(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Fact]
    public async Task ProcessObserverFailure_IsIndeterminateAndCannotAuthorizeCopying()
    {
        var error = await Assert.ThrowsAsync<BackupOperationException>(() => Create(state: () => throw new IOException("fixture probe failure")));
        Assert.Equal("extensions-browser-unknown", error.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Target)));
    }

    private Dictionary<string, string> SourceHashes() => _scope.SelectMany(item => item.Profiles)
        .SelectMany(profile => Directory.GetFiles(profile.ProfileDirectory, "*", SearchOption.AllDirectories))
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class InlineProgress(Action<BackupProgress> callback) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => callback(value);
    }
}