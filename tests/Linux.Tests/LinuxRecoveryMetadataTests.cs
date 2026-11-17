using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class LinuxRecoveryMetadataTests
{
    [Fact]
    public void PermissionsAndTimestampRoundTripThroughAuthorizedHandles()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), "RecoveryMetadata_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            file.Write("synthetic"u8); file.Flush();
            File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(file.SafeFileHandle, new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc));
            var metadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle);
            File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead);
            File.SetLastWriteTimeUtc(file.SafeFileHandle, DateTime.UtcNow);
            LinuxRecoveryMetadata.Apply(file.SafeFileHandle, metadata);
            Assert.Equal(metadata, LinuxRecoveryMetadata.Capture(file.SafeFileHandle));
            var malformed = metadata.ToArray(); malformed[8] = 255; malformed[9] = 255;
            Assert.Throws<InvalidDataException>(() => LinuxRecoveryMetadata.Apply(file.SafeFileHandle, malformed));
            Assert.Equal(metadata, LinuxRecoveryMetadata.Capture(file.SafeFileHandle));
        }
        finally { File.Delete(path); }
    }
}
