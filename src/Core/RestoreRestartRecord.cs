using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Supported opaque image kinds in the restart recovery record.</summary>
public enum RestoreRestartTargetKind : byte
{
    File = 1,
    FlatDirectory = 2
}

/// <summary>One reviewed destination and the images that may be encountered during rollback.</summary>
public sealed record RestoreRestartTarget(Guid MutationId, RestoreRestartTargetKind Kind,
    string InstallationRoot, string ProfileDirectory, string RelativeTarget, string DirectoryEvidence,
    string OriginalFingerprint, string ReplacementFingerprint)
{
    public override string ToString() => "Restore restart target (paths and evidence hidden)";
}

/// <summary>Immutable operation evidence paired with an exact safety snapshot and bound journal.</summary>
public sealed record RestoreRestartRecord(Guid OperationId, string SafetyFingerprint, IReadOnlyList<RestoreRestartTarget> Targets)
{
    public override string ToString() => $"Restore restart record ({Targets.Count} targets)";
}

/// <summary>Authenticated restart metadata. Destination access and write approval belong to the platform adapter.</summary>
public sealed record RestoreRestartInspection(RestoreRestartRecord Record, RestoreArtifactInspection Journal);

/// <summary>Owns the authenticated original images needed for an explicitly approved restart rollback.</summary>
public sealed class VerifiedRestoreRestartArtifacts : IDisposable
{
    internal VerifiedRestoreRestartArtifacts(RestoreRestartInspection inspection, RestoreSafetySnapshot originals, string fingerprint)
    { Inspection = inspection; Originals = originals; RecordFingerprint = fingerprint; }
    public RestoreRestartInspection Inspection { get; }
    public RestoreSafetySnapshot Originals { get; }
    internal string RecordFingerprint { get; }
    public void Dispose() => Originals.Dispose();
}

