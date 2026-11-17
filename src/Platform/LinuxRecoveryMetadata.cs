using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Handle-based Linux permission/mtime codec. Does not represent ownership, ACLs, xattrs or complete filesystem metadata.</summary>
public static class LinuxRecoveryMetadata
{
    /// <summary>Captures ordinary permission bits and last-write UTC ticks from an already authorized file handle.</summary>
    public static byte[] Capture(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        RejectForeignOwnership(handle);
        RejectExtendedAttributes(handle);
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
        RejectForeignOwnership(handle);
        RejectExtendedAttributes(handle);
        File.SetLastWriteTimeUtc(handle, new DateTime(ticks, DateTimeKind.Utc));
        File.SetUnixFileMode(handle, (UnixFileMode)mode);
        if (!Capture(handle).AsSpan().SequenceEqual(bytes)) throw new IOException("Linux metadata recovery verification failed.");
    }
    /// <summary>Requires the current effective user/group; no ownership changes or privilege elevation are attempted.</summary>
    private static void RejectForeignOwnership(SafeFileHandle handle)
    {
        if (ReadStatus(handle, "", 0x1000, 0x18, out var status) != 0 || (status.Mask & 0x18) != 0x18 ||
            !OwnershipMatches(status.User, status.Group, EffectiveUser(), EffectiveGroup()))
            throw new IOException("File ownership is unsupported or cannot be inspected for recovery.");
    }
    internal static bool OwnershipMatches(uint owner, uint group, uint effectiveUser, uint effectiveGroup)
        => owner == effectiveUser && group == effectiveGroup;
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct OwnershipStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint User;
        [FieldOffset(24)] public uint Group;
    }
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int ReadStatus(SafeFileHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out OwnershipStatus status);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint EffectiveUser();
    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint EffectiveGroup();

    /// <summary>Rejects unsupported extended metadata without reading attribute names or values.</summary>
    private static void RejectExtendedAttributes(SafeFileHandle handle)
    {
        var length = ListAttributes(handle, IntPtr.Zero, 0);
        if (length != 0) throw new IOException("Extended file metadata is unsupported or cannot be inspected for recovery.");
    }
    [DllImport("libc", EntryPoint = "flistxattr", SetLastError = true)]
    private static extern nint ListAttributes(SafeFileHandle handle, IntPtr list, nuint size);
}
