using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Windows recovery metadata capture and handle-based application (native validation pending).</summary>
public static class WindowsRecoveryMetadata
{
    /// <summary>Captures handle-based owner/group/DACL and UTC timestamps into bounded opaque metadata.</summary>
    /// <remarks>Does not capture SACL, alternate streams, EAs or complete filesystem metadata.</remarks>
    public static byte[] Capture(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (GetSecurityInfo(handle, 1, 7, out _, out _, out _, out _, out var pointer) != 0)
            throw new IOException("Cannot capture Windows recovery security descriptor.");
        byte[] descriptor;
        try
        {
            if (!DescriptorControl(pointer, out var control, out _) || (control & 0x8000) == 0)
                throw new IOException("Windows recovery requires a self-relative security descriptor.");
            var length = DescriptorLength(pointer);
            if (length == 0 || length > 65536 - 36) throw new IOException("Windows recovery security metadata exceeds its limit.");
            descriptor = new byte[(int)length]; Marshal.Copy(pointer, descriptor, 0, descriptor.Length);
        }
        finally { if (pointer != IntPtr.Zero) LocalFree(pointer); }
        if (descriptor.Length > 65536 - 36) throw new IOException("Windows recovery security metadata exceeds its limit.");
        var bytes = new byte[36 + descriptor.Length];
        "BBKWM001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), File.GetCreationTimeUtc(handle).Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), File.GetLastWriteTimeUtc(handle).Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), File.GetLastAccessTimeUtc(handle).Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), descriptor.Length);
        descriptor.CopyTo(bytes, 36);
        Validate(bytes);
        return bytes;
    }
    /// <summary>Applies owner/group/DACL and creation/write times to a handle opened with write-attribute, WRITE_DAC and WRITE_OWNER access.</summary>
    /// <remarks>
    /// Fails closed: invalid framing, a rejected security change (for example owner changes without the needed privilege) or a readback
    /// that differs in descriptor bytes or creation/write times throws. Last-access time is applied but not compared. SACL, streams and EAs are not handled.
    /// </remarks>
    public static void Apply(SafeFileHandle handle, ReadOnlySpan<byte> bytes)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        Validate(bytes);
        var expected = bytes.ToArray();
        var descriptor = Marshal.AllocHGlobal(expected.Length - 36);
        try
        {
            Marshal.Copy(expected, 36, descriptor, expected.Length - 36);
            if (!GetOwner(descriptor, out var owner, out _) || !GetGroup(descriptor, out var group, out _) ||
                !GetDacl(descriptor, out var present, out var dacl, out _) || !DescriptorControl(descriptor, out var control, out _))
                throw new IOException("Cannot read Windows recovery security descriptor.");
            uint information = 0;
            if (owner != IntPtr.Zero) information |= 1;
            if (group != IntPtr.Zero) information |= 2;
            if (present) information |= 4 | ((control & 0x1000) != 0 ? 0x80000000u : 0x20000000u);
            if (information != 0 && SetSecurityInfo(handle, 1, information, owner, group, dacl, IntPtr.Zero) != 0)
                throw new IOException("Cannot apply Windows recovery security descriptor.");
        }
        finally { Marshal.FreeHGlobal(descriptor); }
        File.SetCreationTimeUtc(handle, new DateTime(BinaryPrimitives.ReadInt64LittleEndian(expected.AsSpan(8)), DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(handle, new DateTime(BinaryPrimitives.ReadInt64LittleEndian(expected.AsSpan(16)), DateTimeKind.Utc));
        File.SetLastAccessTimeUtc(handle, new DateTime(BinaryPrimitives.ReadInt64LittleEndian(expected.AsSpan(24)), DateTimeKind.Utc));
        var actual = Capture(handle);
        if (!actual.AsSpan(8, 16).SequenceEqual(expected.AsSpan(8, 16)) || !actual.AsSpan(32).SequenceEqual(expected.AsSpan(32)))
            throw new IOException("Windows recovery metadata readback did not match.");
    }
    /// <summary>Checks opaque metadata framing, timestamps and self-relative descriptor bounds without invoking Windows APIs.</summary>
    /// <remarks>Structural checks do not establish native ACL validity or authorization to apply it.</remarks>
    public static void Validate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 56 or > 65536 || !bytes[..8].SequenceEqual("BBKWM001"u8)) throw Invalid();
        foreach (var offset in new[] { 8, 16, 24 })
        {
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) throw Invalid();
        }
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        if (length != bytes.Length - 36) throw Invalid();
        var descriptor = bytes[36..];
        if (descriptor[0] != 1 || (BinaryPrimitives.ReadUInt16LittleEndian(descriptor[2..]) & 0x8000) == 0) throw Invalid();
        var regions = new List<(uint Start, uint End)>();
        foreach (var offset in new[] { 4, 8, 12, 16 })
        {
            var relative = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[offset..]);
            if (relative == 0) continue;
            if (relative < 20 || relative >= length || (relative & 3) != 0) throw Invalid();
            var part = descriptor[(int)relative..];
            int size;
            if (offset is 4 or 8)
            {
                if (part.Length < 8 || part[0] != 1 || part[1] > 15) throw Invalid();
                size = 8 + part[1] * 4;
                if (size > part.Length) throw Invalid();
            }
            else
            {
                if (part.Length < 8 || part[0] is not (2 or 4)) throw Invalid();
                size = BinaryPrimitives.ReadUInt16LittleEndian(part[2..]);
                if (size < 8 || size > part.Length || (size & 3) != 0) throw Invalid();
                var count = BinaryPrimitives.ReadUInt16LittleEndian(part[4..]);
                var cursor = 8;
                for (var index = 0; index < count; index++)
                {
                    if (cursor > size - 4) throw Invalid();
                    var aceSize = BinaryPrimitives.ReadUInt16LittleEndian(part[(cursor + 2)..]);
                    if (aceSize < 4 || (aceSize & 3) != 0 || aceSize > size - cursor) throw Invalid();
                    cursor += aceSize;
                }
            }
            var end = relative + (uint)size;
            if (regions.Any(region => relative < region.End && end > region.Start &&
                    (relative != region.Start || end != region.End))) throw Invalid();
            regions.Add((relative, end));
        }
    }
    private static InvalidDataException Invalid() => new("Invalid Windows recovery metadata.");
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorOwner")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOwner(IntPtr descriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorGroup")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGroup(IntPtr descriptor, out IntPtr group, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint SetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")]
    private static extern uint DescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
