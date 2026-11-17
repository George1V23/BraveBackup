using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Windows directory identity/rename protection primitive, not yet connected to recovery publication.</summary>
public sealed class WindowsRecoveryDirectoryIdentity : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly string _path;
    private readonly FileIdentity _identity;
    private bool _disposed;
    private WindowsRecoveryDirectoryIdentity(string path, SafeFileHandle handle)
    { _path = path; _handle = handle; _identity = ReadIdentity(handle); }
    /// <summary>Opens an existing directory without following its final reparse point and denies delete sharing.</summary>
    public static WindowsRecoveryDirectoryIdentity Open(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var full = Path.GetFullPath(path);
        for (string? parent = full; parent is not null; parent = Path.GetDirectoryName(parent))
            if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory) throw new IOException("Recovery directory is missing, linked or unsafe.");
        var handle = OpenDirectory(full);
        try { var result = new WindowsRecoveryDirectoryIdentity(full, handle); result.Verify(); return result; }
        catch { handle.Dispose(); throw; }
    }
    /// <summary>Compares the retained volume/128-bit file ID with a fresh directory handle.</summary>
    public void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var current = OpenDirectory(_path);
        if (!Same(ReadIdentity(current), _identity) || !Same(ReadIdentity(_handle), _identity)) throw new IOException("Recovery directory identity changed.");
    }
    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFile(path, 0, 1 | 2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open recovery directory handle."); }
        return handle;
    }
    private static FileIdentity ReadIdentity(SafeFileHandle handle)
    {
        if (!GetAttributes(handle, 9, out var attributes, 8) || (attributes.Attributes & 0x10) == 0 || (attributes.Attributes & 0x400) != 0 ||
            !GetIdentity(handle, 18, out var identity, 24)) throw new IOException("Recovery directory identity is unavailable or unsafe.");
        return identity;
    }
    private static bool Same(FileIdentity first, FileIdentity second) => first.Volume == second.Volume && first.Id == second.Id;
    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity { public ulong Volume; public Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int kind, out FileIdentity identity, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle handle, int kind, out AttributeTag attributes, uint size);
    /// <summary>Releases the retained directory handle without modifying artifacts.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _handle.Dispose(); }
}
