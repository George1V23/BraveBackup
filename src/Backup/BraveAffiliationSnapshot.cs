using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>On-demand, source-write-free capture of the known profile affiliation cache with stopped-browser checks.</summary>
public sealed class BraveAffiliationSnapshot : IDisposable
{
    public const string DatabaseFileName = "Affiliation Database";
    private readonly string _profile;
    private readonly Func<ProcessPresence> _observe;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private Dictionary<string, Stamp> _stamps = new(StringComparer.Ordinal);
    private BraveAffiliationCache? _cache;
    private bool _disposed;
    public BraveAffiliationCache Cache => !_disposed ? _cache! : throw new ObjectDisposedException(nameof(BraveAffiliationSnapshot));
    public DateTimeOffset CapturedUtc { get; private set; }
    internal long RetainedSourceBytes => _files.Values.Sum(bytes => (long)bytes.Length);
    private BraveAffiliationSnapshot(string profile, Func<ProcessPresence>? observe, CancellationToken token)
    {
        _profile = Path.GetFullPath(profile); _token = token;
        _observe = observe ?? (() => ProcessPresenceProbe.Observe(["brave"], token));
    }

    /// <summary>Reads only the pinned main/cache companions, assembles committed WAL state and validates schema-v7 in memory.</summary>
    /// <remarks>No source is opened by SQLite. Missing cache files are not interpreted as an initialized empty cache.</remarks>
    public static BraveAffiliationSnapshot Read(string profile, Func<ProcessPresence>? observeBrowserState = null,
        CancellationToken token = default)
    {
        BraveAffiliationSnapshot? result = null;
        try
        {
            token.ThrowIfCancellationRequested();
            result = new BraveAffiliationSnapshot(profile, observeBrowserState, token);
            result.EnsureStopped(); result._stamps = result.Scan();
            foreach (var entry in result._stamps)
            {
                result.EnsureStopped(); result._files.Add(entry.Key, result.ReadFile(entry.Key, entry.Value));
            }
            result.Match(result.Scan());
            if (result._files.TryGetValue("-journal", out var journal) && journal.Length != 0)
                throw new BackupOperationException("passwords-affiliation-journal-unsupported", "Nonempty affiliation rollback journals require verified handling; the source was not recovered or modified.");
            var image = SqliteSnapshotImage.Create(result._files[""],
                result._files.TryGetValue("-wal", out var wal) ? wal : ReadOnlySpan<byte>.Empty, token);
            try { result._cache = BraveAffiliationDatabase.Read(image, token); }
            finally { CryptographicOperations.ZeroMemory(image); }
            result.VerifyUnchanged(); result.CapturedUtc = DateTimeOffset.UtcNow;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result?.Dispose(); if (error is BackupOperationException) throw; throw Invalid();
        }
        catch { result?.Dispose(); throw; }
    }

    /// <summary>Rechecks the complete source set, stamps/hashes and stopped-browser state before later CSV publication.</summary>
    public void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            EnsureStopped(); Match(Scan());
            foreach (var entry in _files)
            {
                var current = ReadFile(entry.Key, _stamps[entry.Key]);
                try
                {
                    if (!SHA256.HashData(current).AsSpan().SequenceEqual(SHA256.HashData(entry.Value))) throw Changed();
                }
                finally { CryptographicOperations.ZeroMemory(current); }
            }
            Match(Scan()); EnsureStopped();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { if (error is BackupOperationException) throw; throw Changed(); }
    }

    /// <summary>Clears owned file buffers and releases cache references; borrowed metadata strings cannot be reliably erased.</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (var value in _files.Values) CryptographicOperations.ZeroMemory(value);
        _files.Clear(); _stamps.Clear(); _cache = null;
    }
    public override string ToString() => "Affiliation source snapshot (contents hidden)";

    private Dictionary<string, Stamp> Scan()
    {
        SafeAncestors();
        var files = new Dictionary<string, Stamp>(StringComparer.Ordinal); long bytes = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            _token.ThrowIfCancellationRequested();
            var path = Path.Combine(_profile, DatabaseFileName + suffix);
            if (SnapshotFileAccess.Kind(path) == SnapshotNodeKind.Missing) continue;
            var stamp = Inspect(path); bytes += stamp.Length;
            if (bytes > 64L * 1024 * 1024) throw Invalid();
            files.Add(suffix, stamp);
        }
        if (!files.ContainsKey(""))
        {
            if (files.Count != 0) throw Invalid();
            throw new BackupOperationException("passwords-affiliation-source-missing", "No initialized local affiliation database was found; an empty browser cache was not assumed.");
        }
        return files;
    }
    private byte[] ReadFile(string suffix, Stamp expected)
    {
        _token.ThrowIfCancellationRequested(); SafeAncestors();
        var path = Path.Combine(_profile, DatabaseFileName + suffix);
        if (Inspect(path) != expected) throw Changed();
        using var input = SnapshotFileAccess.OpenRead(path);
        if (input.Length != expected.Length) throw Changed();
        var bytes = new byte[(int)expected.Length];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                _token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw Changed(); offset += count;
            }
            if (input.ReadByte() != -1 || Inspect(path) != expected) throw Changed();
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    private static Stamp Inspect(string path)
    {
        if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.RegularFile) throw Invalid();
        var info = new FileInfo(path); if (info.Length > 32L * 1024 * 1024) throw Invalid();
        return new Stamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }
    private void SafeAncestors()
    {
        for (var parent = _profile; parent is not null; parent = Path.GetDirectoryName(parent))
            if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory) throw Invalid();
    }
    private void Match(Dictionary<string, Stamp> actual)
    {
        if (actual.Count != _stamps.Count || _stamps.Any(pair => !actual.TryGetValue(pair.Key, out var stamp) || stamp != pair.Value)) throw Changed();
    }
    private void EnsureStopped()
    {
        _token.ThrowIfCancellationRequested(); ProcessPresence state;
        try { state = _observe(); }
        catch (OperationCanceledException) { throw; }
        catch { state = ProcessPresence.Unknown; }
        if (state != ProcessPresence.Stopped)
            throw new BackupOperationException("passwords-close-brave", "Close Brave and retry. This affiliation capture path cannot establish a source-write-free live snapshot.");
    }
    private sealed record Stamp(long Length, long ModifiedTicks);
    private static BackupOperationException Invalid() => new("passwords-affiliation-source-invalid",
        "Affiliation source files are unreadable, unsafe, corrupt, unsupported or over-limit; no grouping snapshot was accepted.");
    private static BackupOperationException Changed() => new("passwords-affiliation-source-changed",
        "Affiliation source files changed; retry with Brave closed. No stale grouping snapshot was accepted.");
}
