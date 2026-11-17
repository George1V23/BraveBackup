using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class RestoreArtifactLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RecoveryLease_" + Guid.NewGuid().ToString("N"));
    public RestoreArtifactLeaseTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void LeaseRetainsOwnershipAcrossSafetyAndJournalOperations()
    {
        if (!OperatingSystem.IsLinux()) return;
        var scope = new RestoreArtifactScope(Guid.NewGuid(), _root, Path.Combine(_root, "source"), []);
        using var lease = RestoreArtifactLease.Acquire(scope);
        Assert.Empty(Directory.GetFiles(_root));
        Assert.Throws<IOException>(() => RestoreArtifactLease.Acquire(scope));
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, "synthetic-password")) writer.Write("fixture"u8);
        var bytes = output.ToArray(); lease.SaveSafety(bytes); lease.SaveJournal(bytes); lease.SaveJournal(bytes);
        Assert.Equal(bytes, lease.ReadSafety()); Assert.Equal(bytes, lease.ReadJournal());
        Assert.Throws<IOException>(() => lease.SaveSafety(bytes));
        Assert.Throws<IOException>(() => RestoreArtifactStorage.Publish(scope.JournalPath, bytes, true));
        lease.Dispose();
        using var next = RestoreArtifactLease.Acquire(scope); Assert.Equal(bytes, next.ReadSafety());
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }
    [Fact]
    public void ReplacedDirectoryAndDisposedLeaseCannotPublish()
    {
        if (!OperatingSystem.IsLinux()) return;
        var folder = Path.Combine(_root, "recovery"); Directory.CreateDirectory(folder);
        var scope = new RestoreArtifactScope(Guid.NewGuid(), folder, Path.Combine(_root, "source"), []);
        using var lease = RestoreArtifactLease.Acquire(scope);
        Directory.Move(folder, folder + "-old"); Directory.CreateDirectory(folder);
        Assert.Throws<IOException>(() => lease.Verify());
        Assert.Throws<IOException>(() => lease.SaveSafety(new byte[48]));
        Assert.Empty(Directory.GetFiles(folder)); Assert.Empty(Directory.GetFiles(folder + "-old"));
        lease.Dispose(); Assert.Throws<ObjectDisposedException>(() => lease.Verify());
    }
    public void Dispose() => Directory.Delete(_root, true);
}
