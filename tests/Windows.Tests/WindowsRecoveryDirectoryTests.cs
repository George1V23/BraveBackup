using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class WindowsRecoveryDirectoryTests
{
    [WindowsOnlyFact]
    public void RetainedDirectoryVerifiesAndBlocksRenameUntilReleased()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsRecovery_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var identity = WindowsRecoveryDirectoryIdentity.Open(root); identity.Verify();
            Assert.Throws<IOException>(() => Directory.Move(root, root + ".moved"));
            identity.Dispose(); Assert.Throws<ObjectDisposedException>(() => identity.Verify());
            Directory.Move(root, root + ".moved"); Directory.Move(root + ".moved", root);
        }
        finally { Directory.Delete(root, true); }
    }
    [WindowsOnlyFact]
    public void AncestorChainRetainsParentUntilDisposal()
    {
        var root = Path.Combine(Path.GetTempPath(), "WindowsChain_" + Guid.NewGuid().ToString("N"));
        var child = Path.Combine(root, "nested"); Directory.CreateDirectory(child);
        try
        {
            using var chain = WindowsRecoveryDirectoryChain.Open(child); chain.Verify();
            Assert.Throws<IOException>(() => Directory.Move(root, root + ".moved"));
            chain.Dispose(); Assert.Throws<ObjectDisposedException>(() => chain.Verify());
            Directory.Move(root, root + ".moved"); Directory.Move(root + ".moved", root);
        }
        finally { Directory.Delete(root, true); }
    }
}
