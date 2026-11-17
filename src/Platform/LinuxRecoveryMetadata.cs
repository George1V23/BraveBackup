using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Handle-based Linux permission/mtime codec. Records owner/group without changing ownership; ACLs/xattrs and complete metadata recovery remain unsupported.</summary>
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
        var identity = ReadOwnedIdentity(handle);
        var bytes = new byte[28]; "BBKLM002"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), mode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), File.GetLastWriteTimeUtc(handle).Ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), identity.User);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), identity.Group);
        return bytes;
    }
    /// <summary>Applies validated metadata to an authorized staging handle, then verifies exact readback. Does not publish a file.</summary>
    public static void Apply(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var legacy = bytes.Length == 20 && bytes[..8].SequenceEqual("BBKLM001"u8);
        if (!legacy && (bytes.Length != 28 || !bytes[..8].SequenceEqual("BBKLM002"u8))) throw new InvalidDataException("Invalid Linux recovery metadata.");
        var mode = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]);
        if ((mode & ~0x1ff) != 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Invalid Linux recovery metadata.");
        var identity = ReadOwnedIdentity(handle);
        if (!legacy && (BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != identity.User ||
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) != identity.Group))
            throw new IOException("Saved recovery ownership does not match the destination.");
        RejectExtendedAttributes(handle);
        File.SetLastWriteTimeUtc(handle, new DateTime(ticks, DateTimeKind.Utc));
        File.SetUnixFileMode(handle, (UnixFileMode)mode);
        var verified = Capture(handle);
        if (legacy) { verified = verified[..20]; "BBKLM001"u8.CopyTo(verified); }
        if (!verified.AsSpan().SequenceEqual(bytes)) throw new IOException("Linux metadata recovery verification failed.");
    }
    /// <summary>Requires the current effective user/group; no ownership changes or privilege elevation are attempted.</summary>
    private static void RejectForeignOwnership(SafeFileHandle handle)
    {
        _ = ReadOwnedIdentity(handle);
    }
    private static (uint User, uint Group) ReadOwnedIdentity(SafeFileHandle handle)
    {
        const uint required = 0x11d; // TYPE, NLINK, UID, GID, INO.
        if (ReadStatus(handle, "", 0x1000, required, out var status) != 0 || (status.Mask & required) != required ||
            (status.Mode & 0xF000) != 0x8000 || status.LinkCount != 1 ||
            !OwnershipMatches(status.User, status.Group, EffectiveUser(), EffectiveGroup()))
            throw new IOException("Recovery requires a single-link regular file owned by the current effective user/group.");
        return (status.User, status.Group);
    }
    internal static bool OwnershipMatches(uint owner, uint group, uint effectiveUser, uint effectiveGroup)
        => owner == effectiveUser && group == effectiveGroup;
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct OwnershipStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
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
