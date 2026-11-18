using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>
/// Retained Windows file-or-absence target inside an ancestor-retained directory, with guarded replace/create/delete.
/// Not validated on native Windows yet; every unexpected condition fails closed.
/// </summary>
/// <remarks>
/// The original file is held open with read/delete sharing only, so cooperating processes cannot write to it while retained.
/// Deletion acts on the retained handle itself. Replacement renames a verified staged file over the target after confirming the
/// path still names the retained file; unlike Linux there is no atomic exchange, so a non-cooperating substitution between that
/// check and the rename is possible and is only detected afterwards by the caller's verification.
/// The caller must authorize scope and enforce closed-browser state.
/// </remarks>
public sealed class WindowsRecoveryFileTarget : IDisposable
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000;
    private readonly WindowsRecoveryDirectoryChain _chain;
    private readonly string _path;
    private readonly string _directory;
    private readonly SafeFileHandle? _file;
    private readonly FileIdentity _identity;
    private bool _disposed;

    private WindowsRecoveryFileTarget(WindowsRecoveryDirectoryChain chain, string directory, string path, SafeFileHandle? file, FileIdentity identity)
    { _chain = chain; _directory = directory; _path = path; _file = file; _identity = identity; }

    /// <summary>True when the target was a regular file at capture; false when absence was observed.</summary>
    public bool Exists => _file is not null;

    /// <summary>Retains ancestors and, when present, the single-link regular file. Links and special files are rejected.</summary>
    public static WindowsRecoveryFileTarget Open(string directoryPath, string name)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        CheckName(name);
        var directory = Path.GetFullPath(directoryPath);
        var chain = WindowsRecoveryDirectoryChain.Open(directory);
        SafeFileHandle? file = null;
        try
        {
            var path = Path.Combine(directory, name);
            var kind = SnapshotFileAccess.Kind(path);
            if (kind == SnapshotNodeKind.Missing) { chain.Verify(); return new(chain, directory, path, null, default); }
            if (kind != SnapshotNodeKind.RegularFile) throw new IOException("Unsafe recovery target.");
            file = OpenFile(path, GenericRead | Delete, 1 | 4, 3);
            var identity = Inspect(file);
            var result = new WindowsRecoveryFileTarget(chain, directory, path, file, identity);
            result.Verify(); return result;
        }
        catch { file?.Dispose(); chain.Dispose(); throw; }
    }

    /// <summary>Rechecks ancestors and that the path still names the retained file (or is still absent).</summary>
    public void Verify()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _chain.Verify();
        if (_file is null)
        {
            if (SnapshotFileAccess.Kind(_path) != SnapshotNodeKind.Missing) throw new IOException("Recovery target absence changed.");
            return;
        }
        using var current = OpenFile(_path, 0, 1 | 2 | 4, 3);
        if (!Same(Inspect(current), _identity) || !Same(Inspect(_file), _identity)) throw new IOException("Recovery target file identity changed.");
    }

    /// <summary>Reads the retained file's bounded bytes (empty when absent).</summary>
    public byte[] ReadContent(CancellationToken token = default)
    {
        Verify();
        if (_file is null) return [];
        var length = RandomAccess.GetLength(_file);
        if (length > MaximumBytes) throw new IOException("Restore target exceeds the supported file limit.");
        var bytes = new byte[(int)length];
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = RandomAccess.Read(_file, bytes.AsSpan(offset, Math.Min(65536, bytes.Length - offset)), offset);
            if (count == 0) throw new IOException("Restore target changed length.");
            offset += count;
        }
        if (RandomAccess.GetLength(_file) != length) throw new IOException("Restore target changed length.");
        return bytes;
    }

    /// <summary>Captures opaque metadata from the retained file (empty when absent).</summary>
    public byte[] ReadMetadata() { Verify(); return _file is null ? [] : WindowsRecoveryMetadata.Capture(_file); }

    /// <summary>Replaces the retained file, or creates the target if absence was retained, with verified content and metadata.</summary>
    public void Publish(ReadOnlySpan<byte> content, ReadOnlySpan<byte> metadata)
    {
        Verify();
        var staging = Path.Combine(_directory, ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var output = OpenFile(staging, GenericRead | GenericWrite | Delete | WriteDac | WriteOwner, 0, 1);
        var renamed = false;
        try
        {
            RandomAccess.Write(output, content, 0); RandomAccess.FlushToDisk(output);
            WindowsRecoveryMetadata.Apply(output, metadata); RandomAccess.FlushToDisk(output);
            if (RandomAccess.GetLength(output) != content.Length) throw new IOException("Recovery staging length changed.");
            var buffer = new byte[Math.Min(65536, Math.Max(1, content.Length))];
            for (var offset = 0; offset < content.Length;)
            {
                var count = RandomAccess.Read(output, buffer.AsSpan(0, Math.Min(buffer.Length, content.Length - offset)), offset);
                if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(content.Slice(offset, count))) throw new IOException("Recovery staging verification failed.");
                offset += count;
            }
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer);
            Verify();
            Rename(output, _path, replace: _file is not null);
            renamed = true;
            using var published = OpenFile(_path, 0, 1 | 2 | 4, 3);
            if (!Same(Inspect(published), Inspect(output))) throw new IOException("Published recovery identity changed.");
            _chain.Verify();
        }
        finally
        {
            // An unrenamed staging file is ours alone (share mode 0): mark it for deletion through our own handle.
            if (!renamed) SetDisposition(output);
        }
    }

    /// <summary>Deletes the retained file through its own handle, so a substituted path cannot be removed.</summary>
    public void DeleteRetained()
    {
        Verify();
        if (_file is null) return;
        SetDisposition(_file);
        _chain.Verify();
    }

    private static void SetDisposition(SafeFileHandle handle)
    {
        var flags = 1u | 2u; // FILE_DISPOSITION_DELETE | FILE_DISPOSITION_POSIX_SEMANTICS
        if (!SetInformation(handle, 21, ref flags, 4)) throw new IOException("Recovery file removal failed; recovery artifacts require attention.");
    }

    private static void Rename(SafeFileHandle source, string destination, bool replace)
    {
        var pointer = IntPtr.Size; var nameOffset = pointer * 2 + 4; var size = nameOffset + (destination.Length + 1) * 2;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var index = 0; index < size; index++) Marshal.WriteByte(buffer, index, 0);
            Marshal.WriteInt32(buffer, 0, replace ? 1 | 2 : 2); // REPLACE_IF_EXISTS only when replacing; POSIX semantics
            Marshal.WriteInt32(buffer, pointer * 2, destination.Length * 2);
            Marshal.Copy(destination.ToCharArray(), 0, buffer + nameOffset, destination.Length);
            if (!SetInformationRaw(source, 22, buffer, (uint)size)) throw new IOException("Recovery destination cannot be published.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static SafeFileHandle OpenFile(string path, uint access, uint share, uint disposition)
    {
        // FILE_FLAG_OPEN_REPARSE_POINT: never follow a final link.
        var handle = CreateFile(path, access, share, IntPtr.Zero, disposition, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot open recovery file handle."); }
        return handle;
    }

    private static FileIdentity Inspect(SafeFileHandle handle)
    {
        if (!GetAttributes(handle, 9, out var attributes, 8) || (attributes.Attributes & (0x10 | 0x400)) != 0 ||
            !GetIdentity(handle, 18, out var identity, 24) || !GetBasic(handle, out var info) || info.Links != 1)
            throw new IOException("Recovery file is not a safe single-link regular file.");
        return identity;
    }
    private static bool Same(FileIdentity first, FileIdentity second) => first.Volume == second.Volume && first.Id == second.Id;
    private static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0', ':']) >= 0)
            throw new ArgumentException("A single target filename is required.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileIdentity { public ulong Volume; public Guid Id; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes; public uint Tag; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandle
    {
        public uint Attributes, CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int kind, out FileIdentity identity, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle handle, int kind, out AttributeTag attributes, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetBasic(SafeFileHandle handle, out ByHandle information);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformation(SafeFileHandle handle, int kind, ref uint value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationRaw(SafeFileHandle handle, int kind, IntPtr buffer, uint size);

    /// <summary>Releases the retained file and ancestor handles without modifying the filesystem.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _file?.Dispose(); _chain.Dispose(); }
}
