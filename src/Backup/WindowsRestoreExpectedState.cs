using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Windows retained target state (file or absence) with guarded publication. Not natively validated.</summary>
/// <remarks>
/// Last-access time inside opaque metadata is ignored when comparing, because reads may update it. The original baseline is owned
/// by the safety snapshot, not by this class. See <see cref="WindowsRecoveryFileTarget"/> for the replace race limits.
/// </remarks>
public sealed class WindowsRestoreExpectedState : IRestoreTargetState
{
    private readonly string _directory;
    private readonly string _name;
    private WindowsRecoveryFileTarget _current;
    private RestoreFileImage _expected;
    private bool _disposed;

    /// <summary>Captures and retains the target's original state without writing.</summary>
    public WindowsRestoreExpectedState(string directory, string name, CancellationToken token = default)
    {
        _directory = Path.GetFullPath(directory); _name = name;
        _current = WindowsRecoveryFileTarget.Open(_directory, name);
        try { _expected = Read(_current, token); }
        catch { _current.Dispose(); throw; }
    }

    public RestoreFileImage ReadCurrent(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var actual = Read(_current, token);
        if (!Same(actual, _expected)) throw new IOException("Recovery target no longer matches its expected state.");
        return _expected.Clone();
    }

    public void Publish(RestoreFileImage image, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using (ReadCurrent(token)) { }
        if (image.Exists) _current.Publish(image.Content.Bytes.Span, image.Metadata.Bytes.Span);
        else _current.DeleteRetained();
        var candidate = WindowsRecoveryFileTarget.Open(_directory, _name);
        try
        {
            var actual = Read(candidate, token);
            if (!Same(actual, image)) { actual.Dispose(); throw new IOException("Published target does not match the frozen expected image."); }
            var previous = _current; var previousImage = _expected;
            _current = candidate; _expected = actual; previous.Dispose(); previousImage.Dispose();
        }
        catch { candidate.Dispose(); throw; }
    }

    private static RestoreFileImage Read(WindowsRecoveryFileTarget target, CancellationToken token)
    {
        var content = target.ReadContent(token); var metadata = target.ReadMetadata();
        try
        {
            var image = new RestoreFileImage(target.Exists, content, metadata);
            target.Verify(); return image;
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(content); System.Security.Cryptography.CryptographicOperations.ZeroMemory(metadata); }
    }

    private static bool Same(RestoreFileImage first, RestoreFileImage second)
    {
        if (first.Exists != second.Exists || !first.Content.Bytes.Span.SequenceEqual(second.Content.Bytes.Span)) return false;
        var a = first.Metadata.Bytes.Span; var b = second.Metadata.Bytes.Span;
        if (a.Length != b.Length) return false;
        if (a.IsEmpty) return true;
        // Bytes 24..32 hold last-access ticks; every other metadata byte must match exactly.
        return a[..24].SequenceEqual(b[..24]) && a[32..].SequenceEqual(b[32..]);
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _current.Dispose(); _expected.Dispose(); }
}
