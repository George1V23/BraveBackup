using System.Collections.ObjectModel;
using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Explicit raw/native password-file preservation only. No portable-capture or recoverability claim is made.</summary>
public sealed class BraveRawPasswordSnapshot : IDisposable
{
    private const long MaximumTotalBytes = 128L * 1024 * 1024;
    private readonly string _profile;
    private readonly Func<ProcessPresence> _observe;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private Dictionary<string, Stamp> _stamps = new(StringComparer.Ordinal);
    private bool _disposed;
    private IReadOnlyList<string> _missingMainDatabases = Array.Empty<string>();
    public DateTimeOffset CapturedUtc { get; private set; }
    /// <summary>Borrowed raw bytes, cleared on disposal; no source path or OS key store is included.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Files => !_disposed
        ? new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(_files.ToDictionary(pair => pair.Key, pair => (ReadOnlyMemory<byte>)pair.Value, StringComparer.Ordinal))
        : throw new ObjectDisposedException(nameof(BraveRawPasswordSnapshot));
    /// <summary>Known stores whose companions exist without their main database; preservation is explicitly incomplete.</summary>
    public IReadOnlyList<string> MissingMainDatabases => !_disposed ? _missingMainDatabases
        : throw new ObjectDisposedException(nameof(BraveRawPasswordSnapshot));

    private BraveRawPasswordSnapshot(string profile, Func<ProcessPresence>? observe, CancellationToken token)
    {
        _profile = Path.GetFullPath(profile); _token = token;
        _observe = observe ?? (() => ProcessPresenceProbe.Observe(new[] { "brave" }, token));
    }

    /// <summary>Captures only allowlisted available files after explicit preservation choice; requires Brave observed closed.</summary>
    /// <remarks>Does not authorize an unencrypted local save. Archive protection/consent must still be enforced by the host.</remarks>
    public static BraveRawPasswordSnapshot Read(string profile, bool preservationExplicitlyChosen,
        Func<ProcessPresence>? observeBrowserState = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!preservationExplicitlyChosen)
            throw new BackupOperationException("passwords-raw-not-selected", "Explicitly choose raw password-file preservation before capture.");
        BraveRawPasswordSnapshot? result = null;
        try
        {
            result = new BraveRawPasswordSnapshot(profile, observeBrowserState, token);
            result.EnsureStopped();
            result._stamps = result.Scan();
            foreach (var entry in result._stamps)
            {
                result.EnsureStopped();
                result._files.Add(entry.Key, result.ReadFile(entry.Key, entry.Value));
            }
            result._missingMainDatabases = Array.AsReadOnly(new[] { "Login Data", "Login Data For Account" }
                .Where(name => !result._files.ContainsKey(name) && result._files.Keys.Any(file => file.StartsWith(name + "-", StringComparison.Ordinal))).ToArray());
            result.VerifyUnchanged();
            result.CapturedUtc = DateTimeOffset.UtcNow;
            return result;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            result?.Dispose();
            if (error is BackupOperationException) throw;
            throw Invalid();
        }
        catch { result?.Dispose(); throw; }
    }

    /// <summary>Rechecks available file set, lengths/timestamps, hashes and stopped-browser state before publication.</summary>
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
        {
            if (error is BackupOperationException) throw;
            throw Changed();
        }
    }

    /// <summary>Clears owned raw buffers; previously borrowed memory observes the cleared bytes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var bytes in _files.Values) CryptographicOperations.ZeroMemory(bytes);
        _files.Clear(); _stamps.Clear(); _missingMainDatabases = Array.Empty<string>();
    }
    public override string ToString() => "Raw password-file snapshot (not portable; contents hidden)";

    private Dictionary<string, Stamp> Scan()
    {
        SafeAncestors();
        var files = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        long total = 0;
        foreach (var name in new[] { "Login Data", "Login Data For Account" })
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            _token.ThrowIfCancellationRequested();
            var path = Path.Combine(_profile, name + suffix);
            if (SnapshotFileAccess.Kind(path) == SnapshotNodeKind.Missing) continue;
            var stamp = Inspect(path);
            total += stamp.Length;
            if (total > MaximumTotalBytes) throw Invalid();
            files.Add(name + suffix, stamp);
        }
        if (files.Count == 0)
            throw new BackupOperationException("passwords-raw-source-missing", "No available password-data files can be preserved; absence was not reported as an empty credential collection.");
        return files;
    }

    private byte[] ReadFile(string name, Stamp expected)
    {
        _token.ThrowIfCancellationRequested(); SafeAncestors();
        var path = Path.Combine(_profile, name);
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
                if (count == 0) throw Changed();
                offset += count;
            }
            if (input.ReadByte() != -1 || Inspect(path) != expected) throw Changed();
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    private static Stamp Inspect(string path)
    {
        if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.RegularFile) throw Invalid();
        var info = new FileInfo(path);
        if (info.Length > 64L * 1024 * 1024) throw Invalid();
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
        _token.ThrowIfCancellationRequested();
        ProcessPresence state;
        try { state = _observe(); }
        catch (OperationCanceledException) { throw; }
        catch { state = ProcessPresence.Unknown; }
        if (state != ProcessPresence.Stopped)
            throw new BackupOperationException("passwords-close-brave", "Close Brave and retry. This raw preservation path cannot establish a source-write-free live snapshot.");
    }
    private sealed record Stamp(long Length, long ModifiedTicks);
    private static BackupOperationException Invalid() => new("passwords-raw-source-invalid",
        "Available password files are unreadable, unsafe or over-limit; raw preservation did not complete.");
    private static BackupOperationException Changed() => new("passwords-source-changed",
        "Password source files changed; retry with Brave closed. Raw preservation did not complete.");
}
