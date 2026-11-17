using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>
/// Explicit-operation capture of encrypted password databases into memory. The initial
/// source-copy strategy requires Brave observed closed; it never opens a source with SQLite.
/// </summary>
public sealed class BravePasswordSnapshot : IDisposable
{
    private readonly string _profile;
    private readonly Func<ProcessPresence> _observe;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StoredPasswordDatabase> _databases = new(StringComparer.Ordinal);
    private Dictionary<string, Stamp> _stamps = new(StringComparer.Ordinal);
    private bool _disposed;

    public IReadOnlyDictionary<string, StoredPasswordDatabase> Databases => !_disposed
        ? new System.Collections.ObjectModel.ReadOnlyDictionary<string, StoredPasswordDatabase>(_databases)
        : throw new ObjectDisposedException(nameof(BravePasswordSnapshot));

    public DateTimeOffset CapturedUtc { get; private set; }

    private BravePasswordSnapshot(string profile, Func<ProcessPresence>? observe, CancellationToken token)
    {
        _profile = Path.GetFullPath(profile);
        _token = token;
        _observe = observe ?? (() => ProcessPresenceProbe.Observe(new[] { "brave" }, token));
    }

    /// <summary>Reads both known locally present stores on demand, validating every source before returning encrypted rows.</summary>
    public static BravePasswordSnapshot Read(string profile, Func<ProcessPresence>? observeBrowserState = null,
        CancellationToken token = default)
    {
        BravePasswordSnapshot? result = null;
        try
        {
            token.ThrowIfCancellationRequested();
            result = new BravePasswordSnapshot(profile, observeBrowserState, token);
            result.EnsureStopped();
            result._stamps = result.Scan();
            foreach (var entry in result._stamps)
            {
                result.EnsureStopped();
                result._files.Add(entry.Key, result.ReadFile(entry.Key, entry.Value));
            }

            result.Match(result.Scan());
            foreach (var name in new[] { "Login Data", "Login Data For Account" })
            {
                if (!result._files.TryGetValue(name, out var database)) continue;
                if (result._files.TryGetValue(name + "-journal", out var journal) && journal.Length != 0)
                    throw new BackupOperationException("passwords-journal-unsupported",
                        "Nonempty rollback journals are outside tested capture support; the source was not recovered or modified.");
                var image = SqliteSnapshotImage.Create(database,
                    result._files.TryGetValue(name + "-wal", out var wal) ? wal : ReadOnlySpan<byte>.Empty, token);
                try
                {
                    result._databases.Add(name, BravePasswordDatabase.Read(image, token));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(image);
                }
            }

            if (result._databases.Values.Sum(db => (long)db.Rows.Count + db.BlockedEntries + db.FederatedEntries) >
                100_000)
                throw Invalid();
            result.VerifyUnchanged();
            result.CapturedUtc = DateTimeOffset.UtcNow;
            return result;
        }
        catch (BackupOperationException)
        {
            result?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            result?.Dispose();
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                                          or ArgumentException)
        {
            result?.Dispose();
            throw Invalid();
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    /// <summary>Requires the same source set, stamps, bytes and stopped-browser observation before future publication.</summary>
    public void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            EnsureStopped();
            Match(Scan());
            foreach (var entry in _files)
            {
                var current = ReadFile(entry.Key, _stamps[entry.Key]);
                try
                {
                    if (!SHA256.HashData(current).AsSpan().SequenceEqual(SHA256.HashData(entry.Value))) throw Changed();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(current);
                }
            }

            Match(Scan());
            EnsureStopped();
        }
        catch (BackupOperationException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Changed();
        }
    }

    /// <summary>Clears owned captured images and encrypted values; previously returned managed strings cannot be erased.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var database in _databases.Values) database.Dispose();
        foreach (var bytes in _files.Values) CryptographicOperations.ZeroMemory(bytes);
        _databases.Clear();
        _files.Clear();
        _stamps.Clear();
    }

    private void EnsureStopped()
    {
        _token.ThrowIfCancellationRequested();
        ProcessPresence state;
        try
        {
            state = _observe();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            state = ProcessPresence.Unknown;
        }

        if (state != ProcessPresence.Stopped)
            throw new BackupOperationException("passwords-close-brave",
                "This capture path cannot establish a source-write-free live snapshot. Close Brave and retry; no password files were modified.");
    }

    private Dictionary<string, Stamp> Scan()
    {
        SafeAncestors(_profile);
        var result = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        long total = 0;
        var stores = 0;
        foreach (var name in new[] { "Login Data", "Login Data For Account" })
        {
            var exists = SnapshotFileAccess.Kind(Path.Combine(_profile, name)) != SnapshotNodeKind.Missing;
            if (exists) stores++;
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                _token.ThrowIfCancellationRequested();
                var path = Path.Combine(_profile, name + suffix);
                if (SnapshotFileAccess.Kind(path) == SnapshotNodeKind.Missing) continue;
                if (!exists) throw Invalid();
                var stamp = Inspect(path);
                total += stamp.Length;
                if (total > 128L * 1024 * 1024) throw Invalid();
                result.Add(name + suffix, stamp);
            }
        }

        if (stores == 0)
            throw new BackupOperationException("passwords-source-missing",
                "No local password database was found; missing data was not reported as an empty collection.");
        return result;
    }

    private byte[] ReadFile(string name, Stamp expected)
    {
        _token.ThrowIfCancellationRequested();
        SafeAncestors(_profile);
        var path = Path.Combine(_profile, name);
        if (Inspect(path) != expected) throw Changed();
        using var input = SnapshotFileAccess.OpenRead(path);
        if (input.Length != expected.Length) throw Changed();
        var bytes = new byte[checked((int)expected.Length)];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                _token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw Changed();
                offset += count;
            }

            if (input.ReadByte() != -1 || Inspect(path) != expected) throw Changed();
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static Stamp Inspect(string path)
    {
        if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.RegularFile) throw Invalid();
        var info = new FileInfo(path);
        if (info.Length > SqliteSnapshotImage.MaximumBytes) throw Invalid();
        return new Stamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void SafeAncestors(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory)
                throw Invalid();
    }

    private void Match(Dictionary<string, Stamp> actual)
    {
        if (actual.Count != _stamps.Count ||
            _stamps.Any(pair => !actual.TryGetValue(pair.Key, out var stamp) || stamp != pair.Value)) throw Changed();
    }

    private sealed record Stamp(long Length, long ModifiedTicks);

    private static BackupOperationException Changed() => new("passwords-source-changed",
        "Password source files changed; retry with Brave closed. No stale snapshot was accepted.");

    private static BackupOperationException Invalid() => new("passwords-source-invalid",
        "Password files are unreadable, unsafe, corrupt, unsupported or over-limit; nothing was silently omitted.");
}