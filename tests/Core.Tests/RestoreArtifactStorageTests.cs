using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class RestoreArtifactStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RestoreArtifacts_" + Guid.NewGuid().ToString("N"));
    public RestoreArtifactStorageTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void EncryptedArtifactsRoundTripAndReplacementRequiresExplicitChoice()
    {
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, "synthetic-password")) writer.Write("synthetic-original"u8);
        var target = Path.Combine(_root, "safety.encrypted");
        RestoreArtifactStorage.Publish(target, output.ToArray());
        Assert.Equal(output.ToArray(), RestoreArtifactStorage.Read(target));
        Assert.Throws<IOException>(() => RestoreArtifactStorage.Publish(target, output.ToArray()));
        RestoreArtifactStorage.Publish(target, output.ToArray(), true);
        Assert.Single(Directory.GetFiles(_root));
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(target));
    }
    [Fact]
    public void PlaintextAndLinkedArtifactsAreRejectedWithoutWrites()
    {
        var target = Path.Combine(_root, "artifact");
        Assert.Throws<InvalidDataException>(() => RestoreArtifactStorage.Publish(target, "plaintext"u8.ToArray()));
        Assert.Empty(Directory.GetFiles(_root));
        File.WriteAllText(target, "plaintext");
        Assert.Throws<InvalidDataException>(() => RestoreArtifactStorage.Read(target));
        if (OperatingSystem.IsLinux())
        {
            var link = Path.Combine(_root, "link"); File.CreateSymbolicLink(link, target);
            Assert.Throws<IOException>(() => RestoreArtifactStorage.Read(link));
        }
    }
    [Fact]
    public void ReplacementCannotClobberPlaintextOrDirectoryAndLeavesNoStaging()
    {
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, "synthetic-password")) writer.Write("fixture"u8);
        var target = Path.Combine(_root, "existing"); File.WriteAllText(target, "keep-original");
        Assert.Throws<InvalidDataException>(() => RestoreArtifactStorage.Publish(target, output.ToArray(), true));
        Assert.Equal("keep-original", File.ReadAllText(target));
        var directory = Path.Combine(_root, "directory"); Directory.CreateDirectory(directory);
        Assert.Throws<IOException>(() => RestoreArtifactStorage.Publish(directory, output.ToArray(), true));
        Assert.Single(Directory.GetFiles(_root));
    }
    [Fact]
    public void ContendedLinuxRecoveryDirectoryCannotPublishOrLeaveStaging()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var owner = BraveBackup.Platform.RecoveryDirectoryIdentity.Open(_root); owner.AcquireExclusive();
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, "synthetic-password")) writer.Write("fixture"u8);
        Assert.Throws<IOException>(() => RestoreArtifactStorage.Publish(Path.Combine(_root, "blocked"), output.ToArray()));
        Assert.Empty(Directory.GetFiles(_root));
    }
    public void Dispose() => Directory.Delete(_root, true);
}
