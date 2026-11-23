using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>The result of comparing a journal entry and its current image with authenticated evidence.</summary>
public enum RestoreRollbackDecision
{
    Skip,
    AlreadyOriginal,
    RestoreOriginal,
    Conflict
}

/// <summary>Fingerprints complete restore images and reconciles touched targets for rollback.</summary>
public static class RestoreRecoveryEvidence
{
    /// <summary>
    /// Computes SHA-256 over the v1 image frame, including existence, content and opaque adapter metadata.
    /// </summary>
    public static string Fingerprint(RestoreFileImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var content = image.Content.Bytes.Span;
        var metadata = image.Metadata.Bytes.Span;

        Span<byte> frame = stackalloc byte[17];
        "BBKIMG01"u8.CopyTo(frame);
        frame[8] = image.Exists ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(frame[9..13], content.Length);
        BinaryPrimitives.WriteInt32LittleEndian(frame[13..17], metadata.Length);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(frame);
        hash.AppendData(content);
        hash.AppendData(metadata);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Compares a read-only observation with the original and replacement evidence from a verified recovery record.</summary>
    /// <remarks>Filesystem identity checks, artifact authentication and write approval remain the caller's responsibility.</remarks>
    public static RestoreRollbackDecision AssessRollback(RestoreEntryState state, RestoreFileImage current,
        string originalFingerprint, string replacementFingerprint)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateFingerprint(originalFingerprint);
        ValidateFingerprint(replacementFingerprint);
        var observed = Fingerprint(current);
        var matchesOriginal = observed == originalFingerprint;
        if (state is RestoreEntryState.Untouched or RestoreEntryState.Recovered)
            return matchesOriginal ? RestoreRollbackDecision.Skip : RestoreRollbackDecision.Conflict;

        return AssessTouched(state, matchesOriginal, observed == replacementFingerprint);
    }

    // Also used by the retained transaction, which can compare the exact original and replacement bytes.
    internal static RestoreRollbackDecision AssessTouched(RestoreEntryState state, bool matchesOriginal, bool matchesReplacement)
    {
        if (state is not (RestoreEntryState.Intent or RestoreEntryState.Applied))
            throw new InvalidDataException("Invalid touched recovery state.");
        if (matchesOriginal) return RestoreRollbackDecision.AlreadyOriginal;
        return matchesReplacement ? RestoreRollbackDecision.RestoreOriginal : RestoreRollbackDecision.Conflict;
    }

    internal static void ValidateFingerprint(string fingerprint)
    {
        if (fingerprint is null || fingerprint.Length != 64 ||
            fingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Invalid restore image fingerprint.");
    }
}
