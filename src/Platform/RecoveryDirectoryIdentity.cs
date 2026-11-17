using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Retains a Linux directory handle and detects path replacement. It does not authorize writes or eliminate path races.</summary>
public sealed class RecoveryDirectoryIdentity : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly (uint Major, uint Minor, ulong Inode) _identity;
    private bool _disposed;
    private RecoveryDirectoryIdentity(string path, SafeFileHandle handle)
    { _path = path; _handle = handle; _identity = Identity(handle); }
    /// <summary>Opens a directory without following its final link after rejecting linked ancestors. Unsupported hosts fail closed.</summary>
    public static RecoveryDirectoryIdentity Open(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Recovery directory handle support is not implemented on this host.");
        var full = Path.GetFullPath(path);
        CheckAncestors(full);
        var handle = OpenDirectory(full);
        try { return new(full, handle); } catch { handle.Dispose(); throw; }
    }
    /// <summary>Reopens the authorized path and compares its device/inode with the retained handle.</summary>
    public void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckAncestors(_path);
        using var current = OpenDirectory(_path);
        if (Identity(current) != _identity || Identity(_handle) != _identity)
            throw new IOException("Recovery directory identity changed.");
    }
    private static void CheckAncestors(string path)
    {
        for (string? directory = path; directory is not null; directory = Path.GetDirectoryName(directory))
            if (SnapshotFileAccess.Kind(directory) != SnapshotNodeKind.Directory)
                throw new IOException("Recovery directory is missing, linked or unsafe.");
    }
    private static SafeFileHandle OpenDirectory(string path)
    {
        // Linux O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC; identity is subsequently queried through this handle.
        var descriptor = NativeOpen(path, 0x10000 | 0x20000 | 0x80000);
        if (descriptor < 0) throw new IOException("Cannot open recovery directory handle.");
        return new SafeFileHandle((IntPtr)descriptor, true);
    }
    private static (uint Major, uint Minor, ulong Inode) Identity(SafeFileHandle handle)
    {
        if (Statx(handle, "", 0x1000, 0x101, out var status) != 0 || (status.Mask & 0x101) != 0x101 || (status.Mode & 0xF000) != 0x4000)
            throw new IOException("Recovery directory identity is unavailable.");
        return (status.DeviceMajor, status.DeviceMinor, status.Inode);
    }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int NativeOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out StatxBuffer status);
    /// <summary>Closes the retained native directory handle without modifying the filesystem.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _handle.Dispose(); }
}
