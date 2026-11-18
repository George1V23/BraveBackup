using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Target file state retained by the transaction store: verified reads and guarded publication.</summary>
public interface IRestoreTargetState : IDisposable
{
    /// <summary>Returns an owned copy of the verified current target; an unexpected change throws.</summary>
    RestoreFileImage ReadCurrent(CancellationToken token = default);
    /// <summary>Replaces, creates or deletes the target only from its retained state and adopts the result after exact verification.</summary>
    void Publish(RestoreFileImage image, CancellationToken token = default);
}
/// <summary>Operation-owned encrypted recovery artifacts (safety and journal) with ownership checks.</summary>
public interface IRestoreArtifacts
{
    void Verify();
    void SaveSafety(ReadOnlyMemory<byte> encrypted);
    byte[] ReadSafety();
    void SaveJournal(ReadOnlyMemory<byte> encrypted);
    byte[] ReadJournal();
}

/// <summary>Platform-neutral <see cref="IRestoreTransactionStore"/> over retained per-target state and owned recovery artifacts.</summary>
/// <remarks>
/// Use <see cref="Create"/> for the host platform. Targets are single files directly inside authorized directories and are retained
/// from construction. Writes act only on the retained file identity, so external edits or replacements are reported instead of
/// overwritten. The host must authenticate the archive, obtain final approval and keep the artifacts alive. Context verification
/// rereads the source archive on every call. Windows support is not natively validated.
/// </remarks>
public sealed class RestoreTransactionStore : IRestoreTransactionStore, IDisposable
{
    private readonly BraveRestoreContext _context;
    private readonly IRestoreArtifacts _artifacts;
    private readonly Dictionary<Guid, IRestoreTargetState> _targets = [];
    private bool _disposed;

    /// <summary>Creates a store for the host OS. <paramref name="artifacts"/> must come from the same platform.</summary>
    public static RestoreTransactionStore Create(BraveRestoreContext context, IRestoreArtifacts artifacts,
        IReadOnlyDictionary<Guid, (string Directory, string Name)> targets, CancellationToken token = default)
    {
        if (OperatingSystem.IsLinux()) return new(context, artifacts, targets, (d, n, t) => new LinuxRestoreExpectedState(d, n, t), token);
        if (OperatingSystem.IsWindows()) return new(context, artifacts, targets, (d, n, t) => new WindowsRestoreExpectedState(d, n, t), token);
        throw new PlatformNotSupportedException();
    }

    /// <summary>Captures every target's original state; nothing is written. Disposes partial captures on failure.</summary>
    public RestoreTransactionStore(BraveRestoreContext context, IRestoreArtifacts artifacts,
        IReadOnlyDictionary<Guid, (string Directory, string Name)> targets,
        Func<string, string, CancellationToken, IRestoreTargetState> openTarget, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(artifacts); ArgumentNullException.ThrowIfNull(openTarget);
        if (targets.Count == 0) throw new ArgumentException("At least one restore target is required.");
        _context = context; _artifacts = artifacts;
        try
        {
            context.Verify(token);
            var seen = new HashSet<(string, string)>(HostEnvironment.IsWindows ? new CaseInsensitivePair() : EqualityComparer<(string, string)>.Default);
            foreach (var (id, target) in targets)
            {
                if (!seen.Add((Path.GetFullPath(target.Directory), target.Name))) throw new IOException("Restore targets must be distinct.");
                _targets.Add(id, openTarget(target.Directory, target.Name, token));
            }
        }
        catch { Dispose(); throw; }
    }

    private sealed class CaseInsensitivePair : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            string.Equals(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) value) => HashCode.Combine(value.Item1.ToUpperInvariant(), value.Item2.ToUpperInvariant());
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

    private IRestoreTargetState Target(Guid id)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _targets.TryGetValue(id, out var state) ? state : throw new KeyNotFoundException("Unknown restore target.");
    }
    /// <summary>Releases target handles and buffers; the artifacts remain owned by the caller.</summary>
    public void Dispose()
    { if (_disposed) return; _disposed = true; foreach (var state in _targets.Values) state.Dispose(); _targets.Clear(); }
}
