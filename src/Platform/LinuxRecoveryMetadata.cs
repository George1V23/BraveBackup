using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Handle-based Linux permission/mtime codec. Does not represent ownership, ACLs, xattrs or complete filesystem metadata.</summary>
public static class LinuxRecoveryMetadata
{
    /// <summary>Captures ordinary permission bits and last-write UTC ticks from an already authorized file handle.</summary>
    public static byte[] Capture(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var mode = (int)File.GetUnixFileMode(handle);
        if ((mode & ~0x1ff) != 0) throw new IOException("Special file mode bits are unsupported for recovery.");
        var bytes = new byte[20]; "BBKLM001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), mode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), File.GetLastWriteTimeUtc(handle).Ticks);
        return bytes;
    }
    /// <summary>Applies validated metadata to an authorized staging handle, then verifies exact readback. Does not publish a file.</summary>
    public static void Apply(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (bytes.Length != 20 || !bytes[..8].SequenceEqual("BBKLM001"u8)) throw new InvalidDataException("Invalid Linux recovery metadata.");
        var mode = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]);
        if ((mode & ~0x1ff) != 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Invalid Linux recovery metadata.");
        File.SetLastWriteTimeUtc(handle, new DateTime(ticks, DateTimeKind.Utc));
        File.SetUnixFileMode(handle, (UnixFileMode)mode);
        if (!Capture(handle).AsSpan().SequenceEqual(bytes)) throw new IOException("Linux metadata recovery verification failed.");
    }
}
