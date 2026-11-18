using System.Security.Cryptography;
using System.Text.Json;

namespace BraveBackup.Extensions.TabManagerPlus;

/// <summary>Owns the verified bytes of one new LevelDB log. The caller must authorize and perform any filesystem publication.</summary>
public sealed class SavedWindowRestoreUpdate : IDisposable
{
    private byte[]? _bytes;

    internal SavedWindowRestoreUpdate(string fileName, byte[] bytes)
    {
        FileName = fileName;
        _bytes = bytes;
    }

    public string FileName { get; }

    /// <summary>Borrowed log bytes. Keep the update alive while they are in use.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes ?? throw new ObjectDisposedException(nameof(SavedWindowRestoreUpdate));

    public void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
}

/// <summary>Prepares a replacement of the saved-window map in a closed Tab Manager Plus 7.0.0 database.</summary>
public static class SavedWindowRestore
{
    /// <summary>Checks the target's stored version and existing map, then verifies the proposed update by replaying it.</summary>
    /// <remarks>The caller owns package compatibility, a stable capture, closed-browser checks, review and safety backup.</remarks>
    public static SavedWindowRestoreUpdate Prepare(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files,
        ReadOnlyMemory<byte> archivedPayload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var archived = SavedWindowPayload.Parse(archivedPayload, cancellationToken);
        var version = OfflineLevelDb.ReadValue(files, "version", cancellationToken);
        byte[]? prior = null;
        byte[]? replacement = null;
        try
        {
            if (version is null || !IsSupportedVersion(version))
                throw new InvalidDataException("The destination is missing the supported Tab Manager Plus storage version.");
            prior = OfflineLevelDb.ReadValue(files, "sessions", cancellationToken);
            if (prior is null)
                throw new InvalidDataException("The destination has no initialized Tab Manager Plus saved-window map.");
            SavedWindows.Parse(prior, SavedWindows.SupportedVersion, cancellationToken);

            replacement = SavedWindowPayload.SerializeStoredMap(archived.Data, cancellationToken);
            var (name, bytes) = OfflineLevelDb.CreatePutLog(files, "sessions"u8, replacement, cancellationToken);
            try
            {
                var updated = new Dictionary<string, ReadOnlyMemory<byte>>(files, StringComparer.Ordinal);
                if (!updated.TryAdd(name, bytes))
                    throw new InvalidDataException("The new saved-window log overlaps an existing database file.");
                var replayed = OfflineLevelDb.ReadValue(updated, "sessions", cancellationToken);
                try
                {
                    if (replayed is null || !replayed.AsSpan().SequenceEqual(replacement))
                        throw new InvalidDataException("The saved-window update could not be verified.");
                }
                finally { Clear(replayed); }
                return new SavedWindowRestoreUpdate(name, bytes);
            }
            catch { Clear(bytes); throw; }
        }
        finally
        {
            Clear(version);
            Clear(prior);
            Clear(replacement);
        }
    }

    private static bool IsSupportedVersion(ReadOnlyMemory<byte> raw)
    {
        try
        {
            using var parsed = JsonDocument.Parse(raw);
            return parsed.RootElement.ValueKind == JsonValueKind.String &&
                   parsed.RootElement.ValueEquals(SavedWindows.SupportedVersion);
        }
        catch (JsonException) { return false; }
    }

    private static void Clear(byte[]? bytes)
    {
        if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
    }
}
