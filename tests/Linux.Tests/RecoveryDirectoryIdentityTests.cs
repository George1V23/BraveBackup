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
    [Fact]
    public void LinkedAncestorCannotBeUsedToOpenNestedRecoveryDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var real = Path.Combine(_root, "real"); Directory.CreateDirectory(Path.Combine(real, "nested"));
        var link = Path.Combine(_root, "alias"); Directory.CreateSymbolicLink(link, real);
        Assert.Throws<IOException>(() => RecoveryDirectoryIdentity.Open(Path.Combine(link, "nested")));
        Assert.Empty(Directory.GetFiles(real, "*", SearchOption.AllDirectories));
    }
    [Fact]
    public void ChildNamesCannotEscapeRetainedDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = RecoveryDirectoryIdentity.Open(_root);
        Assert.Throws<ArgumentException>(() => directory.PublishChild("../escape", "synthetic"u8.ToArray(), false));
        Assert.Throws<ArgumentException>(() => directory.OpenReadChild("nested/file"));
        Assert.Empty(Directory.GetFiles(_root));
    }
    [Fact]
    public void PublicationRetainsSingleOwnedFileAndLinkedChildrenCannotBeRead()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var directory = RecoveryDirectoryIdentity.Open(_root);
        directory.PublishChild("artifact", "first"u8.ToArray(), false);
        directory.PublishChild("artifact", "second"u8.ToArray(), true);
        Assert.Equal("second", File.ReadAllText(Path.Combine(_root, "artifact")));
        File.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "artifact"));
        Assert.Throws<IOException>(() => directory.OpenReadChild("linked"));
        Assert.DoesNotContain(Directory.GetFiles(_root), path => path.EndsWith(".tmp"));
    }
    [Fact]
    public void ExclusiveDirectoryOwnershipRejectsContenderAndReleasesOnDispose()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var first = RecoveryDirectoryIdentity.Open(_root);
        using var second = RecoveryDirectoryIdentity.Open(_root);
        first.AcquireExclusive(); first.AcquireExclusive();
        Assert.Throws<IOException>(() => second.AcquireExclusive());
        first.Dispose(); second.AcquireExclusive();
        second.PublishChild("owned", "synthetic"u8.ToArray(), false);
        Assert.Equal("synthetic", File.ReadAllText(Path.Combine(_root, "owned")));
    }
    [Fact]
    public void NewRecoveryFilePublishesVerifiedMetadataWithoutClobberingExistingData()
    {
        if (!OperatingSystem.IsLinux()) return;
        var original = Path.Combine(_root, "original"); File.WriteAllText(original, "synthetic-original");
        File.SetLastWriteTimeUtc(original, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        byte[] metadata; using (var file = File.OpenRead(original)) metadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle);
        using var directory = RecoveryDirectoryIdentity.Open(_root);
        directory.PublishNewRecoveryFile("new", "synthetic-new"u8.ToArray(), metadata);
        Assert.Equal("synthetic-new", File.ReadAllText(Path.Combine(_root, "new")));
        using (var file = File.OpenRead(Path.Combine(_root, "new"))) Assert.Equal(metadata, LinuxRecoveryMetadata.Capture(file.SafeFileHandle));
        Assert.Throws<IOException>(() => directory.PublishNewRecoveryFile("original", "overwrite"u8.ToArray(), metadata));
        Assert.Equal("synthetic-original", File.ReadAllText(original));
        Assert.DoesNotContain(Directory.GetFiles(_root), path => path.EndsWith(".tmp"));
    }
    [Fact]
    public void InvalidMetadataPublicationCleansStagingAndPreservesExistingDestination()
    {
        if (!OperatingSystem.IsLinux()) return;
        File.WriteAllText(Path.Combine(_root, "existing"), "synthetic-original");
        using var directory = RecoveryDirectoryIdentity.Open(_root);
        Assert.Throws<InvalidDataException>(() => directory.PublishNewRecoveryFile("new", "synthetic"u8.ToArray(), new byte[4]));
        Assert.False(File.Exists(Path.Combine(_root, "new")));
        Assert.Single(Directory.GetFiles(_root));
        Assert.Equal("synthetic-original", File.ReadAllText(Path.Combine(_root, "existing")));
    }
    [Fact]
    public void IdenticalContentReplacementDoesNotMatchRetainedFileIdentity()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(_root, "target"); File.WriteAllText(path, "synthetic");
        using var directory = RecoveryDirectoryIdentity.Open(_root);
        using var retained = directory.OpenReadChild("target"); directory.VerifyChildIdentity("target", retained.SafeFileHandle);
        var replacement = Path.Combine(_root, "replacement"); File.WriteAllText(replacement, "synthetic");
        File.Move(replacement, path, true);
        Assert.Throws<IOException>(() => directory.VerifyChildIdentity("target", retained.SafeFileHandle));
        Assert.Equal("synthetic", File.ReadAllText(path));
    }
    [Fact]
    public void DirectoryIdentityComparisonDistinguishesSameAndDifferentTargets()
    {
        if (!OperatingSystem.IsLinux()) return;
        var child = Path.Combine(_root, "child"); Directory.CreateDirectory(child);
        using var first = RecoveryDirectoryIdentity.Open(_root);
        using var alias = RecoveryDirectoryIdentity.Open(Path.Combine(_root, "."));
        using var different = RecoveryDirectoryIdentity.Open(child);
        Assert.True(first.RefersToSameDirectory(alias));
        Assert.False(first.RefersToSameDirectory(different));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
