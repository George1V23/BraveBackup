using System.Text;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BraveBookmarksRestorePlanTests : IDisposable
{
    private const string Version = "1.102.13";
    private const string SafetyPassword = "synthetic-safety-password";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BookmarksPlan_" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _snapshot = Encoding.UTF8.GetBytes(
        """{"version":1,"roots":{"bookmark_bar":{"name":"Bar","children":[{"type":"url","name":"Fixture","url":"https://example.test/"}]},"other":{"name":"Other","children":[]},"synced":{"name":"Mobile","children":[]}}}""");

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ActualBackupRestoresExactNativeBytesAndRollsBack(bool encrypted, bool initiallyAbsent)
    {
        if (!OperatingSystem.IsLinux()) return;
        var password = encrypted ? "synthetic-archive-password" : null;
        var (archive, manifest) = await CreateArchiveAsync(_snapshot, password);
        var (app, profile) = Profile("destination");
        var preferences = Path.Combine(profile.ProfileDirectory, "Preferences");
        File.WriteAllText(preferences, "{}");
        var original = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_snapshot).Replace("Fixture", "Original"));
        if (!initiallyAbsent) File.WriteAllBytes(profile.BookmarksPath, original);
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        using var session = Open(archive, manifest, password, app, profile, recovery);

        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Equal(!initiallyAbsent, File.Exists(profile.BookmarksPath));
        session.Transaction.Prepare();
        session.Transaction.Run();
        Assert.Equal(RestoreTransactionState.Completed, session.Transaction.State);
        Assert.Equal(_snapshot, File.ReadAllBytes(profile.BookmarksPath));
        Assert.Equal("{}", File.ReadAllText(preferences));

        session.Transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, session.Transaction.State);
        if (initiallyAbsent) Assert.False(File.Exists(profile.BookmarksPath));
        else Assert.Equal(original, File.ReadAllBytes(profile.BookmarksPath));
    }

    [Fact]
    public async Task RejectsWrongArchiveReviewVersionAndRunningBrowserBeforeWrites()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (archive, manifest) = await CreateArchiveAsync(_snapshot);
        var (app, profile) = Profile("destination");
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;

        Assert.Throws<InvalidDataException>(() => Open(archive, manifest with { ArchiveId = Guid.NewGuid() }, null, app, profile, recovery));
        Assert.Throws<InvalidDataException>(() => Open(archive, manifest, null, app with { Version = "2.0" }, profile, recovery));
        Assert.Throws<IOException>(() => Open(archive, manifest, null, app, profile, recovery, ProcessPresence.Running));
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Empty(Directory.GetFiles(profile.ProfileDirectory));
    }

    [Theory]
    [InlineData("\"version\":2")]
    [InlineData("\"version\":1,\"sync_metadata\":\"c3luYw==\"")]
    [InlineData("\"version\":9,\"version\":1")]
    public async Task RejectsUnsupportedNativeSnapshotEvenWhenArchiveValidates(string fields)
    {
        if (!OperatingSystem.IsLinux()) return;
        var payload = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(_snapshot).Replace("\"version\":1", fields));
        var (archive, manifest) = await CreateArchiveAsync(payload);
        var (app, profile) = Profile("destination");
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        Assert.Throws<InvalidDataException>(() => Open(archive, manifest, null, app, profile, recovery));
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Empty(Directory.GetFiles(profile.ProfileDirectory));
    }

    [Fact]
    public async Task ReviewDigestAndOperationIdBindTheWriteFreeSession()
    {
        if (!OperatingSystem.IsLinux()) return;
        var (archive, manifest) = await CreateArchiveAsync(_snapshot);
        var inspection = BraveArchiveInspection.Read(archive);
        var (app, profile) = Profile("destination");
        File.WriteAllBytes(profile.BookmarksPath, _snapshot);
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        var operation = Guid.NewGuid();
        BraveRestoreMapping[] mappings = [new(manifest.Installations.Single().Profiles.Single().Id, app, profile, "bookmarks")];
        LinuxRestoreSession OpenReviewed(string digest) => BraveProfileRestorePlan.Open(archive,
            manifest.ArchiveId, null, recovery, SafetyPassword, mappings, [], operation, digest, () => ProcessPresence.Stopped);

        Assert.Throws<IOException>(() => OpenReviewed(new string('0', 64)));
        using var session = OpenReviewed(Assert.IsType<string>(inspection.SourceSha256));
        Assert.Equal(operation, session.Transaction.OperationId);
        Assert.Equal(Path.Combine(recovery, operation.ToString("N") + ".safety.encrypted"), session.Artifacts.SafetyPath);
        Assert.Empty(Directory.GetFiles(recovery));
        Assert.Equal(_snapshot, File.ReadAllBytes(profile.BookmarksPath));
    }

    private LinuxRestoreSession Open(string archive, ArchiveManifest manifest, string? password, BraveAppInfo app,
        BraveProfileInfo profile, string recovery, ProcessPresence presence = ProcessPresence.Stopped) =>
        BraveProfileRestorePlan.Open(archive, manifest.ArchiveId, password, recovery, SafetyPassword,
            [new(manifest.Installations.Single().Profiles.Single().Id, app, profile, "bookmarks")], [], () => presence);

    private async Task<(string Path, ArchiveManifest Manifest)> CreateArchiveAsync(byte[] bytes, string? password = null)
    {
        var (app, profile) = Profile("source");
        File.WriteAllBytes(profile.BookmarksPath, bytes);
        var archive = Path.Combine(_root, "source.bravebackup");
        await BraveArchiveBackup.CreateAsync([new(app, [profile])], ["bookmarks"], archive, password,
            "balanced", [app.UserDataDirectory], "synthetic", _ => Task.FromResult(false));
        using var input = File.OpenRead(archive);
        return (archive, BackupArchive.Validate(input, password));
    }

    private (BraveAppInfo App, BraveProfileInfo Profile) Profile(string name)
    {
        var data = Path.Combine(_root, name);
        var profile = Directory.CreateDirectory(Path.Combine(data, "Default")).FullName;
        return (new(BraveAppChannel.Stable, Version, "synthetic", data),
            new("Synthetic", profile, Path.Combine(profile, "Bookmarks")));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
