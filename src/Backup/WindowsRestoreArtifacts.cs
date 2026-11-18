using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Windows recovery-artifact ownership: retains the artifact directory's ancestors and uses the scope's fixed paths.</summary>
/// <remarks>
/// Weaker than the Linux lease: no exclusive-lock equivalent, and publication is path-based (see <see cref="RestoreArtifactStorage"/>),
/// so artifacts could be replaced by a non-cooperating process between operations. The coordinator's authentication of the
/// safety artifact and journal readback detects tampering. Not natively validated.
/// </remarks>
public sealed class WindowsRestoreArtifacts : IRestoreArtifacts, IDisposable
{
    private readonly RestoreArtifactScope _scope;
    private readonly WindowsRecoveryDirectoryChain _chain;
    private bool _disposed;
    private WindowsRestoreArtifacts(RestoreArtifactScope scope, WindowsRecoveryDirectoryChain chain) { _scope = scope; _chain = chain; }
    /// <summary>Retains the recovery directory chain without creating artifacts.</summary>
    public static WindowsRestoreArtifacts Acquire(RestoreArtifactScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope); scope.Validate();
        var chain = WindowsRecoveryDirectoryChain.Open(Path.GetDirectoryName(scope.SafetyPath)!);
        try { scope.Validate(); return new(scope, chain); } catch { chain.Dispose(); throw; }
    }
    public void Verify() { ObjectDisposedException.ThrowIf(_disposed, this); _scope.Validate(); _chain.Verify(); }
    public void SaveSafety(ReadOnlyMemory<byte> encrypted) { Verify(); _scope.SaveSafety(encrypted); }
    public byte[] ReadSafety() { Verify(); return _scope.ReadSafety(); }
    public void SaveJournal(ReadOnlyMemory<byte> encrypted) { Verify(); _scope.SaveJournal(encrypted); }
    public byte[] ReadJournal() { Verify(); return _scope.ReadJournal(); }
    /// <summary>Releases retained handles without deleting recovery artifacts.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _chain.Dispose(); }
}
