using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class LinuxRecoveryMetadataTests
{
    [Theory]
    [InlineData(1000u, 1000u, true)]
    [InlineData(0u, 1000u, false)]
    [InlineData(1000u, 0u, false)]
    public void RecoveryOwnershipRequiresBothEffectiveIdentities(uint owner, uint group, bool expected)
        => Assert.Equal(expected, LinuxRecoveryMetadata.OwnershipMatches(owner, group, 1000, 1000));

    [Fact]
    public void SavedOwnershipMismatchIsRejectedAndLegacyMetadataRemainsReadable()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), "RecoveryOwner_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var metadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle); Assert.Equal(28, metadata.Length);
            var mismatch = metadata.ToArray(); mismatch[20] ^= 1;
            Assert.Throws<IOException>(() => LinuxRecoveryMetadata.Apply(file.SafeFileHandle, mismatch));
            Assert.Equal(metadata, LinuxRecoveryMetadata.Capture(file.SafeFileHandle));
            var legacy = metadata[..20]; "BBKLM001"u8.CopyTo(legacy);
            LinuxRecoveryMetadata.Apply(file.SafeFileHandle, legacy);
            Assert.Equal(metadata, LinuxRecoveryMetadata.Capture(file.SafeFileHandle));
        }
        finally { File.Delete(path); }
    }

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
    [Fact]
    public void ExtendedAttributesBlockCaptureAndApplyWithoutChangingMetadata()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Path.Combine(Path.GetTempPath(), "RecoveryXattr_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            var metadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle);
            Assert.Equal(0, SetAttribute(file.SafeFileHandle, "user.bravebackup.synthetic", [1], 1, 0));
            var beforeMode = File.GetUnixFileMode(file.SafeFileHandle);
            var beforeTime = File.GetLastWriteTimeUtc(file.SafeFileHandle);
            Assert.Throws<IOException>(() => LinuxRecoveryMetadata.Capture(file.SafeFileHandle));
            Assert.Throws<IOException>(() => LinuxRecoveryMetadata.Apply(file.SafeFileHandle, metadata));
            Assert.Equal(beforeMode, File.GetUnixFileMode(file.SafeFileHandle));
            Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(file.SafeFileHandle));
        }
        finally { File.Delete(path); }
    }
    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "fsetxattr", SetLastError = true)]
    private static extern int SetAttribute(Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, int flags);
}
