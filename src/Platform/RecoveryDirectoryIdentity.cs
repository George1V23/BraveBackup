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
    private bool _exclusive;
    private RecoveryDirectoryIdentity(string path, SafeFileHandle handle)
    { _path = path; _handle = handle; _identity = Identity(handle); }
    /// <summary>Opens a directory without following its final link after rejecting linked ancestors. Unsupported hosts fail closed.</summary>
    public static RecoveryDirectoryIdentity Open(string path)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Recovery directory handle support is not implemented on this host.");
        var full = Path.GetFullPath(path);
        CheckAncestors(full);
        // Walk from the filesystem root through retained handles. No ancestor component
        // is resolved again through an untrusted absolute path during this open.
        var handle = OpenDirectory("/");
        try
        {
            foreach (var component in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var descriptor = OpenAt(handle, component, 0x10000 | 0x20000 | 0x80000, 0);
                if (descriptor < 0) throw new IOException("Cannot securely open recovery directory component.");
                var next = new SafeFileHandle((IntPtr)descriptor, true);
                handle.Dispose(); handle = next;
            }
            var result = new RecoveryDirectoryIdentity(full, handle);
            result.Verify();
            return result;
        }
        catch { handle.Dispose(); throw; }
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
    /// <summary>Compares retained device/inode identities after revalidating both directory paths.</summary>
    public bool RefersToSameDirectory(RecoveryDirectoryIdentity other)
    {
        ArgumentNullException.ThrowIfNull(other); Verify(); other.Verify();
        return _identity == other._identity;
    }

    /// <summary>Acquires a nonblocking Linux advisory lock for this retained directory until disposal.</summary>
    /// <remarks>Only cooperating writers honor this lock. It does not prevent unrelated processes changing directory entries.</remarks>
    public void AcquireExclusive()
    {
        Verify();
        if (_exclusive) return;
        if (Flock(_handle, 2 | 4) != 0) throw new IOException("Recovery directory is already owned or cannot be locked.");
        _exclusive = true;
        try { Verify(); }
        catch { Flock(_handle, 8); _exclusive = false; throw; }
    }
    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle directory, int operation);

    /// <summary>Opens a regular child relative to the retained directory; links and special files are rejected.</summary>
    public FileStream OpenReadChild(string name)
    {
        Verify(); CheckName(name);
        var descriptor = OpenAt(_handle, name, 0x80000 | 0x20000 | 0x800, 0);
        if (descriptor < 0) throw new IOException("Cannot open recovery artifact.");
        var handle = new SafeFileHandle((IntPtr)descriptor, true);
        try
        {
            if (Statx(handle, "", 0x1000, 5, out var status) != 0 || (status.Mask & 5) != 5 || status.LinkCount != 1 || (status.Mode & 0xF000) != 0x8000)
                throw new IOException("Recovery artifact is not a regular file.");
            Verify(); return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }
    /// <summary>Requires the named regular child to still refer to an already retained file handle.</summary>
    public void VerifyChildIdentity(string name, SafeFileHandle retainedFile)
    {
        Verify(); CheckName(name);
        if (!MatchesChild(name, retainedFile)) throw new IOException("Recovery target file identity changed.");
    }

    /// <summary>Publishes a private staged child using directory-relative syscalls, so directory replacement cannot redirect writes.</summary>
    public void PublishChild(string name, ReadOnlyMemory<byte> bytes, bool replace)
    {
        Verify(); CheckName(name);
        var staging = ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        var descriptor = OpenAt(_handle, staging, 1 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180);
        if (descriptor < 0) throw new IOException("Cannot create private recovery staging.");
        using var output = new FileStream(new SafeFileHandle((IntPtr)descriptor, true), FileAccess.Write);
        try
        {
            output.Write(bytes.Span); output.Flush(true);
            Verify();
            if (!MatchesChild(staging, output.SafeFileHandle)) throw new IOException("Recovery staging identity changed.");
            // RENAME_NOREPLACE prevents a new artifact from replacing a concurrently created child.
            if (RenameAt(_handle, staging, _handle, name, replace ? 0u : 1u) != 0)
                throw new IOException("Cannot publish recovery artifact.");
            if (!MatchesChild(name, output.SafeFileHandle)) throw new IOException("Published recovery identity changed.");
            Verify();
        }
        finally
        {
            // Do not knowingly delete a child that replaced our staging file.
            CleanupOwnedChild(staging, output.SafeFileHandle);
        }
    }
    /// <summary>Publishes a new recovery file with verified metadata; never replaces an existing child.</summary>
    /// <remarks>Callers must authorize the target and enforce closed-browser state. Existing-target recovery is not implemented here.</remarks>
    public void PublishNewRecoveryFile(string name, ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> metadata) =>
        PublishRecoveryFile(name, content, metadata, expectedTarget: null, verifyTarget: null);

    /// <summary>Publishes a new child without clobbering an existing entry and verifies context after staging.</summary>
    public void PublishNewRecoveryFile(string name, ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> metadata, Action verifyTarget)
    {
        ArgumentNullException.ThrowIfNull(verifyTarget);
        RequireExclusive();
        PublishRecoveryFile(name, content, metadata, expectedTarget: null, verifyTarget);
    }

    /// <summary>Stages a replacement, then checks the retained target and caller's precondition before renaming.</summary>
    /// <remarks>Requires the directory lock. Callers must prevent concurrent writers that ignore the advisory lock.</remarks>
    public void ReplaceRecoveryFile(string name, SafeFileHandle expectedTarget, ReadOnlyMemory<byte> content,
        ReadOnlyMemory<byte> metadata, Action verifyTarget)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        ArgumentNullException.ThrowIfNull(verifyTarget);
        RequireExclusive();
        PublishRecoveryFile(name, content, metadata, expectedTarget, verifyTarget);
    }

    /// <summary>Removes the expected single-link file after rechecking the caller's precondition and retained identity.</summary>
    /// <remarks>Requires the directory lock. A writer that ignores the lock can still race the final unlink.</remarks>
    public void DeleteRecoveryFile(string name, SafeFileHandle expectedTarget, Action verifyTarget)
    {
        ArgumentNullException.ThrowIfNull(expectedTarget);
        ArgumentNullException.ThrowIfNull(verifyTarget);
        RequireExclusive();
        VerifyChildIdentity(name, expectedTarget);
        verifyTarget();
        VerifyChildIdentity(name, expectedTarget);
        if (UnlinkAt(_handle, name, 0) != 0) throw new IOException("Cannot remove the restore target.");
        Verify();
    }

    private void RequireExclusive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_exclusive) throw new InvalidOperationException("Recovery replacement or deletion requires exclusive directory ownership.");
    }

    private void PublishRecoveryFile(string name, ReadOnlyMemory<byte> content, ReadOnlyMemory<byte> metadata,
        SafeFileHandle? expectedTarget, Action? verifyTarget)
    {
        Verify(); CheckName(name);
        if (content.Length > 64 * 1024 * 1024) throw new IOException("Recovery file exceeds the supported size.");
        if (expectedTarget is not null) VerifyChildIdentity(name, expectedTarget);
        var staging = ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        var descriptor = OpenAt(_handle, staging, 2 | 0x40 | 0x80 | 0x20000 | 0x80000, 0x180);
        if (descriptor < 0) throw new IOException("Cannot create private recovery staging.");
        using var output = new FileStream(new SafeFileHandle((IntPtr)descriptor, true), FileAccess.ReadWrite);
        try
        {
            output.Write(content.Span); output.Flush(true);
            LinuxRecoveryMetadata.Apply(output.SafeFileHandle, metadata.Span); output.Flush(true);
            if (output.Length != content.Length) throw new IOException("Recovery staging length changed.");
            output.Position = 0;
            var buffer = new byte[65536];
            try
            {
                var offset = 0;
                while (offset < content.Length)
                {
                    var count = output.Read(buffer, 0, Math.Min(buffer.Length, content.Length - offset));
                    if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(content.Span.Slice(offset, count)))
                        throw new IOException("Recovery staging verification failed.");
                    offset += count;
                }
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(buffer); }
            verifyTarget?.Invoke();
            Verify();
            if (!MatchesChild(staging, output.SafeFileHandle)) throw new IOException("Recovery staging identity changed.");
            if (expectedTarget is not null && !MatchesChild(name, expectedTarget))
                throw new IOException("Recovery target identity changed before publication.");
            if (RenameAt(_handle, staging, _handle, name, expectedTarget is null ? 1u : 0u) != 0)
                throw new IOException("Recovery destination cannot be published.");
            if (!MatchesChild(name, output.SafeFileHandle)) throw new IOException("Published recovery identity changed.");
            Verify();
        }
        finally { CleanupOwnedChild(staging, output.SafeFileHandle); }
    }

    /// <summary>Removes an observed owned staging child; cleanup failure must remain visible to the recovery caller.</summary>
    private void CleanupOwnedChild(string name, SafeFileHandle owned)
    {
        if (MatchesChild(name, owned) && UnlinkAt(_handle, name, 0) != 0)
            throw new IOException("Recovery staging cleanup failed; recovery artifacts require attention.");
    }

    private bool MatchesChild(string name, SafeFileHandle owned)
    {
        if (Statx(owned, "", 0x1000, 0x105, out var first) != 0 ||
            Statx(_handle, name, 0x100, 0x105, out var second) != 0) return false;
        return (first.Mask & 0x105) == 0x105 && (second.Mask & 0x105) == 0x105 &&
            first.LinkCount == 1 && second.LinkCount == 1 && (second.Mode & 0xF000) == 0x8000 &&
            first.Inode == second.Inode && first.DeviceMajor == second.DeviceMajor && first.DeviceMinor == second.DeviceMinor;
    }

    /// <summary>Enumerates names beneath the retained directory. Callers must recheck identity and changes after reading.</summary>
    internal IEnumerable<string> EnumerateChildNames()
    {
        Verify();
        return Directory.EnumerateFileSystemEntries(_path).Select(path => Path.GetFileName(path)!);
    }

    /// <summary>Opens a single directory child without following a link.</summary>
    internal RecoveryDirectoryIdentity OpenDirectoryChild(string name)
    {
        Verify();
        CheckName(name);
        var descriptor = OpenAt(_handle, name, 0x10000 | 0x20000 | 0x80000, 0);
        if (descriptor < 0) throw new IOException("Cannot open recovery directory child.");
        var handle = new SafeFileHandle((IntPtr)descriptor, true);
        try
        {
            var child = new RecoveryDirectoryIdentity(Path.Combine(_path, name), handle);
            VerifyDirectoryChild(name, child);
            return child;
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>Creates a new mode-0700 child relative to this retained directory.</summary>
    internal RecoveryDirectoryIdentity CreatePrivateDirectory(string name)
    {
        Verify();
        CheckName(name);
        if (MkdirAt(_handle, name, 0x1c0) != 0)
            throw new IOException("Cannot create private recovery staging directory.");
        return OpenDirectoryChild(name);
    }

    /// <summary>Checks the directory entry against a retained handle, even after that handle's original path moved.</summary>
    internal void VerifyDirectoryChild(string name, RecoveryDirectoryIdentity child)
    {
        Verify();
        CheckName(name);
        if (Statx(_handle, name, 0x100, 0x101, out var status) != 0 ||
            (status.Mask & 0x101) != 0x101 || (status.Mode & 0xf000) != 0x4000 ||
            (status.DeviceMajor, status.DeviceMinor, status.Inode) != child._identity)
            throw new IOException("Recovery directory child changed identity.");
    }

    /// <summary>Compares identities of two retained handles without reopening either stored pathname.</summary>
    internal bool MatchesRetainedIdentity(RecoveryDirectoryIdentity other)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(other._disposed, other);
        return _identity == other._identity;
    }

    /// <summary>Exchanges two observed directory entries atomically while the target parent remains locked.</summary>
    internal void ExchangeDirectoryChild(string name, RecoveryDirectoryIdentity expected,
        RecoveryDirectoryIdentity stagingParent, string stagingName, RecoveryDirectoryIdentity staged)
    {
        RequireExclusive();
        VerifyDirectoryChild(name, expected);
        stagingParent.VerifyDirectoryChild(stagingName, staged);
        const uint exchange = 2;
        if (RenameAt(_handle, name, stagingParent._handle, stagingName, exchange) != 0)
            throw new IOException("The filesystem could not atomically exchange the recovery directory.");
    }

    /// <summary>Removes an observed directory if it is still the same empty child.</summary>
    internal void RemoveEmptyDirectory(string name, RecoveryDirectoryIdentity directory)
    {
        VerifyDirectoryChild(name, directory);
        const int removeDirectory = 0x200;
        if (UnlinkAt(_handle, name, removeDirectory) != 0)
            throw new IOException("Could not remove the private recovery staging directory.");
    }

    /// <summary>Captures the supported metadata for the retained directory itself.</summary>
    internal byte[] CaptureMetadata()
    {
        Verify();
        return LinuxRecoveryMetadata.CaptureDirectory(_handle);
    }

    /// <summary>Applies and verifies metadata on an unpublished staging directory.</summary>
    internal void ApplyMetadata(ReadOnlySpan<byte> metadata)
    {
        Verify();
        LinuxRecoveryMetadata.ApplyDirectory(_handle, metadata);
    }

    /// <summary>Flushes directory entries after publication or removal.</summary>
    internal void Flush()
    {
        Verify();
        if (Fsync(_handle) != 0) throw new IOException("Could not flush the recovery directory.");
    }

    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(SafeFileHandle directory);

    private static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
            throw new ArgumentException("A single artifact filename is required.");
    }
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags, uint mode);
    [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt(SafeFileHandle oldDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldName,
        SafeFileHandle newDirectory, [MarshalAs(UnmanagedType.LPUTF8Str)] string newName, uint flags);
    [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(SafeFileHandle directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);

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
        [FieldOffset(16)] public uint LinkCount;
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
