using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Handle-based Linux permission/mtime codec. Records owner/group without changing ownership; ACLs/xattrs and complete metadata recovery remain unsupported.</summary>
public static class LinuxRecoveryMetadata
{
    /// <summary>Captures ordinary permission bits and last-write UTC ticks from an already authorized file handle.</summary>
    public static byte[] Capture(SafeFileHandle handle) => CaptureCore(handle, directory: false);

    internal const int MetadataSize = 28;

    /// <summary>Captures metadata for a private, owner-writable directory retaining ordinary permission bits.</summary>
    internal static byte[] CaptureDirectory(SafeFileHandle handle) => CaptureCore(handle, directory: true);

    private static byte[] CaptureCore(SafeFileHandle handle, bool directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var identity = ReadOwnedIdentity(handle, directory);
        RejectExtendedAttributes(handle);
        var mode = (int)File.GetUnixFileMode(handle);
        if (!ValidMode(mode, directory)) throw new IOException("Unsupported recovery permissions.");
        var bytes = new byte[MetadataSize];
        (directory ? "BBKDM001"u8 : "BBKLM002"u8).CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), mode);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), File.GetLastWriteTimeUtc(handle).Ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), identity.User);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), identity.Group);
        return bytes;
    }
    /// <summary>Applies validated metadata to an authorized staging handle, then verifies exact readback. Does not publish a file.</summary>
    public static void Apply(SafeFileHandle handle, ReadOnlySpan<byte> bytes) => ApplyCore(handle, bytes, directory: false);

    /// <summary>Restores supported metadata on a staging directory.</summary>
    internal static void ApplyDirectory(SafeFileHandle handle, ReadOnlySpan<byte> bytes) => ApplyCore(handle, bytes, directory: true);

    private static void ApplyCore(SafeFileHandle handle, ReadOnlySpan<byte> bytes, bool directory)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var legacy = !directory && bytes.Length == 20 && bytes[..8].SequenceEqual("BBKLM001"u8);
        var magic = directory ? "BBKDM001"u8 : "BBKLM002"u8;
        if (!legacy && (bytes.Length != MetadataSize || !bytes[..8].SequenceEqual(magic)))
            throw new InvalidDataException("Invalid Linux recovery metadata.");
        var mode = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]);
        if (!ValidMode(mode, directory) || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Invalid Linux recovery metadata.");
        var identity = ReadOwnedIdentity(handle, directory);
        if (!legacy && (BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != identity.User ||
                        BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) != identity.Group))
            throw new IOException("Saved recovery ownership does not match the destination.");
        RejectExtendedAttributes(handle);
        File.SetLastWriteTimeUtc(handle, new DateTime(ticks, DateTimeKind.Utc));
        File.SetUnixFileMode(handle, (UnixFileMode)mode);
        var verified = CaptureCore(handle, directory);
        if (legacy) { verified = verified[..20]; "BBKLM001"u8.CopyTo(verified); }
        if (!verified.AsSpan().SequenceEqual(bytes)) throw new IOException("Linux metadata recovery verification failed.");
    }
    private static bool ValidMode(int mode, bool directory) =>
        (mode & ~0x1ff) == 0 && (!directory || ((mode & 0x1c0) == 0x1c0 && (mode & 0x12) == 0));

    private static (uint User, uint Group) ReadOwnedIdentity(SafeFileHandle handle, bool directory)
    {
        const uint required = 0x11d; // TYPE, NLINK, UID, GID, INO.
        if (ReadStatus(handle, "", 0x1000, required, out var status) != 0 || (status.Mask & required) != required ||
            (status.Mode & 0xF000) != (directory ? 0x4000 : 0x8000) || (!directory && status.LinkCount != 1) ||
            !OwnershipMatches(status.User, status.Group, EffectiveUser(), EffectiveGroup()))
            throw new IOException("Recovery requires an object of the expected type and effective ownership.");
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
