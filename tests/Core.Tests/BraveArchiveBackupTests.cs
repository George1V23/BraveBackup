using System.IO.Compression;
using System.Text.Json;
using BraveBackup.Core;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BraveArchiveBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BraveArchiveTests_" + Guid.NewGuid().ToString("N"));
    private readonly BraveBackupSelection[] _selections;
    private string Destination => Path.Combine(_root, "output", "backup.bravebackup");

    public BraveArchiveBackupTests()
    {
        _selections = Enumerable.Range(0, 2).Select(index =>
        {
            var data = Path.Combine(_root, "browser-" + index);
            var directory = Path.Combine(data, "Default"); Directory.CreateDirectory(directory);
            var source = Path.Combine(directory, "Bookmarks");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), source);
            var app = new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "browser"), data);
            return new BraveBackupSelection(app, new[] { new BraveProfileInfo("Same name", directory, source) });
        }).ToArray();
    }

    private Task<BackupResult> Create(string? password = null, string compression = "balanced",
        Func<string, Task<bool>>? confirm = null, IProgress<BackupProgress>? progress = null,
        CancellationToken token = default, IReadOnlyList<string>? categories = null, string? destination = null) =>
        BraveArchiveBackup.CreateAsync(_selections, categories ?? new[] { "bookmarks" }, destination ?? Destination,
            password, compression, _selections.Select(item => item.Installation.UserDataDirectory).ToArray(),
            "fixture", confirm ?? (_ => Task.FromResult(false)), progress, token);

    [Theory]
    [InlineData(false, "none")]
    [InlineData(false, "fast")]
    [InlineData(false, "balanced")]
    [InlineData(false, "high")]
    [InlineData(true, "none")]
    [InlineData(true, "fast")]
    [InlineData(true, "balanced")]
    [InlineData(true, "high")]
    public async Task Archive_RoundTripsDuplicateNamesAndKeepsNativeAndLegacyPayloads(bool encrypted, string compression)
    {
        var password = encrypted ? "fixture-archive-key" : null;
        var original = File.ReadAllBytes(_selections[0].Profiles[0].BookmarksPath);
        var stages = new List<string>();
        var result = await Create(password, compression, progress: new InlineProgress(value => stages.Add(value.Stage)));
        Assert.Equal(Destination, result.Destination);
        Assert.Equal(new FileInfo(Destination).Length, result.Bytes);
        Assert.Contains("Succeeded", result.Report);
        Assert.DoesNotContain("Same name", result.Report);
        Assert.DoesNotContain(_root, result.Report);
        Assert.DoesNotContain("fixture-archive-key", result.Report);
        Assert.Contains("Reading", stages); Assert.Contains("Verifying", stages); Assert.Contains("Finalizing", stages);
        using var file = File.OpenRead(Destination);
        Assert.Equal(encrypted, ArchiveEncryption.IsEncrypted(file));
        var manifest = BackupArchive.Validate(file, password);
        Assert.Equal(2, manifest.Installations.Length);
        Assert.NotEqual(manifest.Installations[0].Id, manifest.Installations[1].Id);
        var json = JsonSerializer.Serialize(manifest, ArchiveManifest.JsonOptions);
        Assert.DoesNotContain(_root, json);
        file.Position = 0;
        using var decrypted = encrypted ? ArchiveEncryption.OpenReader(file, password!) : null;
        using var zip = new ZipArchive(decrypted ?? file, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries.Where(item => item.FullName.EndsWith("source.json")))
        {
            using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            Assert.Equal(original, bytes.ToArray());
        }
        foreach (var entry in zip.Entries.Where(item => item.FullName.EndsWith("portable-v1.json")))
        {
            using var input = entry.Open();
            var model = JsonSerializer.Deserialize<BackupModel>(input)!;
            Assert.Equal(1, model.SchemaVersion);
            Assert.Equal("https://example.test/", model.Bookmarks[0].Children[0].Url);
        }
        foreach (var profile in _selections.SelectMany(item => item.Profiles)) Assert.Equal(original, File.ReadAllBytes(profile.BookmarksPath));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Destination));
    }

    [Theory]
    [InlineData("Validating")] [InlineData("Reading")] [InlineData("Writing")]
    [InlineData("Verifying")] [InlineData("Finalizing")]
    public async Task Cancellation_PreservesExistingDestinationAndRemovesIncompleteFile(string stage)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        await File.WriteAllTextAsync(Destination, "existing backup");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create("fixture", confirm: _ => Task.FromResult(true),
            progress: new InlineProgress(value => { if (value.Stage == stage) cancellation.Cancel(); }), token: cancellation.Token));
        Assert.Equal("existing backup", File.ReadAllText(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    [Fact]
    public async Task OverwriteRequiresConsentAndDetectsChangesBeforePublication()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        await File.WriteAllTextAsync(Destination, "original");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create());
        Assert.Equal("original", File.ReadAllText(Destination));
        var changed = await Assert.ThrowsAsync<BackupOperationException>(() => Create(confirm: _ => Task.FromResult(true),
            progress: new InlineProgress(value => { if (value.Stage == "Finalizing") File.WriteAllText(Destination, "changed"); })));
        Assert.Equal("destination-changed", changed.Code);
        Assert.Equal("changed", File.ReadAllText(Destination));
        var result = await Create(confirm: _ => Task.FromResult(true));
        Assert.True(result.Bytes > 0);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    [Theory]
    [InlineData("passwords")] [InlineData("sessions")] [InlineData("unknown")]
    [InlineData("extensions")] [InlineData("tabmanagerplus")]
    public async Task UnsupportedCategory_NeverSilentlyOmitsSelectedData(string category)
    {
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create(categories: new[] { "bookmarks", category }));
        Assert.Equal("unsupported-category", failure.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(Destination)));
    }

    [Theory]
    [InlineData("missing")] [InlineData("invalid")] [InlineData("empty")]
    public async Task UnreadableSource_BlocksTheEntireArchive(string fault)
    {
        var path = _selections[1].Profiles[0].BookmarksPath;
        if (fault == "missing") File.Delete(path);
        else File.WriteAllText(path, fault == "empty" ? "" : "{}");
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.Equal("source-unreadable", failure.Code);
        Assert.Contains("Profile 2 of 2", failure.Message);
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    [Fact]
    public async Task ValidEmptyBookmarksAndBomArePreserved()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.empty-roots.json"));
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray();
        foreach (var profile in _selections.SelectMany(item => item.Profiles)) File.WriteAllBytes(profile.BookmarksPath, withBom);
        await Create();
        using var file = File.OpenRead(Destination); BackupArchive.Validate(file);
        foreach (var profile in _selections.SelectMany(item => item.Profiles)) Assert.Equal(withBom, File.ReadAllBytes(profile.BookmarksPath));
    }

    [Fact]
    public async Task LiveProfileDestination_IsRejectedWithoutAnySourceWrite()
    {
        var target = Path.Combine(_selections[0].Profiles[0].ProfileDirectory, "backup.bravebackup");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(destination: target));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task VerificationFailure_DiscardsCorruptedTemporaryOutput()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => Create(progress: new InlineProgress(value =>
        {
            if (value.Stage == "Verifying")
            {
                var path = Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp").Single();
                File.WriteAllBytes(path, new byte[] { 0, 1, 2 });
            }
        })));
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    [Fact]
    public async Task CleanupFailure_IsReportedAndNeverPublishesOutput()
    {
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create(progress: new InlineProgress(value =>
        {
            if (value.Stage != "Verifying") return;
            var temporary = Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp").Single();
            File.Delete(temporary);
            Directory.CreateDirectory(temporary);
        })));
        Assert.Equal("cleanup-failed", failure.Code);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task DestinationIoFailure_LeavesSourceAndExistingFilesUntouched()
    {
        var blocker = Path.Combine(_root, "output");
        File.WriteAllText(blocker, "existing ordinary file");
        await Assert.ThrowsAnyAsync<IOException>(() => Create());
        Assert.Equal("existing ordinary file", File.ReadAllText(blocker));
        Assert.All(_selections, selection => Assert.True(File.Exists(selection.Profiles[0].BookmarksPath)));
    }

    [Fact]
    public async Task OversizedSource_IsRejectedWithoutReadingItsContent()
    {
        using (var source = File.OpenWrite(_selections[0].Profiles[0].BookmarksPath))
            source.SetLength(ArchiveLimits.MaxBookmarkBytes + 1L);
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.Equal("source-size", failure.Code);
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    [Theory]
    [InlineData(false, false, "none")] [InlineData(false, false, "fast")]
    [InlineData(false, false, "balanced")] [InlineData(false, false, "high")]
    [InlineData(true, false, "none")] [InlineData(true, false, "fast")]
    [InlineData(true, false, "balanced")] [InlineData(true, false, "high")]
    [InlineData(false, true, "none")] [InlineData(false, true, "fast")]
    [InlineData(false, true, "balanced")] [InlineData(false, true, "high")]
    [InlineData(true, true, "none")] [InlineData(true, true, "fast")]
    [InlineData(true, true, "balanced")] [InlineData(true, true, "high")]
    public async Task SettingsArchives_RoundTripSupportedValuesAcrossScopesAndModes(bool encrypted, bool bookmarks, string compression)
    {
        var prefs = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Preferences.settings.json"));
        foreach (var profile in _selections.SelectMany(item => item.Profiles))
        {
            File.WriteAllBytes(Path.Combine(profile.ProfileDirectory, "Preferences"), prefs);
            if (!bookmarks) File.Delete(profile.BookmarksPath);
        }
        var categories = bookmarks ? new[] { "settings", "bookmarks" } : new[] { "settings" };
        var password = encrypted ? "settings-fixture-secret" : null;
        var result = await Create(password, compression, categories: categories);
        Assert.Contains("Stored supported settings: 14; absent keys: 0", result.SettingsSummary);
        Assert.DoesNotContain("home.example", result.Report);
        using var source = File.OpenRead(Destination);
        var manifest = BraveSettingsBackup.ValidateArchive(source, password);
        Assert.Equal(2, manifest.FormatVersion);
        Assert.Equal(bookmarks ? new[] { "bookmarks-v1", "settings-v1" } : new[] { "settings-v1" }, manifest.RequiredCapabilities);
        Assert.All(manifest.Installations.SelectMany(item => item.Profiles), profile =>
            Assert.Equal(bookmarks ? 2 : 1, profile.Categories.Length));
        source.Position = 0;
        using var decrypted = encrypted ? ArchiveEncryption.OpenReader(source, password!) : null;
        using var zip = new ZipArchive(decrypted ?? source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries.Where(entry => entry.FullName.EndsWith("allowlisted-v1.json")).ToArray();
        Assert.Equal(2, entries.Length);
        foreach (var entry in entries)
        {
            using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            Assert.Equal(7, BraveSettingsBackup.ValidatePayload(bytes.ToArray()));
            Assert.DoesNotContain("unrelated", System.Text.Encoding.UTF8.GetString(bytes.ToArray()));
        }
        foreach (var profile in _selections.SelectMany(item => item.Profiles))
            Assert.Equal(prefs, File.ReadAllBytes(Path.Combine(profile.ProfileDirectory, "Preferences")));
    }

    [Theory]
    [InlineData("missing")] [InlineData("broken")] [InlineData("null-parent")] [InlineData("bad-type")]
    public async Task SettingsFailure_PreservesExistingDestinationAndNeverSkipsAProfile(string fault)
    {
        foreach (var profile in _selections.SelectMany(item => item.Profiles))
            File.WriteAllText(Path.Combine(profile.ProfileDirectory, "Preferences"), "{}");
        var bad = Path.Combine(_selections[1].Profiles[0].ProfileDirectory, "Preferences");
        if (fault == "missing") File.Delete(bad);
        else File.WriteAllText(bad, fault switch
        { "broken" => "{bad", "null-parent" => "{\"session\":null}", _ => "{\"homepage\":true}" });
        Directory.CreateDirectory(Path.GetDirectoryName(Destination)!);
        File.WriteAllText(Destination, "original");
        var failure = await Assert.ThrowsAsync<BackupOperationException>(() => Create("fixture", categories: new[] { "settings" },
            confirm: _ => Task.FromResult(true)));
        Assert.Contains("Profile 2 of 2", failure.Message);
        Assert.Equal("original", File.ReadAllText(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    [Fact]
    public async Task NoStoredSettings_ReportsZeroWithoutClaimingCompleteBrowserSettings()
    {
        foreach (var profile in _selections.SelectMany(item => item.Profiles))
            File.WriteAllText(Path.Combine(profile.ProfileDirectory, "Preferences"), "{}");
        var result = await Create(categories: new[] { "settings" });
        Assert.Contains("Stored supported settings: 0; absent keys: 14", result.SettingsSummary);
        Assert.Contains("scope order): 0, 0", result.Report);
    }

    [Theory]
    [InlineData("Reading settings")] [InlineData("Writing settings")] [InlineData("Verifying")]
    public async Task SettingsCancellation_CleansUpAndDoesNotPublish(string stage)
    {
        foreach (var profile in _selections.SelectMany(item => item.Profiles))
            File.WriteAllText(Path.Combine(profile.ProfileDirectory, "Preferences"), "{}");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create("fixture", categories: new[] { "settings" },
            progress: new InlineProgress(value => { if (value.Stage == stage) cancellation.Cancel(); }), token: cancellation.Token));
        Assert.False(File.Exists(Destination));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Destination)!, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_root, true);
    private sealed class InlineProgress(Action<BackupProgress> action) : IProgress<BackupProgress>
    { public void Report(BackupProgress value) => action(value); }
}
