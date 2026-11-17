using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class RecoveryDirectoryIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RecoveryIdentity_" + Guid.NewGuid().ToString("N"));
    public RecoveryDirectoryIdentityTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void SameNamedDirectoryReplacementIsDetected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "recovery"); Directory.CreateDirectory(path);
        using var identity = RecoveryDirectoryIdentity.Open(path); identity.Verify();
        Directory.Move(path, path + "-original"); Directory.CreateDirectory(path);
        Assert.Throws<IOException>(() => identity.Verify());
    }
    [Fact]
    public void SymlinkDirectoriesAreRejectedAndDisposalInvalidatesVerification()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "recovery"); Directory.CreateDirectory(path);
        var link = Path.Combine(_root, "link"); Directory.CreateSymbolicLink(link, path);
        Assert.Throws<IOException>(() => RecoveryDirectoryIdentity.Open(link));
        using var identity = RecoveryDirectoryIdentity.Open(path); identity.Dispose();
        Assert.Throws<ObjectDisposedException>(() => identity.Verify());
    }
    public void Dispose() => Directory.Delete(_root, true);
}
