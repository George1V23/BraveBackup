using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Retains original Linux file/directory identity across a recovery operation. Target changes only happen through the explicit guarded <see cref="PublishFromRetained"/> call.</summary>
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
    /// <summary>Changes the target from this lease's retained state to <paramref name="image"/> (replace, create or delete).</summary>
    /// <remarks>
    /// Verifies retained state first and acts only on the retained file identity. The caller must authorize scope, enforce
    /// closed-browser state and then adopt the result (see <see cref="LinuxRestoreExpectedState"/>). Existing files require valid metadata.
    /// </remarks>
    public void PublishFromRetained(RestoreFileImage image, CancellationToken token = default)
    {
        VerifyOriginal(token);
        if (_file is not null)
        {
            if (image.Exists) _directory.ReplaceChildWithRecoveryFile(_name, image.Content.Bytes, image.Metadata.Bytes, _file.SafeFileHandle);
            else _directory.DeleteChild(_name, _file.SafeFileHandle);
        }
        else if (image.Exists) _directory.PublishNewRecoveryFile(_name, image.Content.Bytes, image.Metadata.Bytes);
    }
    /// <summary>Releases native handles and original buffers without modifying targets or safety artifacts.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _original.Dispose(); _file?.Dispose(); _directory.Dispose();
    }
}
