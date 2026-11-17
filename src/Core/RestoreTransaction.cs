using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BraveBackup.Core;

public enum RestoreTransactionState { New, Prepared, Running, Paused, Completed, RollingBack, RolledBack, Failed, RecoveryIncomplete }
public enum RestoreEntryState { Untouched, Intent, Applied, Recovered }
/// <summary>Frozen mutation identity and proposed image; the transaction clones the image before using it.</summary>
public sealed record RestoreMutation(Guid Id, Guid InstallationId, Guid ProfileId, string Category, RestoreFileImage Replacement);
/// <summary>Value-free progress. Category completion requires all its file mutations to finish.</summary>
public sealed record RestoreTransactionProgress(RestoreTransactionState State, Guid InstallationId, Guid ProfileId,
    string Category, int CompletedFiles, int TotalFiles, bool CategoryCompleted);
/// <summary>Encrypted storage and context boundary; adapters must implement private durable artifact publication and atomic target writes.</summary>
public interface IRestoreTransactionStore
{
    void VerifyContext(CancellationToken token);
    RestoreFileImage ReadTarget(Guid mutationId, CancellationToken token);
    void WriteTargetAtomic(Guid mutationId, RestoreFileImage image);
    void SaveSafety(ReadOnlyMemory<byte> encryptedBytes);
    byte[] ReadSafety();
    void SaveJournal(ReadOnlyMemory<byte> encryptedBytes);
    byte[] ReadJournal();
}
/// <summary>Exact journal codec used inside the authenticated envelope; contains only logical IDs and states.</summary>
public static class RestoreJournalFormat
{
    public static byte[] Encode(Guid operation, long revision, RestoreTransactionState state,
        IReadOnlyList<(Guid Id, RestoreEntryState State)> entries)
    {
        RestoreSafetyFormat.ValidateIdentity(operation, entries.Select(e => e.Id));
        if (revision < 0 || !Enum.IsDefined(state) || entries.Any(e => !Enum.IsDefined(e.State))) throw Invalid();
        var bytes = new byte[40 + entries.Count * 20];
        "BBKJR001"u8.CopyTo(bytes); operation.TryWriteBytes(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), revision);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), (int)state);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            entries[index].Id.TryWriteBytes(bytes.AsSpan(40 + index * 20));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56 + index * 20), (int)entries[index].State);
        }
        return bytes;
    }
    public static (Guid Operation, long Revision, RestoreTransactionState State, (Guid Id, RestoreEntryState State)[] Entries)
        Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 40 || !bytes[..8].SequenceEqual("BBKJR001"u8)) throw Invalid();
        var operation = new Guid(bytes.Slice(8, 16)); var revision = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
        var state = (RestoreTransactionState)BinaryPrimitives.ReadInt32LittleEndian(bytes[32..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);
        if (revision < 0 || !Enum.IsDefined(state) || count is < 1 or > 10000 || bytes.Length != 40 + count * 20) throw Invalid();
        var entries = new (Guid Id, RestoreEntryState State)[count];
        for (var index = 0; index < count; index++)
        {
            entries[index] = (new Guid(bytes.Slice(40 + index * 20, 16)), (RestoreEntryState)BinaryPrimitives.ReadInt32LittleEndian(bytes[(56 + index * 20)..]));
            if (!Enum.IsDefined(entries[index].State)) throw Invalid();
        }
        RestoreSafetyFormat.ValidateIdentity(operation, entries.Select(e => e.Id));
        return (operation, revision, state, entries);
    }
    private static InvalidDataException Invalid() => new("Invalid restore journal.");
}
/// <summary>Format-independent restore coordinator, not connected to browser writers. Only injected stores perform I/O.</summary>
public sealed class RestoreTransaction : IDisposable
{
    private readonly RestoreMutation[] _mutations;
    private readonly IRestoreTransactionStore _store;
    private readonly RestoreEntryState[] _entries;
    private readonly object _execution = new();
    private RestoreSafetySnapshot? _snapshot;
    private string? _password;
    private string? _safetyHash;
    private long _revision;
    private int _pause;
    private bool _disposed;
    public Guid OperationId { get; } = Guid.NewGuid();
    public RestoreTransactionState State { get; private set; }
    public IReadOnlyList<RestoreEntryState> EntryStates => Array.AsReadOnly(_entries);
    public Action<RestoreTransactionProgress>? Progress { get; set; }

