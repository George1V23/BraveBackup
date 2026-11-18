namespace BraveBackup.Core;

/// <summary>Owned expected-state checkpoint, separate from immutable original safety state. Target changes occur only through the explicit <see cref="Publish"/> call.</summary>
public sealed class LinuxRestoreExpectedState : IDisposable
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
    /// <summary>Returns the original recovery baseline; accepting applied state never replaces it.</summary>
    public RestoreFileImage CopyOriginal() { ObjectDisposedException.ThrowIf(_disposed, this); return _original.Clone(); }
    /// <summary>Verifies the currently expected physical target identity and contents.</summary>
    public void VerifyCurrent(CancellationToken token = default)
    { ObjectDisposedException.ThrowIf(_disposed, this); _current.VerifyOriginal(token); }
    /// <summary>After an explicit caller-controlled write, validates the exact expected image before retaining its new identity.</summary>
    /// <remarks>This observation is not write authorization or proof of who made the change; it must not be used to adopt arbitrary external edits.</remarks>
    public void AcceptPublished(RestoreFileImage expected, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var candidate = LinuxRestoreTargetLease.Capture(_directory, _name, token);
        try
        {
            using var actual = candidate.CopyOriginal();
            if (actual.Exists != expected.Exists || !actual.Content.Bytes.Span.SequenceEqual(expected.Content.Bytes.Span) ||
                !actual.Metadata.Bytes.Span.SequenceEqual(expected.Metadata.Bytes.Span))
                throw new IOException("Published target does not match the frozen expected image.");
            candidate.VerifyOriginal(token);
            var previous = _current; _current = candidate; previous.Dispose();
        }
        catch { candidate.Dispose(); throw; }
    }
    /// <summary>Returns an owned copy of the currently expected target state after verifying its identity and contents.</summary>
    public RestoreFileImage ReadCurrent(CancellationToken token = default)
    { VerifyCurrent(token); return _current.CopyOriginal(); }
    /// <summary>Publishes <paramref name="image"/> over the verified expected state, then adopts it only after exact verification.</summary>
    /// <remarks>Failure leaves the previous expectation in place, so a later check reports a partial or conflicting change instead of hiding it.</remarks>
    public void Publish(RestoreFileImage image, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _current.PublishFromRetained(image, token);
        AcceptPublished(image, token);
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _current.Dispose(); _original.Dispose(); }
}
