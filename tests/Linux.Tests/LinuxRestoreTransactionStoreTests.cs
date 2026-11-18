using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Runs the production Linux store through the transaction engine on synthetic directories only.</summary>
public sealed class LinuxRestoreTransactionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LinuxStore_" + Guid.NewGuid().ToString("N"));
    private readonly string _data, _profile, _recovery, _source;
    public LinuxRestoreTransactionStoreTests()
    {
        _data = Path.Combine(_root, "browser"); _profile = Path.Combine(_data, "Default"); _recovery = Path.Combine(_root, "recovery");
        Directory.CreateDirectory(_profile); Directory.CreateDirectory(_recovery);
        _source = Path.Combine(_root, "source.bravebackup"); File.WriteAllText(_source, "synthetic-source");
    }
    private (BraveRestoreContext Context, RestoreArtifactLease Lease, RestoreArtifactScope Scope) Open(Func<ProcessPresence>? observe = null)
    {
        (BraveAppInfo, BraveProfileInfo)[] targets = [(new(BraveAppChannel.Stable, "synthetic", "synthetic", _data), new("Synthetic", _profile, "unused"))];
        var context = BraveRestoreContext.Capture(_source, targets, observe ?? (() => ProcessPresence.Stopped));
        var scope = new RestoreArtifactScope(Guid.NewGuid(), _recovery, _source, [_data]);
        return (context, RestoreArtifactLease.Acquire(scope), scope);
    }
    private static byte[] Metadata(string path) { using var file = File.OpenRead(path); return LinuxRecoveryMetadata.Capture(file.SafeFileHandle); }

    [Fact]
    public void ReplacesCreatesAndRollsBackIncludingDeletion()
    {
        if (!OperatingSystem.IsLinux()) return;
        var existing = Path.Combine(_profile, "Bookmarks"); File.WriteAllText(existing, "synthetic-original");
        File.SetUnixFileMode(existing, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var removed = Path.Combine(_profile, "Removed"); File.WriteAllText(removed, "synthetic-removed");
        var metadata = Metadata(existing);
        var (context, lease, scope) = Open(); using var _ = lease;
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), profile = Guid.NewGuid(), installation = Guid.NewGuid();
        using var store = new LinuxRestoreTransactionStore(context, lease, new Dictionary<Guid, (string, string)>
        { [a] = (_profile, "Bookmarks"), [b] = (_profile, "Created"), [c] = (_profile, "Removed") });
        using var replacement = new RestoreFileImage(true, "synthetic-new"u8, metadata);
        using var deletion = new RestoreFileImage(false, []);
        using var transaction = new RestoreTransaction([
            new(a, installation, profile, "bookmarks", replacement), new(b, installation, profile, "bookmarks", replacement),
            new(c, installation, profile, "bookmarks", deletion)], store, "synthetic-password");
        transaction.Prepare(); transaction.Run();
        Assert.Equal(RestoreTransactionState.Completed, transaction.State);
        Assert.Equal("synthetic-new", File.ReadAllText(existing)); Assert.Equal("synthetic-new", File.ReadAllText(Path.Combine(_profile, "Created")));
        Assert.False(File.Exists(removed));
        transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("synthetic-original", File.ReadAllText(existing)); Assert.False(File.Exists(Path.Combine(_profile, "Created")));
        Assert.Equal("synthetic-removed", File.ReadAllText(removed));
        Assert.Equal(metadata, Metadata(existing));
        Assert.DoesNotContain(Directory.GetFiles(_profile), path => Path.GetFileName(path).StartsWith(".restore-"));
    }

    [Fact]
    public void ExternalEditBeforeWriteIsNeverOverwritten()
    {
        if (!OperatingSystem.IsLinux()) return;
        var existing = Path.Combine(_profile, "Bookmarks"); File.WriteAllText(existing, "synthetic-original");
        var metadata = Metadata(existing);
        var (context, lease, _) = Open(); using var _ = lease;
        var id = Guid.NewGuid();
        using var store = new LinuxRestoreTransactionStore(context, lease, new Dictionary<Guid, (string, string)> { [id] = (_profile, "Bookmarks") });
        using var replacement = new RestoreFileImage(true, "synthetic-new"u8, metadata);
        using var transaction = new RestoreTransaction([new(id, Guid.NewGuid(), Guid.NewGuid(), "bookmarks", replacement)], store, "synthetic-password");
        transaction.Prepare();
        File.WriteAllText(existing, "synthetic-user-edit");
        Assert.Throws<IOException>(() => transaction.Run());
        Assert.Equal("synthetic-user-edit", File.ReadAllText(existing));
        Assert.Equal(RestoreTransactionState.Prepared, transaction.State); // preflight conflict: nothing was started
    }

    [Fact]
    public void RunningBrowserBlocksStoreCreationAndExecution()
    {
        if (!OperatingSystem.IsLinux()) return;
        File.WriteAllText(Path.Combine(_profile, "Bookmarks"), "synthetic-original");
        var state = ProcessPresence.Stopped;
        var (context, lease, _) = Open(() => state); using var _ = lease;
        var id = Guid.NewGuid();
        using var store = new LinuxRestoreTransactionStore(context, lease, new Dictionary<Guid, (string, string)> { [id] = (_profile, "Bookmarks") });
        using var replacement = new RestoreFileImage(true, "synthetic-new"u8, Metadata(Path.Combine(_profile, "Bookmarks")));
        using var transaction = new RestoreTransaction([new(id, Guid.NewGuid(), Guid.NewGuid(), "bookmarks", replacement)], store, "synthetic-password");
        transaction.Prepare();
        state = ProcessPresence.Running;
        Assert.Throws<IOException>(() => transaction.Run());
        Assert.Equal("synthetic-original", File.ReadAllText(Path.Combine(_profile, "Bookmarks")));
        Assert.Throws<IOException>(() => new LinuxRestoreTransactionStore(context, lease, new Dictionary<Guid, (string, string)> { [id] = (_profile, "Bookmarks") }));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
