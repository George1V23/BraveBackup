using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class LinuxRestoreTargetLeaseTests
{
    [Fact]
    public void OriginalIdentityAndAbsenceRemainFrozenUntilDisposal()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "TargetLease_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "target"); File.WriteAllText(path, "synthetic");
            using var lease = LinuxRestoreTargetLease.Capture(root, "target"); lease.VerifyOriginal();
            using var copy = lease.CopyOriginal(); Assert.Equal("synthetic"u8.ToArray(), copy.Content.Bytes.ToArray());
            var replacement = Path.Combine(root, "replacement"); File.WriteAllText(replacement, "synthetic");
            File.Move(replacement, path, true); Assert.Throws<IOException>(() => lease.VerifyOriginal());
            Assert.Equal("synthetic"u8.ToArray(), copy.Content.Bytes.ToArray());
            using var missing = LinuxRestoreTargetLease.Capture(root, "missing"); missing.VerifyOriginal();
            File.WriteAllText(Path.Combine(root, "missing"), "external"); Assert.Throws<IOException>(() => missing.VerifyOriginal());
            missing.Dispose(); Assert.Throws<ObjectDisposedException>(() => missing.CopyOriginal());
        }
        finally { Directory.Delete(root, true); }
    }
}
