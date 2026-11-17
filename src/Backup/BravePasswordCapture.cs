using System.Collections.ObjectModel;
using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Owns an explicitly requested, verified in-memory password capture. Does not authorize saving plaintext.</summary>
public sealed class BravePasswordCapture : IDisposable
{
    private readonly BravePasswordSnapshot _source;
    private readonly LocalStateSource? _localState;
    private readonly Dictionary<string, DecodedPasswordDatabase> _databases = new(StringComparer.Ordinal);
    private bool _disposed;

    private BravePasswordCapture(BravePasswordSnapshot source, LocalStateSource? localState)
    { _source = source; _localState = localState; }

    public IReadOnlyDictionary<string, DecodedPasswordDatabase> Databases => !_disposed
        ? new ReadOnlyDictionary<string, DecodedPasswordDatabase>(_databases)
        : throw new ObjectDisposedException(nameof(BravePasswordCapture));
    public DateTimeOffset CapturedUtc => _source.CapturedUtc;
    internal long RetainedSourceBytes => _source.RetainedSourceBytes + (_localState?.Bytes.Length ?? 0);

    /// <summary>Captures both local stores, requests at most one key and decodes every eligible password and note.</summary>
    /// <remarks>
    /// Run on a worker after an explicit password operation. Provider context must be established by the caller,
    /// not guessed from a v10 prefix. Windows uses Local State in the profile's parent user-data directory.
    /// An injected unlocker receives borrowed Local State bytes (empty on Linux) and returns an owned key.
    /// It must not retain source bytes or key copies. Native adapters are used when no unlocker is supplied.
    /// </remarks>
    public static BravePasswordCapture Read(string profile, PasswordEncryptionProvider provider,
        Func<ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? unlock = null,
        Func<ProcessPresence>? observeBrowserState = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (provider is not (PasswordEncryptionProvider.WindowsDpapiV10 or PasswordEncryptionProvider.LinuxKeyringV11))
            throw new ArgumentOutOfRangeException(nameof(provider));
        BravePasswordSnapshot? source = null;
        LocalStateSource? localState = null;
        BravePasswordCapture? result = null;
        try
        {
            source = BravePasswordSnapshot.Read(profile, observeBrowserState, token);
            var needsKey = Preflight(source, provider, token);
            if (needsKey && provider == PasswordEncryptionProvider.WindowsDpapiV10)
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(profile));
                if (parent is null) throw KeySourceInvalid();
                localState = LocalStateSource.Read(Path.Combine(parent, "Local State"), token);
            }
            result = new BravePasswordCapture(source, localState);
            result.VerifyUnchanged();
            using var key = needsKey ? Unlock() : null;
            token.ThrowIfCancellationRequested();
            if (key is not null && key.Provider != provider) throw Unsupported();
            if (needsKey && key is null)
                throw new BackupOperationException("passwords-key-unlock-failed", "No supported password key was returned.");
            result.VerifyUnchanged();
            long bytes = 0;
            foreach (var entry in source.Databases)
            {
                token.ThrowIfCancellationRequested();
                var decoded = key is null ? DecodeEmpty(entry.Value, token) : BravePasswordCrypto.DecodeDatabase(entry.Value, key, token);
                result._databases.Add(entry.Key, decoded);
                foreach (var row in decoded.Rows)
                {
                    bytes += row.Password.Utf8.Length;
                    foreach (var note in row.Notes) bytes += note.Value.Utf8.Length;
                }
                if (bytes > 32L * 1024 * 1024)
                    throw new BackupOperationException("passwords-capture-limit", "Combined password data exceeds the supported capture limit.");
            }
            result.VerifyUnchanged();
            return result;

