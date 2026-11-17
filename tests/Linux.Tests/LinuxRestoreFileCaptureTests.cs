using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class LinuxRestoreFileCaptureTests
{
    [Fact]
    public void CapturesOriginalBytesMetadataAndAbsenceWithoutWriting()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "TargetCapture_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "target"); File.WriteAllText(path, "synthetic-original");
            using var image = LinuxRestoreFileCapture.Read(root, "target");
            Assert.True(image.Exists); Assert.Equal("synthetic-original"u8.ToArray(), image.Content.Bytes.ToArray());
            Assert.Equal(28, image.Metadata.Bytes.Length);
            using var absent = LinuxRestoreFileCapture.Read(root, "missing"); Assert.False(absent.Exists);
            Assert.Single(Directory.GetFiles(root)); Assert.Equal("synthetic-original", File.ReadAllText(path));
            File.CreateSymbolicLink(Path.Combine(root, "link"), path);
            Assert.Throws<IOException>(() => LinuxRestoreFileCapture.Read(root, "link"));
            Assert.Throws<ArgumentException>(() => LinuxRestoreFileCapture.Read(root, "../target"));
            Assert.Throws<OperationCanceledException>(() => LinuxRestoreFileCapture.Read(root, "target", new CancellationToken(true)));
        }
        finally { Directory.Delete(root, true); }
    }
}
