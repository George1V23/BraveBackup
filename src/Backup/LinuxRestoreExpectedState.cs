namespace BraveBackup.Core;

/// <summary>Owned expected-state checkpoint, separate from immutable original safety state. Retains the expected state through guarded publication.</summary>
public sealed class LinuxRestoreExpectedState : ILinuxRestoreTarget
{
    private readonly string _directory;
    private readonly string _name;
    private LinuxRestoreTargetLease _current;
    private readonly RestoreFileImage _original;
    private bool _disposed;
    public LinuxRestoreExpectedState(string directory, string name, CancellationToken token = default)
    {
        _directory = Path.GetFullPath(directory); _name = name;
        _current = LinuxRestoreTargetLease.Capture(_directory, name, token);
        try { _original = _current.CopyOriginal(); } catch { _current.Dispose(); throw; }
    }
    string ILinuxRestoreTarget.CaptureRestartDirectoryEvidence() => _current.CaptureRestartDirectoryEvidence();
    long ILinuxRestoreTarget.ImageBytes => _original.Content.Bytes.Length + _original.Metadata.Bytes.Length;

    /// <summary>Returns the original recovery baseline; accepting applied state never replaces it.</summary>
    public RestoreFileImage CopyOriginal() { ObjectDisposedException.ThrowIf(_disposed, this); return _original.Clone(); }
    /// <summary>Verifies the currently expected physical target identity and contents.</summary>
    public void VerifyCurrent(CancellationToken token = default)
    { ObjectDisposedException.ThrowIf(_disposed, this); _current.VerifyOriginal(token); }
    /// <summary>Reads the verified expected image for the coordinator. The caller owns the returned copy.</summary>
    public RestoreFileImage ReadCurrent(CancellationToken token = default)
    {
        VerifyCurrent(token);
        return _current.CopyOriginal();
    }

    /// <summary>Publishes and adopts an explicitly authorized target image under the Linux directory guard.</summary>
    /// <remarks>The coordinator must have verified the safety artifact and persisted write intent first.</remarks>
    public void Publish(RestoreFileImage image, Action verifyContext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(verifyContext);
        _current.Publish(image, verifyContext, () => AcceptPublished(image));
    }

    /// <summary>After an explicit caller-controlled write, validates the exact expected image before retaining its new identity.</summary>
    /// <remarks>This observation is not write authorization or proof of who made the change; it must not be used to adopt arbitrary external edits.</remarks>
    public void AcceptPublished(RestoreFileImage expected, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var candidate = LinuxRestoreTargetLease.Capture(_directory, _name, token);
        try
        {
            if (!_current.SharesDirectoryWith(candidate))
                throw new IOException("Published target directory changed.");
            using var actual = candidate.CopyOriginal();
            if (actual.Exists != expected.Exists || !actual.Content.Bytes.Span.SequenceEqual(expected.Content.Bytes.Span) ||
                !actual.Metadata.Bytes.Span.SequenceEqual(expected.Metadata.Bytes.Span))
                throw new IOException("Published target does not match the frozen expected image.");
            candidate.VerifyOriginal(token);
            var previous = _current; _current = candidate; previous.Dispose();
        }
        catch { candidate.Dispose(); throw; }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _current.Dispose(); _original.Dispose(); }
}
