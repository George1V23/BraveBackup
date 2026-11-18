using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;

/// <summary>Native Windows cases on synthetic files only. They are skipped elsewhere and have not been run on Windows yet.</summary>
public sealed class WindowsRestoreTransactionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WindowsStore_" + Guid.NewGuid().ToString("N"));
    private readonly string _data, _profile, _recovery, _source;
    public WindowsRestoreTransactionStoreTests()
    {
        _data = Path.Combine(_root, "browser"); _profile = Path.Combine(_data, "Default"); _recovery = Path.Combine(_root, "recovery");
        Directory.CreateDirectory(_profile); Directory.CreateDirectory(_recovery);
        _source = Path.Combine(_root, "source.bravebackup"); File.WriteAllText(_source, "synthetic-source");
    }
    private static byte[] Metadata(string path) { using var file = File.OpenRead(path); return WindowsRecoveryMetadata.Capture(file.SafeFileHandle); }

    [WindowsOnlyFact]
    public void FileTargetDeletesOnlyRetainedFileAndRejectsSubstitution()
    {
        var path = Path.Combine(_profile, "target"); File.WriteAllText(path, "synthetic");
        using (var target = WindowsRecoveryFileTarget.Open(_profile, "target"))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(path, "edit"));
            target.DeleteRetained();
        }
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(Directory.GetFiles(_profile), p => Path.GetFileName(p).StartsWith(".restore-"));
    }

    [WindowsOnlyFact]
    public void StoreReplacesCreatesDeletesAndRollsBack()
    {
        var existing = Path.Combine(_profile, "Bookmarks"); File.WriteAllText(existing, "synthetic-original");
        var removed = Path.Combine(_profile, "Removed"); File.WriteAllText(removed, "synthetic-removed");
        var metadata = Metadata(existing);
        (BraveAppInfo, BraveProfileInfo)[] targets = [(new(BraveAppChannel.Stable, "synthetic", "synthetic", _data), new("Synthetic", _profile, "unused"))];
        var context = BraveRestoreContext.Capture(_source, targets, () => ProcessPresence.Stopped);
        using var artifacts = WindowsRestoreArtifacts.Acquire(new RestoreArtifactScope(Guid.NewGuid(), _recovery, _source, [_data]));
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        using var store = RestoreTransactionStore.Create(context, artifacts, new Dictionary<Guid, (string, string)>
        { [a] = (_profile, "Bookmarks"), [b] = (_profile, "Created"), [c] = (_profile, "Removed") });
        using var replacement = new RestoreFileImage(true, "synthetic-new"u8, metadata);
        using var deletion = new RestoreFileImage(false, []);
        using var transaction = new RestoreTransaction([
            new(a, Guid.NewGuid(), Guid.NewGuid(), "bookmarks", replacement), new(b, Guid.NewGuid(), Guid.NewGuid(), "bookmarks", replacement),
            new(c, Guid.NewGuid(), Guid.NewGuid(), "bookmarks", deletion)], store, "synthetic-password");
        transaction.Prepare(); transaction.Run();
        Assert.Equal("synthetic-new", File.ReadAllText(existing)); Assert.False(File.Exists(removed));
        transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("synthetic-original", File.ReadAllText(existing)); Assert.False(File.Exists(Path.Combine(_profile, "Created")));
        Assert.Equal("synthetic-removed", File.ReadAllText(removed));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