/// <summary>Encodes immutable restart evidence and verifies its relationship to both encrypted artifacts.</summary>
public static class RestoreRestartRecordFormat
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    private const int MaximumTextBytes = 4096;
    private const int MaximumEvidenceBytes = 8192;
    private const int BoundJournalHeaderSize = 40;
    private const int MaximumBoundJournalBytes = BoundJournalHeaderSize + 40 + 10_000 * 20;
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Creates owned plaintext for encryption by the caller.</summary>
    public static SensitiveBytes Encode(RestoreRestartRecord record, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        token.ThrowIfCancellationRequested();
        Validate(record, token);
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Utf8, leaveOpen: true);
        try
        {
            writer.Write("BBKRP001"u8);
            writer.Write(record.OperationId.ToByteArray());
            writer.Write(Convert.FromHexString(record.SafetyFingerprint));
            writer.Write(record.Targets.Count);
            foreach (var target in record.Targets)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(target.MutationId.ToByteArray());
                writer.Write((byte)target.Kind);
                WriteText(writer, target.InstallationRoot);
                WriteText(writer, target.ProfileDirectory);
                WriteText(writer, target.RelativeTarget);
                WriteText(writer, target.DirectoryEvidence);
                writer.Write(Convert.FromHexString(target.OriginalFingerprint));
                writer.Write(Convert.FromHexString(target.ReplacementFingerprint));
                if (buffer.Length > MaximumBytes) throw Invalid();
            }
            token.ThrowIfCancellationRequested();
            return new SensitiveBytes(buffer.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(buffer.GetBuffer()); }
    }

    /// <summary>Decodes a bounded record and rejects malformed or duplicate mappings.</summary>
    public static RestoreRestartRecord Decode(SensitiveBytes plaintext, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        token.ThrowIfCancellationRequested();
        var bytes = plaintext.Bytes;
        if (bytes.Length > MaximumBytes) throw Invalid();
        var owned = bytes.ToArray();
        try
        {
            using var stream = new MemoryStream(owned, writable: false);
            using var reader = new BinaryReader(stream);
            if (!ReadBytes(reader, 8).AsSpan().SequenceEqual("BBKRP001"u8)) throw Invalid();
            var operation = new Guid(ReadBytes(reader, 16));
            var safetyFingerprint = Convert.ToHexStringLower(ReadBytes(reader, 32));
            var count = reader.ReadInt32();
            if (count is < 1 or > 10_000) throw Invalid();
            var targets = new RestoreRestartTarget[count];
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                targets[index] = new(new Guid(ReadBytes(reader, 16)), (RestoreRestartTargetKind)reader.ReadByte(),
                    ReadText(reader, MaximumTextBytes), ReadText(reader, MaximumTextBytes),
                    ReadText(reader, MaximumTextBytes), ReadText(reader, MaximumEvidenceBytes),
                    Convert.ToHexStringLower(ReadBytes(reader, 32)), Convert.ToHexStringLower(ReadBytes(reader, 32)));
            }
            if (stream.Position != stream.Length) throw Invalid();
            var record = new RestoreRestartRecord(operation, safetyFingerprint, Array.AsReadOnly(targets));
            Validate(record, token);
            return record;
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException)
        {
            throw Invalid();
        }
        finally { CryptographicOperations.ZeroMemory(owned); }
    }

    /// <summary>Encodes a journal revision bound to the exact restart record plaintext.</summary>
    public static SensitiveBytes EncodeJournal(SensitiveBytes recordPlaintext, long revision, RestoreTransactionState state,
        IReadOnlyList<(Guid Id, RestoreEntryState State)> entries, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var record = Decode(recordPlaintext, token);
        if (entries.Count != record.Targets.Count ||
            !record.Targets.Select(target => target.MutationId).SequenceEqual(entries.Select(entry => entry.Id)))
            throw Invalid();

        return EncodeBoundJournal(SHA256.HashData(recordPlaintext.Bytes.Span), record.OperationId, revision, state, entries);
    }

    internal static SensitiveBytes EncodeBoundJournal(ReadOnlySpan<byte> recordFingerprint, Guid operation, long revision,
        RestoreTransactionState state, IReadOnlyList<(Guid Id, RestoreEntryState State)> entries)
    {
        if (recordFingerprint.Length != 32) throw Invalid();
        var journal = RestoreJournalFormat.Encode(operation, revision, state, entries);
        try
        {
            var bytes = new byte[BoundJournalHeaderSize + journal.Length];
            "BBKJR002"u8.CopyTo(bytes);
            recordFingerprint.CopyTo(bytes.AsSpan(8, 32));
            journal.CopyTo(bytes.AsSpan(BoundJournalHeaderSize));
            return new SensitiveBytes(bytes);
        }
        finally { CryptographicOperations.ZeroMemory(journal); }
    }

    /// <summary>Authenticates and cross-checks the restart record, safety snapshot and bound journal.</summary>
    /// <remarks>The caller owns the streams. The verified record describes paths but does not authorize access to them.</remarks>
    public static RestoreRestartInspection Inspect(Stream record, Stream safety, Stream journal, string password,
        CancellationToken token = default)
    {
        using var verified = Open(record, safety, journal, password, token);
        return verified.Inspection;
    }

    /// <summary>Authenticates the three artifacts and returns owned original images for native recovery.</summary>
    public static VerifiedRestoreRestartArtifacts Open(Stream record, Stream safety, Stream journal, string password,
        CancellationToken token = default)
    {
        using var recordPlaintext = RestoreArtifactInspection.ReadEncrypted(record, password, MaximumBytes, token);
        var plan = Decode(recordPlaintext, token);
        using var journalPlaintext = RestoreArtifactInspection.ReadEncrypted(journal, password, MaximumBoundJournalBytes, token);
        var journalBytes = journalPlaintext.Bytes.Span;
        var recordHash = SHA256.HashData(recordPlaintext.Bytes.Span);
        if (journalBytes.Length <= BoundJournalHeaderSize || !journalBytes[..8].SequenceEqual("BBKJR002"u8) ||
            !CryptographicOperations.FixedTimeEquals(recordHash, journalBytes[8..40]))
            throw Invalid();
        var state = RestoreJournalFormat.Decode(journalBytes[BoundJournalHeaderSize..]);
        if (state.Operation != plan.OperationId || state.Entries.Length != plan.Targets.Count) throw Invalid();

        using var safetyPlaintext = RestoreArtifactInspection.ReadEncrypted(safety, password, RestoreSafetyFormat.MaximumBytes, token);
        if (Convert.ToHexStringLower(SHA256.HashData(safetyPlaintext.Bytes.Span)) != plan.SafetyFingerprint) throw Invalid();
        var snapshot = RestoreSafetyFormat.Decode(safetyPlaintext.Bytes.Span, token);
        try
        {
            if (snapshot.OperationId != plan.OperationId || snapshot.Entries.Count != plan.Targets.Count) throw Invalid();
            var entries = new RestoreArtifactEntry[plan.Targets.Count];
            for (var index = 0; index < entries.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var target = plan.Targets[index];
                var original = snapshot.Entries[index];
                var entry = state.Entries[index];
                if (target.MutationId != entry.Id || target.MutationId != original.Id ||
                    target.OriginalFingerprint != RestoreRecoveryEvidence.Fingerprint(original.Original))
                    throw Invalid();
                entries[index] = new(original.Id, original.InstallationId, original.ProfileId, original.Category,
                    original.Original.Exists, entry.State);
            }
            var inspection = new RestoreRestartInspection(plan,
                new(plan.OperationId, state.Revision, state.State, Array.AsReadOnly(entries)));
            return new(inspection, snapshot, Convert.ToHexStringLower(recordHash));
        }
        catch
        {
            snapshot.Dispose();
            throw;
        }
    }

    /// <summary>Encodes a revision for an already authenticated restart record's exact fingerprint.</summary>
    public static SensitiveBytes EncodeVerifiedJournal(VerifiedRestoreRestartArtifacts verified, long revision,
        RestoreTransactionState state, IReadOnlyList<(Guid Id, RestoreEntryState State)> entries)
    {
        ArgumentNullException.ThrowIfNull(verified);
        var record = verified.Inspection.Record;
        if (entries.Count != record.Targets.Count ||
            !record.Targets.Select(target => target.MutationId).SequenceEqual(entries.Select(entry => entry.Id)))
            throw Invalid();
        return EncodeBoundJournal(Convert.FromHexString(verified.RecordFingerprint), record.OperationId, revision, state, entries);
    }

    private static void Validate(RestoreRestartRecord record, CancellationToken token)
    {
        if (record.OperationId == Guid.Empty || record.Targets is null || record.Targets.Count is < 1 or > 10_000)
            throw Invalid();
        RestoreRecoveryEvidence.ValidateFingerprint(record.SafetyFingerprint);
        var ids = new HashSet<Guid>();
        var paths = new HashSet<(string Profile, string Target)>();
        foreach (var target in record.Targets)
        {
            token.ThrowIfCancellationRequested();
            if (target is null || target.MutationId == Guid.Empty || !ids.Add(target.MutationId) ||
                target.Kind is not (RestoreRestartTargetKind.File or RestoreRestartTargetKind.FlatDirectory))
                throw Invalid();
            ValidateText(target.InstallationRoot, MaximumTextBytes);
            ValidateText(target.ProfileDirectory, MaximumTextBytes);
            ValidateText(target.RelativeTarget, MaximumTextBytes);
            ValidateText(target.DirectoryEvidence, MaximumEvidenceBytes);
            if (target.RelativeTarget.IndexOfAny(['\\', ':']) >= 0 ||
                target.RelativeTarget.Split('/').Any(part => part is "" or "." or "..") ||
                !paths.Add((target.ProfileDirectory, target.RelativeTarget)))
                throw Invalid();
            RestoreRecoveryEvidence.ValidateFingerprint(target.OriginalFingerprint);
            RestoreRecoveryEvidence.ValidateFingerprint(target.ReplacementFingerprint);
        }
    }

    private static void ValidateText(string text, int limit)
    {
        if (string.IsNullOrEmpty(text) || text.Length > limit || text.Any(char.IsControl)) throw Invalid();
        try { if (Utf8.GetByteCount(text) > limit) throw Invalid(); }
        catch (EncoderFallbackException) { throw Invalid(); }
    }

    private static void WriteText(BinaryWriter writer, string value)
    {
        var bytes = Utf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadText(BinaryReader reader, int limit)
    {
        var length = reader.ReadInt32();
        if (length is < 1 || length > limit) throw Invalid();
        return Utf8.GetString(ReadBytes(reader, length));
    }

    private static byte[] ReadBytes(BinaryReader reader, int length)
    {
        if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw Invalid();
        return reader.ReadBytes(length);
    }

    private static InvalidDataException Invalid() => new("Invalid or mismatched restart recovery evidence.");
}
