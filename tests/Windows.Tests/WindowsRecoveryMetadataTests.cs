using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class WindowsRecoveryMetadataTests
{
    [WindowsOnlyFact]
    public void CapturesSecurityAndTimestampsWithoutChangingContents()
    {
        var path = Path.Combine(Path.GetTempPath(), "WindowsMetadata_" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "synthetic-original");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var metadata = WindowsRecoveryMetadata.Capture(file.SafeFileHandle);
                Assert.True(metadata.AsSpan(0, 8).SequenceEqual("BBKWM001"u8));
                Assert.InRange(metadata.Length, 37, 65536);
                Assert.Equal(metadata, WindowsRecoveryMetadata.Capture(file.SafeFileHandle));
            }
            Assert.Equal("synthetic-original", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
