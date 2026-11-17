using Xunit;
namespace BraveBackup.Core.Tests;

/// <summary>Integration of real encrypted artifact storage with synthetic file-only targets.</summary>
public sealed class RestoreLeasedTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LeasedTransaction_" + Guid.NewGuid().ToString("N"));
    public RestoreLeasedTransactionTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void PausedTransactionRetainsLeaseAndResumeKeepsSafetyOriginal()
    {
        if (!OperatingSystem.IsLinux()) return;
        var recovery = Path.Combine(_root, "recovery"); Directory.CreateDirectory(recovery);
        var target = Path.Combine(_root, "target"); File.WriteAllText(target, "original");
        var scope = new RestoreArtifactScope(Guid.NewGuid(), recovery, Path.Combine(_root, "source"), []);
        using var lease = RestoreArtifactLease.Acquire(scope);
        using var image = new RestoreFileImage(true, "replacement"u8);
        using var transaction = new RestoreTransaction([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "bookmarks", image)], new Store(lease, target), "synthetic-password");
        transaction.Prepare(); var safety = File.ReadAllBytes(scope.SafetyPath);
        transaction.RequestPause(); transaction.Run();
        Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.Throws<IOException>(() => RestoreArtifactLease.Acquire(scope));
        Assert.Equal("original", File.ReadAllText(target));
        transaction.Run(); Assert.Equal(RestoreTransactionState.Completed, transaction.State);
        Assert.Equal(safety, File.ReadAllBytes(scope.SafetyPath)); Assert.Equal("replacement", File.ReadAllText(target));
        transaction.Rollback(true); Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(safety, File.ReadAllBytes(scope.SafetyPath));
        lease.Dispose(); using var reopened = RestoreArtifactLease.Acquire(scope);
        Assert.Equal(safety, reopened.ReadSafety());
    }
    [Fact]
    public void ExternalJournalEditBlocksFurtherWritesAndLeavesRecoverableSafety()
    {
        if (!OperatingSystem.IsLinux()) return;
        var target = Path.Combine(_root, "target"); File.WriteAllText(target, "original");
        var scope = new RestoreArtifactScope(Guid.NewGuid(), _root, Path.Combine(_root, "source"), []);
        using var lease = RestoreArtifactLease.Acquire(scope);
        using var image = new RestoreFileImage(true, "replacement"u8);
        using var transaction = new RestoreTransaction([new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "settings", image)], new Store(lease, target), "synthetic-password");
        transaction.Prepare(); var safety = File.ReadAllBytes(scope.SafetyPath);
        var journal = File.ReadAllBytes(scope.JournalPath); journal[^1] ^= 1; File.WriteAllBytes(scope.JournalPath, journal);
        Assert.Throws<IOException>(() => transaction.Run());
        Assert.Equal(RestoreTransactionState.Failed, transaction.State); Assert.Equal("original", File.ReadAllText(target));
        Assert.Equal(safety, lease.ReadSafety());
    }
    // Targets are fixture files only; this adapter intentionally does not claim metadata/path-race guarantees.
    private sealed class Store(RestoreArtifactLease artifacts, string target) : IRestoreTransactionStore
    {
        public void VerifyContext(CancellationToken token) { token.ThrowIfCancellationRequested(); artifacts.Verify(); }
        public RestoreFileImage ReadTarget(Guid _, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return File.Exists(target) ? new(true, File.ReadAllBytes(target)) : new(false, []); }
        public void WriteTargetAtomic(Guid _, RestoreFileImage image)
        {
            if (!image.Exists) { File.Delete(target); return; }
            var staging = target + ".tmp"; File.WriteAllBytes(staging, image.Content.Bytes.ToArray()); File.Move(staging, target, true);
        }
        public void SaveSafety(ReadOnlyMemory<byte> bytes) => artifacts.SaveSafety(bytes);
        public byte[] ReadSafety() => artifacts.ReadSafety();
        public void SaveJournal(ReadOnlyMemory<byte> bytes) => artifacts.SaveJournal(bytes);
        public byte[] ReadJournal() => artifacts.ReadJournal();
    }
    public void Dispose() => Directory.Delete(_root, true);
}
