using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Logical recovery inventory without original contents or filesystem paths.</summary>
public sealed record RestoreArtifactEntry(Guid MutationId, Guid InstallationId, Guid ProfileId,
    string Category, bool OriginallyExisted, RestoreEntryState State);

/// <summary>Authenticated v1 recovery artifact metadata for inspection after an app restart.</summary>
public sealed record RestoreArtifactInspection(Guid OperationId, long Revision, RestoreTransactionState State,
    IReadOnlyList<RestoreArtifactEntry> Entries)
{
    private const int MaximumJournalBytes = 40 + 10_000 * 20;

    /// <summary>Authenticates both artifacts and matches their logical operation and ordered entries.</summary>
    /// <remarks>The caller owns the streams. V1 does not bind a journal revision to the safety image's contents
    /// or provide destination and replacement state. This result does not authorize recovery writes.</remarks>
    public static RestoreArtifactInspection Read(Stream safety, Stream journal, string password,
        CancellationToken token = default)
    {
        using var journalPlaintext = ReadEncrypted(journal, password, MaximumJournalBytes, token);
        var state = RestoreJournalFormat.Decode(journalPlaintext.Bytes.Span);
        using var safetyPlaintext = ReadEncrypted(safety, password, RestoreSafetyFormat.MaximumBytes, token);
        using var snapshot = RestoreSafetyFormat.Decode(safetyPlaintext.Bytes.Span, token);
        if (snapshot.OperationId != state.Operation || snapshot.Entries.Count != state.Entries.Length)
            throw Mismatched();

        var entries = new RestoreArtifactEntry[state.Entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var original = snapshot.Entries[index];
            var journalEntry = state.Entries[index];
            if (original.Id != journalEntry.Id) throw Mismatched();
            entries[index] = new(original.Id, original.InstallationId, original.ProfileId,
                original.Category, original.Original.Exists, journalEntry.State);
        }

        return new(state.Operation, state.Revision, state.State, Array.AsReadOnly(entries));
    }

    internal static SensitiveBytes ReadEncrypted(Stream stream, string password, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!stream.CanSeek || !stream.CanRead || stream.Length > limit + 4096L)
            throw new InvalidDataException("Invalid recovery artifact stream or size.");
        stream.Position = 0;
        if (!ArchiveEncryption.IsEncrypted(stream))
            throw new InvalidDataException("A recovery artifact must be encrypted.");
        using var reader = ArchiveEncryption.OpenReader(stream, password, token);
        if (reader.Length > limit)
            throw new InvalidDataException("Recovery artifact exceeds its plaintext limit.");
        var buffer = new byte[(int)reader.Length];
        try
        {
            reader.ReadExactly(buffer);
            token.ThrowIfCancellationRequested();
            return new SensitiveBytes(buffer);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(buffer);
            throw;
        }
    }

    private static InvalidDataException Mismatched() =>
        new("Safety backup and recovery journal describe different operations or targets.");
}
