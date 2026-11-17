using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Windows recovery metadata capture only. Application/publication is not enabled.</summary>
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
        foreach (var offset in new[] { 4, 8, 12, 16 })
        {
            var relative = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[offset..]);
            if (relative != 0 && (relative < 20 || relative >= length || (relative & 3) != 0)) throw Invalid();
        }
    }
    private static InvalidDataException Invalid() => new("Invalid Windows recovery metadata.");
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorControl")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")]
    private static extern uint DescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
