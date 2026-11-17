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
    [Fact]
    public void RelativePublicationIsPrivateAndNeverClobbersNewArtifact()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = RecoveryDirectoryIdentity.Open(_root);
        directory.PublishChild("artifact", "synthetic"u8.ToArray(), false);
        Assert.Throws<IOException>(() => directory.PublishChild("artifact", "other"u8.ToArray(), false));
        using var input = directory.OpenReadChild("artifact"); using var reader = new StreamReader(input);
        Assert.Equal("synthetic", reader.ReadToEnd());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(_root, "artifact")));
        Assert.Single(Directory.GetFiles(_root));
    }
    [Fact]
    public void ReplacedDirectoryCannotReceivePublicationThroughRetainedIdentity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "recovery"); Directory.CreateDirectory(path);
        using var directory = RecoveryDirectoryIdentity.Open(path);
        Directory.Move(path, path + "-original"); Directory.CreateDirectory(path);
        Assert.Throws<IOException>(() => directory.PublishChild("artifact", "synthetic"u8.ToArray(), false));
        Assert.Empty(Directory.GetFiles(path)); Assert.Empty(Directory.GetFiles(path + "-original"));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
