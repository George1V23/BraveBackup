using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class LinuxRestoreExpectedStateTests
{
    [Fact]
    public void AppliedCheckpointsNeverReplaceOriginalRecoveryBaseline()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "ExpectedRecovery_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "target"); File.WriteAllText(path, "original");
            using var state = new LinuxRestoreExpectedState(root, "target");
            using var baseline = state.CopyOriginal();
            File.WriteAllText(path, "published");
            using var applied = LinuxRestoreFileCapture.Read(root, "target");
            state.AcceptPublished(applied); state.VerifyCurrent();
            using var unchanged = state.CopyOriginal(); Assert.Equal(baseline.Content.Bytes.ToArray(), unchanged.Content.Bytes.ToArray());
            Assert.Equal(baseline.Metadata.Bytes.ToArray(), unchanged.Metadata.Bytes.ToArray());
            File.WriteAllText(path, "conflict"); Assert.Throws<IOException>(() => state.VerifyCurrent());
            Assert.Throws<IOException>(() => state.AcceptPublished(applied));
            File.Delete(path); using var absent = new RestoreFileImage(false, []);
            state.AcceptPublished(absent); state.VerifyCurrent();
            using var original = state.CopyOriginal(); Assert.Equal("original"u8.ToArray(), original.Content.Bytes.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
}
