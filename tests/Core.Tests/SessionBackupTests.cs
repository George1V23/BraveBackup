using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BraveBackup.Core;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class SessionBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SessionsBackup_" + Guid.NewGuid().ToString("N"));
    private readonly BraveBackupSelection[] _scope;
    private string Target => Path.Combine(_root, "out", "backup.bravebackup");
    private string Profile => _scope[0].Profiles[0].ProfileDirectory;

    public SessionBackupTests()
    {
        _scope = Enumerable.Range(0, 2).Select(index =>
        {
            var data = Path.Combine(_root, "browser" + index);
            var profile = Path.Combine(data, "Default");
            Directory.CreateDirectory(Path.Combine(profile, "Sessions"));
            File.WriteAllBytes(Path.Combine(profile, "Sessions", "Session_10"), BraveSessionDecoderTests.Fixture());
            File.WriteAllBytes(Path.Combine(profile, "Sessions", "Session_9"),
                BraveSessionDecoderTests.Fixture("Session.empty-v3.bin"));
            File.WriteAllBytes(Path.Combine(profile, "Sessions", "Tabs_10"), new byte[] { 0, 255, 1 });
            File.WriteAllText(Path.Combine(profile, "Last Tabs"), "synthetic raw legacy marker");
            var bookmarks = Path.Combine(profile, "Bookmarks");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), bookmarks);
            File.WriteAllText(Path.Combine(profile, "Preferences"), "{}");
            return new BraveBackupSelection(
                new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), data),
                new[] { new BraveProfileInfo("Synthetic", profile, bookmarks) });
        }).ToArray();
    }

    private Task<BackupResult> Create(bool native = false, string? password = "fixture",
        string compression = "balanced",
        string[]? categories = null, Func<string, Task<bool>>? consent = null,
        IProgress<BackupProgress>? progress = null,
        CancellationToken token = default, ProcessPresence browser = ProcessPresence.Stopped) =>
        BraveArchiveBackup.CreateAsync(_scope, categories ?? new[] { "sessions" }, Target, password, compression,
            _scope.Select(item => item.Installation.UserDataDirectory).ToArray(), "fixture", _ => Task.FromResult(true),
            progress, token,
            new ExtensionBackupOptions
                { ObserveBrowserState = () => browser, ConfirmUnencryptedData = _ => Task.FromResult(true) },
            new SessionBackupOptions { IncludeNativeFiles = native, ConfirmUnencryptedNativeFiles = consent });

    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(false, false, "fast")]
    [InlineData(false, false, "balanced")]
    [InlineData(false, false, "high")]
    [InlineData(false, true, "none")]
    [InlineData(false, true, "fast")]
    [InlineData(false, true, "balanced")]
    [InlineData(false, true, "high")]
    [InlineData(true, false, "none")]
    [InlineData(true, false, "fast")]
    [InlineData(true, false, "balanced")]
    [InlineData(true, false, "high")]
    [InlineData(true, true, "none")]
    [InlineData(true, true, "fast")]
    [InlineData(true, true, "balanced")]
    [InlineData(true, true, "high")]
    public async Task Sessions_RoundTripNewestNumericSourceAndOptionalRawFiles(bool encrypted, bool native,
        string compression)
    {
        var original = Hashes();
        var password = encrypted ? "fixture" : null;
        var calls = 0;
        var result = await Create(native, password, compression, consent: _ =>
        {
            calls++;
            return Task.FromResult(true);
        }, browser: ProcessPresence.Running);
        Assert.Equal(!encrypted && native ? 1 : 0, calls);
        Assert.Contains("windows: 4; tabs: 6; groups: 2", result.SessionsSummary);
        Assert.DoesNotContain("selected.example", result.Report);
        Assert.DoesNotContain("Group", result.Report.Replace("Groups without", ""));
        using var input = File.OpenRead(Target);
        var manifest = BraveSettingsBackup.ValidateArchive(input, password);
        Assert.Equal(4, manifest.FormatVersion);
        Assert.Equal(new[] { "sessions-v1" }, manifest.RequiredCapabilities);
        input.Position = 0;
        using var decoded = encrypted ? ArchiveEncryption.OpenReader(input, password!) : null;
        using var zip = new ZipArchive(decoded ?? input, ZipArchiveMode.Read, true);
        foreach (var category in manifest.Installations.SelectMany(item => item.Profiles)
                     .SelectMany(item => item.Categories))
        {
            var portable = category.Files.Single(file => file.Role == "portable-session");
            using var document = SessionArchiveFormat.ReadJson(zip, portable, CancellationToken.None);
            var model = PortableSessionFormat.Parse(document.RootElement);
            Assert.Equal("Sessions/Session_10", model.Source.Path);
            Assert.Equal(native, model.IncludeNativeFiles);
            var text = document.RootElement.GetRawText();
            Assert.DoesNotContain("excluded-page-state", text);
            Assert.DoesNotContain("old.example", text);
            Assert.Equal(native ? 4 : 0, category.Files.Count(file => file.Role == "native-session-file"));
            if (native)
            {
                var raw = category.Files.Single(file =>
                    file.Role == "native-session-file" && file.Path.EndsWith("Sessions/Session_10"));
                using var stream = zip.GetEntry(raw.Path)!.Open();
                using var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                Assert.Equal(BraveSessionDecoderTests.Fixture(), bytes.ToArray());
            }
        }

        Assert.Equal(original.OrderBy(pair => pair.Key), Hashes().OrderBy(pair => pair.Key));
    }

    [Theory]
    [InlineData("bookmarks")]
    [InlineData("settings")]
    [InlineData("extensions")]
    [InlineData("all")]
    public async Task MixedCategories_WriteV4AndRetainExtensionClosedPolicy(string choice)
    {
        var categories = choice == "all"
            ? new[] { "sessions", "extensions", "settings", "bookmarks" }
            : new[] { "sessions", choice };
        if (categories.Contains("extensions"))
        {
            var failure = await Assert.ThrowsAsync<BackupOperationException>(() =>
                Create(categories: categories, browser: ProcessPresence.Running));
            Assert.Equal("extensions-browser-running", failure.Code);
            Assert.False(File.Exists(Target));
        }

        await Create(categories: categories);
        using var source = File.OpenRead(Target);
        Assert.Equal(categories.Length,
            BraveSettingsBackup.ValidateArchive(source, "fixture").RequiredCapabilities.Length);
    }

    [Fact]
    public async Task NativePlaintext_RequiresFreshConsentAndDeclineCreatesNothing()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(true, null));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Target)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(true, null, consent: _ => Task.FromResult(false)));
        Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData("encrypted-directory")]
    [InlineData("encrypted-header")]
    [InlineData("unknown-command")]
    [InlineData("truncated")]
    [InlineData("missing")]
    [InlineData("legacy-only")]
    public async Task UnsupportedNewestSource_DoesNotFallBackToOlderPlaintext(string fault)
    {
        var source = Path.Combine(Profile, "Sessions", "Session_10");
        switch (fault)
        {
            case "encrypted-directory":
                Directory.CreateDirectory(Path.Combine(Profile, "Sessions_Encrypted"));
                File.WriteAllText(Path.Combine(Profile, "Sessions_Encrypted", "Session_11"), "opaque");
                break;
            case "encrypted-header":
                File.WriteAllBytes(source, BraveSessionDecoderTests.Fixture("Session.encrypted-header.bin")); break;
            case "unknown-command":
                File.WriteAllBytes(source, BraveSessionDecoderTests.Fixture("Session.unknown-command.bin")); break;
            case "truncated":
                File.WriteAllBytes(source, BraveSessionDecoderTests.Fixture("Session.truncated.bin")); break;
            default:
                File.Delete(source);
                File.Delete(Path.Combine(Profile, "Sessions", "Session_9"));
                if (fault == "legacy-only")
                    File.WriteAllBytes(Path.Combine(Profile, "Last Session"), BraveSessionDecoderTests.Fixture());
                break;
        }

        await Assert.ThrowsAsync<BackupOperationException>(() => Create(true));
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    [Theory]
    [InlineData("same-size")]
    [InlineData("new-source")]
    public async Task SourceChangesBeforePublication_PreserveExistingDestination(string kind)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Target)!);
        File.WriteAllText(Target, "original");
        await Assert.ThrowsAsync<BackupOperationException>(() => Create(progress: new InlineProgress(value =>
        {
            if (value.Stage != "Finalizing") return;
            var source = Path.Combine(Profile, "Sessions", "Session_10");
            if (kind == "new-source")
                File.WriteAllBytes(Path.Combine(Profile, "Sessions", "Session_11"), BraveSessionDecoderTests.Fixture());
            else
            {
                var stamp = File.GetLastWriteTimeUtc(source);
                var bytes = File.ReadAllBytes(source);
                bytes[^1] ^= 1;
                File.WriteAllBytes(source, bytes);
                File.SetLastWriteTimeUtc(source, stamp);
            }
        })));
        Assert.Equal("original", File.ReadAllText(Target));
    }

    [Fact]
    public async Task SelectedSessions_RequireNoBookmarksPreferencesOrStoppedBrowser()
    {
        foreach (var profile in _scope.SelectMany(item => item.Profiles))
        {
            File.Delete(profile.BookmarksPath);
            File.Delete(Path.Combine(profile.ProfileDirectory, "Preferences"));
        }

        await Create(browser: ProcessPresence.Unknown);
        Assert.True(File.Exists(Target));
    }

    [Fact]
    public async Task ValidEmptySnapshot_IsReportedAsPersistedZero()
    {
        foreach (var profile in _scope.SelectMany(item => item.Profiles))
            File.WriteAllBytes(Path.Combine(profile.ProfileDirectory, "Sessions", "Session_10"),
                BraveSessionDecoderTests.Fixture("Session.empty-v3.bin"));
        Assert.Contains("windows: 0; tabs: 0; groups: 0", (await Create()).SessionsSummary);
    }

    [Fact]
    public async Task LinksAndOversizedSources_AreRejected()
    {
        var source = Path.Combine(Profile, "Sessions", "Session_10");
        File.Delete(source);
        File.CreateSymbolicLink(source, _scope[0].Profiles[0].BookmarksPath);
        await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        File.Delete(source);
        using (var file = File.Create(source)) file.SetLength(ArchiveLimits.MaxBookmarkBytes + 1L);
        await Assert.ThrowsAsync<BackupOperationException>(() => Create());
        Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData("Reading/writing persisted sessions")]
    [InlineData("Verifying")]
    [InlineData("Finalizing")]
    public async Task Cancellation_DiscardsIncompleteOutput(string stage)
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(true, progress: new InlineProgress(value =>
        {
            if (value.Stage == stage) cancellation.Cancel();
        }), token: cancellation.Token));
        Assert.False(File.Exists(Target));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Target)!, "*.tmp"));
    }

    private Dictionary<string, string> Hashes() => _scope.SelectMany(item => item.Profiles)
        .SelectMany(profile => Directory.GetFiles(profile.ProfileDirectory, "*", SearchOption.AllDirectories))
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class InlineProgress(Action<BackupProgress> callback) : IProgress<BackupProgress>
    {
        public void Report(BackupProgress value) => callback(value);
    }
}