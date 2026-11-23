using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BraveBackup.Platform;

/// <summary>Owned contents and supported metadata from a read-only Windows target capture.</summary>
public sealed class WindowsRecoveryFileImage : IDisposable
{
    private byte[]? _content;
    private byte[]? _metadata;

    internal WindowsRecoveryFileImage(bool exists, byte[] content, byte[] metadata)
    {
        Exists = exists;
        _content = content;
        _metadata = metadata;
    }

    public bool Exists { get; }
    public ReadOnlyMemory<byte> Content => _content ?? throw new ObjectDisposedException(nameof(WindowsRecoveryFileImage));
    public ReadOnlyMemory<byte> Metadata => _metadata ?? throw new ObjectDisposedException(nameof(WindowsRecoveryFileImage));

    /// <summary>Clears the owned image buffers.</summary>
    public void Dispose()
    {
        if (_content is not null) CryptographicOperations.ZeroMemory(_content);
        if (_metadata is not null) CryptographicOperations.ZeroMemory(_metadata);
        _content = null;
        _metadata = null;
    }
}

/// <summary>Captures a bounded existing file or verified absence under a retained Windows directory chain.</summary>
public static class WindowsRecoveryFileCapture
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes UnsupportedAttributes = FileAttributes.Directory | FileAttributes.Device |
        FileAttributes.SparseFile | FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Offline |
        FileAttributes.Encrypted | RecallOnOpen | RecallOnDataAccess;

    /// <summary>Reads a single child without granting any profile publication capability.</summary>
    public static WindowsRecoveryFileImage Read(string directory, string name, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ValidateChildName(name);
        var parent = Path.GetFullPath(directory);
        var root = Path.GetPathRoot(parent);
        if (root is null || root.Length != 3 || root[1] != ':')
            throw new IOException("Windows recovery capture requires a supported local drive.");
        if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("Windows recovery capture requires a supported local drive.");
        using var chain = WindowsRecoveryDirectoryChain.Open(parent);
        var path = Path.Combine(parent, name);
        using var handle = OpenFile(path);
        if (handle is null)
        {
            chain.Verify();
            using var appeared = OpenFile(path);
            if (appeared is not null) throw new IOException("Recovery target appeared during capture.");
            token.ThrowIfCancellationRequested();
            return new(false, [], []);
        }

        var expected = Inspect(handle);
        if (expected.Length is < 0 or > MaximumBytes)
            throw new IOException("Windows recovery target exceeds the supported file limit.");
        var metadata = WindowsRecoveryMetadata.Capture(handle);
        byte[]? content = null;
        byte[]? currentMetadata = null;
        try
        {
            content = new byte[(int)expected.Length];
            using var stream = new FileStream(handle, FileAccess.Read, 65_536);
            for (var offset = 0; offset < content.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = stream.Read(content, offset, Math.Min(content.Length - offset, 65_536));
                if (count == 0) throw Changed();
                offset += count;
            }
            if (stream.ReadByte() != -1 || Inspect(handle) != expected) throw Changed();
            currentMetadata = WindowsRecoveryMetadata.Capture(handle);
            if (!metadata.AsSpan().SequenceEqual(currentMetadata)) throw Changed();

            chain.Verify();
            using var reopened = OpenFile(path);
            if (reopened is null || Inspect(reopened) != expected) throw Changed();
            chain.Verify();
            token.ThrowIfCancellationRequested();
            return new(true, content, metadata);
        }
        catch
        {
            if (content is not null) CryptographicOperations.ZeroMemory(content);
            CryptographicOperations.ZeroMemory(metadata);
            throw;
        }
        finally
        {
            if (currentMetadata is not null) CryptographicOperations.ZeroMemory(currentMetadata);
        }
    }

    internal static void ValidateChildName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name.EndsWith('.') || name.EndsWith(' ') ||
            name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.Any(char.IsControl))
            throw new ArgumentException("A literal Windows recovery filename is required.", nameof(name));

        var stem = name.Split('.', 2)[0].TrimEnd(' ');
        var upper = stem.ToUpperInvariant();
        var isNumberedDevice = upper.Length == 4 &&
            (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) &&
            upper[3] is >= '0' and <= '9' or '¹' or '²' or '³';
        if (upper is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" || isNumberedDevice)
            throw new ArgumentException("A literal Windows recovery filename is required.", nameof(name));
    }

    internal static bool HasOnlyUnnamedDataStream(ReadOnlySpan<byte> buffer, long length)
    {
        const string name = "::$DATA";
        const int streamNameOffset = 24;
        var nameBytes = name.Length * sizeof(char);
        return length >= 0 && buffer.Length >= streamNameOffset + nameBytes &&
            BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]) == nameBytes &&
            BinaryPrimitives.ReadInt64LittleEndian(buffer[8..]) == length &&
            Encoding.Unicode.GetString(buffer.Slice(streamNameOffset, nameBytes)) == name;
    }

    private static SafeFileHandle? OpenFile(string path)
    {
        // GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING and FILE_FLAG_OPEN_REPARSE_POINT.
        var handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (error == 2) return null; // ERROR_FILE_NOT_FOUND. Other errors do not establish absence.
        throw new IOException("Cannot open a Windows recovery file for a stable read.");
    }

    private static (ulong Volume, Guid Id, long Length) Inspect(SafeFileHandle handle)
    {
        if (GetFileType(handle) != 1 || // FILE_TYPE_DISK
            !GetAttributes(handle, 9, out var attributes, 8) || ((FileAttributes)attributes.Attributes & UnsupportedAttributes) != 0 ||
            !GetStandard(handle, 1, out var standard, 24) || standard.NumberOfLinks != 1 ||
            standard.DeletePending != 0 || standard.Directory != 0 ||
            !GetIdentity(handle, 18, out var identity, 24) || identity.Id == Guid.Empty)
            throw new IOException("Windows recovery target is linked, special, or cannot be identified.");

        var streamInfo = new byte[4096];
        if (!GetStreams(handle, 7, streamInfo, (uint)streamInfo.Length) ||
            !HasOnlyUnnamedDataStream(streamInfo, standard.Length))
            throw new IOException("Windows recovery cannot verify a single unnamed data stream.");
        return (identity.Volume, identity.Id, standard.Length);
    }

    private static IOException Changed() => new("Windows recovery target changed while being captured.");

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes; public uint Tag; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong Volume; public Guid Id; }

    [StructLayout(LayoutKind.Sequential)]
    private struct StandardInfo
    {
        public long AllocationSize;
        public long Length;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetAttributes(SafeFileHandle handle, int kind, out AttributeTag attributes, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIdentity(SafeFileHandle handle, int kind, out FileIdentity identity, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStandard(SafeFileHandle handle, int kind, out StandardInfo standard, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetStreams(SafeFileHandle handle, int kind, [Out] byte[] streams, uint size);
}
