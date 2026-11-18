using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Retains original Linux file/directory identity across a recovery operation. Mutation uses an exclusive directory lock and revalidates the retained state.</summary>
public sealed class LinuxRestoreTargetLease : IDisposable
{
    private readonly RecoveryDirectoryIdentity _directory;
    private readonly FileStream? _file;
    private readonly RestoreFileImage _original;
    private readonly string _directoryPath;
    private readonly string _name;
    private bool _disposed;
    private LinuxRestoreTargetLease(RecoveryDirectoryIdentity directory, FileStream? file, RestoreFileImage original, string path, string name)
    { _directory = directory; _file = file; _original = original; _directoryPath = path; _name = name; }
    /// <summary>Captures and retains an original file or observed absence. The caller must authorize scope and closed-browser state.</summary>
    public static LinuxRestoreTargetLease Capture(string directoryPath, string name, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0) throw new ArgumentException("A single target filename is required.");
        var path = Path.GetFullPath(directoryPath);
        var directory = RecoveryDirectoryIdentity.Open(path); FileStream? file = null; RestoreFileImage? original = null;
        try
        {
            var kind = SnapshotFileAccess.Kind(Path.Combine(path, name));
            if (kind == SnapshotNodeKind.RegularFile) file = directory.OpenReadChild(name);
            else if (kind != SnapshotNodeKind.Missing) throw new IOException("Unsafe recovery target.");
            original = LinuxRestoreFileCapture.Read(path, name, token);
            if (original.Exists != (file is not null)) throw new IOException("Recovery target changed during lease capture.");
            var result = new LinuxRestoreTargetLease(directory, file, original, path, name);
            result.VerifyOriginal(token); return result;
        }
        catch { original?.Dispose(); file?.Dispose(); directory.Dispose(); throw; }
    }
    /// <summary>Returns an independently owned copy of the original safety image.</summary>
    public RestoreFileImage CopyOriginal() { ObjectDisposedException.ThrowIf(_disposed, this); return _original.Clone(); }
    /// <summary>Rechecks retained identities and exact original bytes/metadata; never refreshes the original safety state.</summary>
    public void VerifyOriginal(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        _directory.Verify();
        if (_file is not null) _directory.VerifyChildIdentity(_name, _file.SafeFileHandle);
        using var current = LinuxRestoreFileCapture.Read(_directoryPath, _name, token);
        if (current.Exists != _original.Exists || !current.Content.Bytes.Span.SequenceEqual(_original.Content.Bytes.Span) ||
            !current.Metadata.Bytes.Span.SequenceEqual(_original.Metadata.Bytes.Span)) throw new IOException("Recovery target no longer matches its original state.");
        if (_file is not null) _directory.VerifyChildIdentity(_name, _file.SafeFileHandle);
        _directory.Verify();
    }
    /// <summary>Publishes a new state under the directory lock with a final caller-owned context check.</summary>
    internal void Publish(RestoreFileImage image, Action verifyContext, Action adoptPublication)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var writer = RecoveryDirectoryIdentity.Open(_directoryPath);
        writer.AcquireExclusive();
        if (!writer.RefersToSameDirectory(_directory)) throw new IOException("Restore target directory changed.");

        void VerifyBeforeWrite()
        {
            verifyContext();
            VerifyOriginal();
        }

        VerifyBeforeWrite();
        if (_file is null)
        {
            if (image.Exists)
                writer.PublishNewRecoveryFile(_name, image.Content.Bytes, image.Metadata.Bytes, VerifyBeforeWrite);
        }
        else if (image.Exists)
        {
            writer.ReplaceRecoveryFile(_name, _file.SafeFileHandle, image.Content.Bytes, image.Metadata.Bytes, VerifyBeforeWrite);
        }
        else
        {
            writer.DeleteRecoveryFile(_name, _file.SafeFileHandle, VerifyBeforeWrite);
        }
        adoptPublication();
    }

    /// <summary>Ensures both checkpoints belong to the same retained destination directory.</summary>
    internal bool SharesDirectoryWith(LinuxRestoreTargetLease other) => _directory.RefersToSameDirectory(other._directory);

    /// <summary>Releases native handles and original buffers without modifying targets or safety artifacts.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _original.Dispose(); _file?.Dispose(); _directory.Dispose();
    }
}
