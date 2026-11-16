using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Identifies filesystem objects before raw snapshot capture.</summary>
public enum SnapshotNodeKind
{
    Missing,
    RegularFile,
    Directory,
    Unsafe
}

/// <summary>Prevents raw snapshots from opening links, FIFOs, devices or sockets as ordinary files.</summary>
public static class SnapshotFileAccess
{
    /// <summary>Reads object type without following a final symbolic link. Access errors are not absence.</summary>
    public static SnapshotNodeKind Kind(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (Statx(-100, path, 0x100, 0x201, out var status) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 2) return SnapshotNodeKind.Missing;
                    throw new IOException("Cannot inspect snapshot source type.");
                }
    
                return Classify(status);
            }
            catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
            { throw new IOException("The host cannot safely inspect raw snapshot files."); }
        }

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Snapshot capture supports Windows and Linux.");
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return SnapshotNodeKind.Unsafe;
            return attributes.HasFlag(FileAttributes.Directory)
                ? SnapshotNodeKind.Directory
                : SnapshotNodeKind.RegularFile;
        }
        catch (FileNotFoundException)
        {
            return SnapshotNodeKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return SnapshotNodeKind.Missing;
        }
    }

    /// <summary>Opens an existing regular file read-only, rejecting special files and final symlink races.</summary>
    public static FileStream OpenRead(string path)
    {
        if (Kind(path) != SnapshotNodeKind.RegularFile)
            throw new IOException("Snapshot source must be a regular file.");
        if (!OperatingSystem.IsLinux())
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            try
            {
                if (Kind(path) != SnapshotNodeKind.RegularFile) throw new IOException("Snapshot source changed type.");
                return stream;
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        // Linux flags: O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK. Never create a source or wait on a FIFO.
        var descriptor = Open(path, 0x80000 | 0x20000 | 0x800);
        if (descriptor < 0) throw new IOException("Cannot open regular snapshot source.");
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try
        {
            if (Statx(descriptor, "", 0x1000, 0x201, out var status) != 0 ||
                Classify(status) != SnapshotNodeKind.RegularFile)
                throw new IOException("Snapshot source changed type.");
            return new FileStream(handle, FileAccess.Read, 65536);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SnapshotNodeKind Classify(StatxBuffer status)
    {
        if ((status.Mask & 1) == 0) throw new IOException("Snapshot source type is unavailable.");
        return (status.Mode & 0xF000) switch
        {
            0x8000 => SnapshotNodeKind.RegularFile, 0x4000 => SnapshotNodeKind.Directory,
            _ => SnapshotNodeKind.Unsafe
        };
    }

    // Linux statx has an architecture-independent, fixed-width layout (256 bytes).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out StatxBuffer status);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}