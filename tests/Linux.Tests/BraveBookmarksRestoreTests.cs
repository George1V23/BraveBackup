using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>End-to-end bookmarks-only restore from a real synthetic archive into synthetic destination profiles.</summary>
public sealed class BraveBookmarksRestoreTests : IDisposable
{
    private const string SourceJson = """{"checksum":"","roots":{"bookmark_bar":{"children":[{"date_added":"1","guid":"11111111-1111-4111-8111-111111111111","id":"5","name":"Synthetic","type":"url","url":"https://example.invalid/"}],"date_added":"1","date_modified":"1","guid":"00000000-4000-4000-a000-000000000002","id":"1","name":"Bookmarks bar","type":"folder"}},"version":1}""";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BookmarksRestore_" + Guid.NewGuid().ToString("N"));
    private readonly string _recovery, _archive;
    private readonly byte[] _sourceBytes = System.Text.Encoding.UTF8.GetBytes(SourceJson);
    private Guid _sourceProfile;

    public BraveBookmarksRestoreTests()
    {
        _recovery = Path.Combine(_root, "recovery"); Directory.CreateDirectory(_recovery);
        _archive = Path.Combine(_root, "out", "backup.bravebackup");
    }
    private (BraveAppInfo App, BraveProfileInfo Profile) Browser(string name, string? bookmarks, bool preferences)
    {
        var data = Path.Combine(_root, name); var profile = Path.Combine(data, "Default"); Directory.CreateDirectory(profile);
        var path = Path.Combine(profile, "Bookmarks");
        if (bookmarks is not null) File.WriteAllText(path, bookmarks);
        if (preferences && OperatingSystem.IsLinux()) { File.WriteAllText(Path.Combine(profile, "Preferences"), "{}"); File.SetUnixFileMode(Path.Combine(profile, "Preferences"), UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        return (new(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), data), new("Destination", profile, path));
    }
    private async Task CreateArchive(string? password)
    {
        var source = Browser("source", SourceJson, true);
        Directory.CreateDirectory(Path.GetDirectoryName(_archive)!);
        await BraveArchiveBackup.CreateAsync([new BraveBackupSelection(source.App, [source.Profile])], ["bookmarks"], _archive, password, "fast",
            [source.App.UserDataDirectory], "fixture", _ => Task.FromResult(false), null, default);
        var inspected = BraveArchiveInspection.Read(_archive, password);
        _sourceProfile = inspected.Manifest!.Installations[0].Profiles[0].Id;
    }
    private BraveBookmarksRestoreSession Session(string? password, (BraveAppInfo App, BraveProfileInfo Profile) destination, Func<ProcessPresence>? observe = null) =>
        BraveBookmarksRestoreSession.Create(_archive, password, [new(_sourceProfile, destination.App, destination.Profile)], _recovery, "synthetic-safety", observe ?? (() => ProcessPresence.Stopped));

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-archive-key")]
    public async Task RestoresExactNativeBytesPreservesOthersAndRollsBack(string? password)
    {
        if (!OperatingSystem.IsLinux()) return;
        await CreateArchive(password);
        var destination = Browser("destination", """{"roots":{},"version":1,"original":true}""", true);
        var other = Path.Combine(destination.Profile.ProfileDirectory, "Preferences"); var otherBytes = File.ReadAllBytes(other);
        var originalBytes = File.ReadAllBytes(destination.Profile.BookmarksPath);
        File.SetUnixFileMode(destination.Profile.BookmarksPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        using var session = Session(password, destination);
        Assert.False(File.Exists(session.SafetyPath));
        session.Transaction.Prepare(); session.Transaction.Run();
        Assert.Equal(RestoreTransactionState.Completed, session.Transaction.State);
        Assert.Equal(_sourceBytes, File.ReadAllBytes(destination.Profile.BookmarksPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination.Profile.BookmarksPath));
        Assert.Equal(otherBytes, File.ReadAllBytes(other));
        Assert.True(File.Exists(session.SafetyPath)); Assert.DoesNotContain("example.invalid", File.ReadAllText(session.SafetyPath, System.Text.Encoding.Latin1));
        session.Transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, session.Transaction.State);
        Assert.Equal(originalBytes, File.ReadAllBytes(destination.Profile.BookmarksPath));
        Assert.Equal(otherBytes, File.ReadAllBytes(other));
    }

    [Fact]
    public async Task AbsentDestinationIsCreatedFromPreferencesPermissionsAndRemovedOnRollback()
    {
        if (!OperatingSystem.IsLinux()) return;
        await CreateArchive(null);
        var destination = Browser("fresh", null, true);
        using var session = Session(null, destination);
        session.Transaction.Prepare(); session.Transaction.Run();
        Assert.Equal(_sourceBytes, File.ReadAllBytes(destination.Profile.BookmarksPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(destination.Profile.BookmarksPath));
        session.Transaction.Rollback(true);
        Assert.False(File.Exists(destination.Profile.BookmarksPath));
    }

    [Fact]
    public async Task NeverUsedProfileBlockedWithActionableReasonAndNothingWritten()
    {
        if (!OperatingSystem.IsLinux()) return;
        await CreateArchive(null);
        var destination = Browser("never", null, false);
        var error = Assert.Throws<InvalidDataException>(() => Session(null, destination));
        Assert.Contains("open it in Brave", error.Message);
        Assert.Empty(Directory.GetFiles(destination.Profile.ProfileDirectory)); Assert.Empty(Directory.GetFiles(_recovery));
    }

    [Fact]
    public async Task RunningBrowserBlocksSessionCreationAndExecution()
    {
        if (!OperatingSystem.IsLinux()) return;
        await CreateArchive(null);
        var destination = Browser("running", "{\"roots\":{},\"version\":1}", true);
        Assert.Throws<IOException>(() => Session(null, destination, () => ProcessPresence.Running));
        var state = ProcessPresence.Stopped;
        using var session = Session(null, destination, () => state);
        session.Transaction.Prepare(); state = ProcessPresence.Running;
        Assert.Throws<IOException>(() => session.Transaction.Run());
        Assert.Equal("{\"roots\":{},\"version\":1}", File.ReadAllText(destination.Profile.BookmarksPath));
    }

    [Fact]
    public async Task WrongPasswordAndUnknownSourceProfileAreRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        await CreateArchive("synthetic-archive-key");
        var destination = Browser("wrong", "{\"roots\":{},\"version\":1}", true);
        Assert.ThrowsAny<Exception>(() => Session("incorrect", destination));
        var good = Session("synthetic-archive-key", destination); good.Dispose();
        Assert.Throws<InvalidDataException>(() => BraveBookmarksRestoreSession.Create(_archive, "synthetic-archive-key",
            [new(Guid.NewGuid(), destination.App, destination.Profile)], _recovery, "synthetic-safety", () => ProcessPresence.Stopped));
    }

    [Fact]
    public void UnsupportedSnapshotsAreRejected()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => BraveBookmarksRestore.CheckSupported("not json"u8));
        Assert.Throws<InvalidDataException>(() => BraveBookmarksRestore.CheckSupported("""{"version":2,"roots":{}}"""u8));
        BraveBookmarksRestore.CheckSupported("""{"version":1,"roots":{}}"""u8);
        Assert.ThrowsAny<Exception>(() => BraveBookmarksRestore.CheckSupported(new byte[] { 0xff, 0xfe }));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
