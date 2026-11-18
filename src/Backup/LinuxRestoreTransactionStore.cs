namespace BraveBackup.Core;

/// <summary>Linux <see cref="IRestoreTransactionStore"/>: guarded file targets plus operation-owned encrypted recovery artifacts.</summary>
/// <remarks>
/// Targets are single files directly inside authorized directories and are retained from construction. Writes act only on the
/// retained file identity, so external edits or replacements are reported instead of overwritten. The host must authenticate the
/// archive, obtain final approval and keep the artifact lease alive. Context verification rereads the source archive on every call.
/// </remarks>
public sealed class LinuxRestoreTransactionStore : IRestoreTransactionStore, IDisposable
{
    private readonly BraveRestoreContext _context;
    private readonly RestoreArtifactLease _artifacts;
    private readonly Dictionary<Guid, LinuxRestoreExpectedState> _targets = [];
    private bool _disposed;

    /// <summary>Captures every target's original state; nothing is written. Disposes partial captures on failure.</summary>
    public LinuxRestoreTransactionStore(BraveRestoreContext context, RestoreArtifactLease artifacts,
        IReadOnlyDictionary<Guid, (string Directory, string Name)> targets, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(artifacts);
        if (targets.Count == 0) throw new ArgumentException("At least one restore target is required.");
        _context = context; _artifacts = artifacts;
        try
        {
            context.Verify(token);
            var seen = new HashSet<(string, string)>();
            foreach (var (id, target) in targets)
            {
                if (!seen.Add((Path.GetFullPath(target.Directory), target.Name))) throw new IOException("Restore targets must be distinct.");
                _targets.Add(id, new LinuxRestoreExpectedState(target.Directory, target.Name, token));
            }
        }
        catch { Dispose(); throw; }
    }

    /// <summary>Checks frozen source/paths/browser state and artifact ownership. Per-target conflicts surface on read/write.</summary>
    public void VerifyContext(CancellationToken token) { ObjectDisposedException.ThrowIf(_disposed, this); _context.Verify(token); _artifacts.Verify(); }
    /// <summary>Returns the verified current target image; any unexpected change throws instead of being adopted.</summary>
    public RestoreFileImage ReadTarget(Guid mutationId, CancellationToken token) => Target(mutationId).ReadCurrent(token);
    /// <summary>Replaces, creates or deletes the target only from its retained expected state.</summary>
    public void WriteTargetAtomic(Guid mutationId, RestoreFileImage image) => Target(mutationId).Publish(image);
    public void SaveSafety(ReadOnlyMemory<byte> encryptedBytes) => _artifacts.SaveSafety(encryptedBytes);
    public byte[] ReadSafety() => _artifacts.ReadSafety();
    public void SaveJournal(ReadOnlyMemory<byte> encryptedBytes) => _artifacts.SaveJournal(encryptedBytes);
    public byte[] ReadJournal() => _artifacts.ReadJournal();

    private LinuxRestoreExpectedState Target(Guid id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targets.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException("Unknown restore target.");
    }
    /// <summary>Releases target handles and buffers; the artifact lease remains owned by the caller.</summary>
    public void Dispose()
    { if (_disposed) return; _disposed = true; foreach (var state in _targets.Values) state.Dispose(); _targets.Clear(); }
}