    /// <summary>Freezes identities/content in owned buffers. The safety password is separate from source archive handling.</summary>
    public RestoreTransaction(IReadOnlyList<RestoreMutation> mutations, IRestoreTransactionStore store, string safetyPassword)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.IsNullOrEmpty(safetyPassword)) throw new ArgumentException("A safety encryption password is required.");
        RestoreSafetyFormat.ValidateIdentity(OperationId, mutations.Select(m => m.Id));
        _store = store; _password = safetyPassword; _entries = new RestoreEntryState[mutations.Count];
        var owned = new List<RestoreMutation>();
        try
        {
            long bytes = 0;
            foreach (var mutation in mutations)
            {
                if (mutation.InstallationId == Guid.Empty || mutation.ProfileId == Guid.Empty || mutation.Category.Length is < 1 or > 64 ||
                    mutation.Category.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')) throw new InvalidDataException("Invalid restore mutation.");
                bytes += mutation.Replacement.Content.Bytes.Length + mutation.Replacement.Metadata.Bytes.Length;
                if (bytes > RestoreSafetyFormat.MaximumBytes) throw new InvalidDataException("Restore plan exceeds retention limit.");
                owned.Add(mutation with { Replacement = mutation.Replacement.Clone() });
            }
            _mutations = owned.ToArray();
        }
        catch { foreach (var mutation in owned) mutation.Replacement.Dispose(); throw; }
    }

    /// <summary>Captures all original state, publishes encrypted safety and authenticates/compares its readback before allowing writes.</summary>
    public void Prepare(CancellationToken token = default)
    {
        lock (_execution)
        {
            Ensure(RestoreTransactionState.New);
            var originals = new List<RestoreSafetyEntry>();
            try
            {
                long retained = 0; _store.VerifyContext(token);
                foreach (var mutation in _mutations)
                {
                    token.ThrowIfCancellationRequested();
                    _store.VerifyContext(token);
                    var image = _store.ReadTarget(mutation.Id, token);
                    originals.Add(new(mutation.Id, mutation.InstallationId, mutation.ProfileId, mutation.Category, image));
                    retained += image.Content.Bytes.Length + image.Metadata.Bytes.Length;
                    if (retained > RestoreSafetyFormat.MaximumBytes) throw new InvalidDataException("Safety originals exceed retention limit.");
                }
                _snapshot = new(OperationId, originals);
                using var plain = RestoreSafetyFormat.Encode(_snapshot, token);
                _safetyHash = Convert.ToHexStringLower(SHA256.HashData(plain.Bytes.Span));
                var encrypted = Encrypt(plain.Bytes.Span, token);
                try { _store.SaveSafety(encrypted); }
                finally { CryptographicOperations.ZeroMemory(encrypted); }
                var readback = _store.ReadSafety();
                try
                {
                    using var decodedBytes = Decrypt(readback, token);
                    using var verified = RestoreSafetyFormat.Decode(decodedBytes.Bytes.Span, token);
                    if (verified.OperationId != OperationId || verified.Entries.Count != originals.Count) throw new InvalidDataException("Safety verification failed.");
                    for (var index = 0; index < originals.Count; index++)
                    {
                        var first = originals[index]; var second = verified.Entries[index];
                        if (first.Id != second.Id || first.InstallationId != second.InstallationId || first.ProfileId != second.ProfileId || first.Category != second.Category ||
                            !Same(first.Original, second.Original)) throw new InvalidDataException("Safety verification failed.");
                    }
                }
                finally { CryptographicOperations.ZeroMemory(readback); }
                _store.VerifyContext(token); State = RestoreTransactionState.Prepared; Journal();
            }
            catch
            {
                if (_snapshot is null) foreach (var original in originals) original.Original.Dispose();
                State = RestoreTransactionState.Failed; throw;
            }
        }
    }

    /// <summary>Requests pausing after the current atomic operation. Does not roll back or resume automatically.</summary>
    public void RequestPause() => Interlocked.Exchange(ref _pause, 1);

    /// <summary>Applies prepared work or explicitly resumes paused work; all originals are retained across pauses.</summary>
    public void Run()
    {
        lock (_execution)
        {
            if (_disposed || State is not (RestoreTransactionState.Prepared or RestoreTransactionState.Paused)) throw new InvalidOperationException("Restore cannot run in this state.");
            // A failed resume preflight keeps the operation paused and never refreshes originals.
            VerifySafety();
            _store.VerifyContext(CancellationToken.None);
            for (var index = 0; index < _mutations.Length; index++)
            {
                using var actual = _store.ReadTarget(_mutations[index].Id, CancellationToken.None);
                var expected = _entries[index] == RestoreEntryState.Applied ? _mutations[index].Replacement : _snapshot!.Entries[index].Original;
                if (!Same(actual, expected)) throw new IOException("Restore target changed; no conflicting data was overwritten.");
            }
            _store.VerifyContext(CancellationToken.None);
            try
            {
                if (State == RestoreTransactionState.Paused) Interlocked.Exchange(ref _pause, 0);
                State = RestoreTransactionState.Running; Journal();
                for (var index = 0; index < _mutations.Length; index++)
                {
                    if (_entries[index] == RestoreEntryState.Applied) continue;
                    if (Volatile.Read(ref _pause) != 0) { State = RestoreTransactionState.Paused; Journal(); return; }
                    var mutation = _mutations[index];
                    _store.VerifyContext(CancellationToken.None);
                    using (var current = _store.ReadTarget(mutation.Id, CancellationToken.None))
                        if (!Same(current, _snapshot!.Entries[index].Original)) throw new IOException("Restore target changed; no conflicting data was overwritten.");
                    _entries[index] = RestoreEntryState.Intent; Journal();
                    _store.VerifyContext(CancellationToken.None);
                    if (Volatile.Read(ref _pause) != 0) { State = RestoreTransactionState.Paused; Journal(); return; }
                    _store.WriteTargetAtomic(mutation.Id, mutation.Replacement);
                    using (var published = _store.ReadTarget(mutation.Id, CancellationToken.None))
                        if (!Same(published, mutation.Replacement)) throw new IOException("Restore publication verification failed.");
                    _entries[index] = RestoreEntryState.Applied; Journal(); Report(mutation);
                }
                State = Volatile.Read(ref _pause) == 0 ? RestoreTransactionState.Completed : RestoreTransactionState.Paused;
                Journal();
            }
            catch { State = RestoreTransactionState.Failed; TryJournal(); throw; }
        }
    }

    /// <summary>Rolls back only after explicit caller approval; conflicts/failures are retained and reported as incomplete recovery.</summary>
    public void Rollback(bool explicitlyApproved)
    {
        lock (_execution)
        {
            if (!explicitlyApproved) return;
            if (_disposed || _snapshot is null || State is not (RestoreTransactionState.Paused or RestoreTransactionState.Failed or RestoreTransactionState.Completed or RestoreTransactionState.RecoveryIncomplete))
                throw new InvalidOperationException("Rollback cannot run in this state.");
            State = RestoreTransactionState.RollingBack;
            try { VerifySafety(); _store.VerifyContext(CancellationToken.None); Journal(); }
            catch { State = RestoreTransactionState.RecoveryIncomplete; TryJournal(); throw; }
            var failed = false;
            for (var index = _mutations.Length - 1; index >= 0; index--)
            {
                if (_entries[index] is RestoreEntryState.Untouched or RestoreEntryState.Recovered) continue;
                var mutation = _mutations[index]; var original = _snapshot.Entries[index].Original;
                try
                {
                    _store.VerifyContext(CancellationToken.None);
                    using var current = _store.ReadTarget(mutation.Id, CancellationToken.None);
                    if (!Same(current, original))
                    {
                        if (!Same(current, mutation.Replacement)) throw new IOException("Rollback target conflict.");
                        // Recovery intent is already represented by the persisted touched entry and RollingBack state.
                        Journal(); _store.VerifyContext(CancellationToken.None); _store.WriteTargetAtomic(mutation.Id, original);
                        using var restored = _store.ReadTarget(mutation.Id, CancellationToken.None);
                        if (!Same(restored, original)) throw new IOException("Rollback publication verification failed.");
                    }
                    _entries[index] = RestoreEntryState.Recovered; Journal(); Report(mutation);
                }
                catch { failed = true; }
            }
            State = failed ? RestoreTransactionState.RecoveryIncomplete : RestoreTransactionState.RolledBack;
            try { Journal(); } catch { State = RestoreTransactionState.RecoveryIncomplete; throw; }
        }
    }
    /// <summary>Reauthenticates the retained safety artifact before executing or recovering a transaction.</summary>
    private void VerifySafety()
    {
        var bytes = _store.ReadSafety();
        try
        {
            using var plaintext = Decrypt(bytes, CancellationToken.None);
            if (Convert.ToHexStringLower(SHA256.HashData(plaintext.Bytes.Span)) != _safetyHash)
                throw new InvalidDataException("The verified safety artifact changed.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void Report(RestoreMutation mutation)
    {
        var group = Enumerable.Range(0, _mutations.Length).Where(i => _mutations[i].InstallationId == mutation.InstallationId &&
            _mutations[i].ProfileId == mutation.ProfileId && _mutations[i].Category == mutation.Category).ToArray();
        var recovered = State == RestoreTransactionState.RollingBack;
        var wanted = recovered ? RestoreEntryState.Recovered : RestoreEntryState.Applied;
        Progress?.Invoke(new(State, mutation.InstallationId, mutation.ProfileId, mutation.Category,
            _entries.Count(e => e == wanted), _entries.Length, group.All(i => _entries[i] == wanted)));
    }
    private void Journal()
    {
        var plaintext = RestoreJournalFormat.Encode(OperationId, ++_revision, State,
            _mutations.Select((m, index) => (m.Id, _entries[index])).ToArray());
        try
        {
            var encrypted = Encrypt(plaintext, CancellationToken.None);
            try
            {
                _store.SaveJournal(encrypted);
                var readback = _store.ReadJournal();
                try
                {
                    using var verified = Decrypt(readback, CancellationToken.None);
                    if (!verified.Bytes.Span.SequenceEqual(plaintext)) throw new InvalidDataException("Journal verification failed.");
                    RestoreJournalFormat.Decode(verified.Bytes.Span);
                }
                finally { CryptographicOperations.ZeroMemory(readback); }
            }
            finally { CryptographicOperations.ZeroMemory(encrypted); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    private void TryJournal() { try { Journal(); } catch { } }
    private byte[] Encrypt(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        using var output = new MemoryStream();
        try
        {
            using (var writer = ArchiveEncryption.CreateWriter(output, _password!, token)) writer.Write(bytes);
            return output.ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(output.GetBuffer()); }
    }
    private SensitiveBytes Decrypt(byte[] bytes, CancellationToken token)
    {
        using var source = new MemoryStream(bytes, writable: false);
        if (!ArchiveEncryption.IsEncrypted(source)) throw new InvalidDataException("Safety artifact must be encrypted.");
        using var reader = ArchiveEncryption.OpenReader(source, _password!, token);
        if (reader.Length > RestoreSafetyFormat.MaximumBytes) throw new InvalidDataException("Safety artifact exceeds its limit.");
        var plain = new byte[(int)reader.Length];
        try { reader.ReadExactly(plain); return new SensitiveBytes(plain); }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }
    private static bool Same(RestoreFileImage first, RestoreFileImage second) => first.Exists == second.Exists &&
        first.Content.Bytes.Length == second.Content.Bytes.Length && first.Metadata.Bytes.Length == second.Metadata.Bytes.Length &&
        CryptographicOperations.FixedTimeEquals(first.Content.Bytes.Span, second.Content.Bytes.Span) &&
        CryptographicOperations.FixedTimeEquals(first.Metadata.Bytes.Span, second.Metadata.Bytes.Span);
    private void Ensure(RestoreTransactionState state)
    { if (_disposed || State != state) throw new InvalidOperationException("Restore cannot run in this state."); }
    /// <summary>Releases owned secret buffers/password references; persisted recovery artifacts remain owned by the store.</summary>
    public void Dispose()
    {
        RequestPause(); lock (_execution)
        {
            if (_disposed) return; _disposed = true; _password = null; Progress = null;
            _snapshot?.Dispose(); foreach (var mutation in _mutations) mutation.Replacement.Dispose();
        }
    }
}