            PasswordKeyMaterial Unlock() => unlock is not null
                ? unlock(localState?.Bytes ?? ReadOnlyMemory<byte>.Empty, token)
                : provider == PasswordEncryptionProvider.WindowsDpapiV10
                    ? BraveWindowsPasswordKey.Unlock(localState!.Bytes, token: token)
                    : BraveLinuxPasswordKey.Unlock(token: token);
        }
        catch
        {
            if (result is not null) result.Dispose();
            else { localState?.Dispose(); source?.Dispose(); }
            throw;
        }
    }

    /// <summary>Rechecks database companions, Windows key source and stopped-browser state before future publication.</summary>
    public void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _source.VerifyUnchanged();
        _localState?.Verify();
        _source.VerifyUnchanged();
    }

    /// <summary>Clears decoded values, encrypted snapshot buffers and any captured Local State bytes.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var database in _databases.Values) database.Dispose();
        _databases.Clear();
        _localState?.Dispose();
        _source.Dispose();
    }

    public override string ToString() => "Password capture (contents hidden)";

    private static bool Preflight(BravePasswordSnapshot source, PasswordEncryptionProvider provider, CancellationToken token)
    {
        var needsKey = false;
        foreach (var database in source.Databases.Values)
        foreach (var row in database.Rows)
        {
            Check(row.EncryptedValue);
            foreach (var note in row.Notes) Check(note.EncryptedValue);
        }
        return needsKey;

        void Check(byte[] value)
        {
            token.ThrowIfCancellationRequested();
            if (value.Length == 0) return;
            var prefix = provider == PasswordEncryptionProvider.WindowsDpapiV10 ? "v10"u8 : "v11"u8;
            if (!value.AsSpan().StartsWith(prefix)) throw Unsupported();
            if (provider == PasswordEncryptionProvider.WindowsDpapiV10 ? value.Length < 31 : value.Length < 19 || (value.Length - 3) % 16 != 0)
                throw new BackupOperationException("passwords-decode-failed", "A credential encryption frame is incomplete; nothing was omitted.");
            needsKey = true;
        }
    }

    private static DecodedPasswordDatabase DecodeEmpty(StoredPasswordDatabase source, CancellationToken token)
    {
        var values = new List<SensitiveUtf8>();
        var rows = new List<DecodedPasswordRow>();
        try
        {
            foreach (var row in source.Rows)
            {
                token.ThrowIfCancellationRequested();
                var password = Empty(row.EncryptedValue);
                var notes = row.Notes.Select(note => new DecodedPasswordNote(note, Empty(note.EncryptedValue))).ToList();
                rows.Add(new DecodedPasswordRow(row, password, notes.AsReadOnly()));
            }
            return new DecodedPasswordDatabase(rows, values, source.BlockedEntries, source.FederatedEntries);
        }
        catch { foreach (var value in values) value.Dispose(); throw; }

        SensitiveUtf8 Empty(byte[] bytes)
        {
            token.ThrowIfCancellationRequested();
            if (bytes.Length != 0) throw Unsupported();
            var value = new SensitiveUtf8([]);
            values.Add(value);
            return value;
        }
    }

    private static BackupOperationException Unsupported() => new("passwords-encryption-unsupported",
        "Password data does not match the selected supported provider; no alternate key or backend was attempted.");
    private static BackupOperationException KeySourceInvalid() => new("passwords-key-source-invalid",
        "The existing browser key source is missing, unsafe, unreadable or over-limit.");
    private static BackupOperationException KeySourceChanged() => new("passwords-key-source-changed",
        "The browser key source changed during capture; retry with Brave closed.");

    private sealed class LocalStateSource : IDisposable
    {
        private readonly string _path;
        private readonly CancellationToken _token;
        private readonly byte[] _bytes;
        private readonly (long Length, long Modified) _stamp;
        public ReadOnlyMemory<byte> Bytes => _bytes;
        private LocalStateSource(string path, CancellationToken token)
        {
            _path = path; _token = token;
            _stamp = Inspect();
            _bytes = ReadBytes(_stamp);
        }
        public static LocalStateSource Read(string path, CancellationToken token)
        {
            LocalStateSource? result = null;
            try { result = new LocalStateSource(path, token); result.Verify(); return result; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { result?.Dispose(); throw KeySourceInvalid(); }
            catch { result?.Dispose(); throw; }
        }
        public void Verify()
        {
            byte[]? current = null;
            try
            {
                current = ReadBytes(_stamp);
                if (!SHA256.HashData(current).AsSpan().SequenceEqual(SHA256.HashData(_bytes))) throw KeySourceChanged();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw KeySourceChanged(); }
            finally { if (current is not null) CryptographicOperations.ZeroMemory(current); }
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_bytes);
        private (long Length, long Modified) Inspect()
        {
            _token.ThrowIfCancellationRequested();
            for (var parent = Path.GetDirectoryName(_path); parent is not null; parent = Path.GetDirectoryName(parent))
                if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory) throw KeySourceInvalid();
            if (SnapshotFileAccess.Kind(_path) != SnapshotNodeKind.RegularFile) throw KeySourceInvalid();
            var info = new FileInfo(_path);
            if (info.Length is <= 0 or > 4 * 1024 * 1024) throw KeySourceInvalid();
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }
        private byte[] ReadBytes((long Length, long Modified) expected)
        {
            if (Inspect() != expected) throw KeySourceChanged();
            using var input = SnapshotFileAccess.OpenRead(_path);
            if (input.Length != expected.Length) throw KeySourceChanged();
            var bytes = new byte[(int)expected.Length];
            try
            {
                for (var offset = 0; offset < bytes.Length;)
                {
                    _token.ThrowIfCancellationRequested();
                    var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                    if (count == 0) throw KeySourceChanged();
                    offset += count;
                }
                if (input.ReadByte() != -1 || Inspect() != expected) throw KeySourceChanged();
                return bytes;
            }
            catch { CryptographicOperations.ZeroMemory(bytes); throw; }
        }
    }
}
