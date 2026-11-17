using BraveBackup.Platform;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Bounded encrypted recovery-artifact I/O; never writes browser profiles or accepts plaintext artifacts.</summary>
public static class RestoreArtifactStorage
{
    public const long MaximumBytes = RestoreSafetyFormat.MaximumBytes + 3L * 1024 * 1024;
    /// <summary>Privately stages, flushes and atomically publishes an encrypted artifact in an existing safe directory.</summary>
    /// <remarks>The caller must authorize the exact artifact path and keep it outside all browser/source paths.</remarks>
    public static void Publish(string path, ReadOnlyMemory<byte> encrypted, bool replaceExisting = false)
    {
        var target = Validate(path);
        if (encrypted.Length > MaximumBytes || encrypted.Length < ArchiveEncryption.HeaderSize ||
            !encrypted.Span[..8].SequenceEqual("BBKENC1\n"u8)) throw new InvalidDataException("An encrypted recovery artifact is required.");
        var initial = SnapshotFileAccess.Kind(target);
        if (initial != SnapshotNodeKind.Missing && (!replaceExisting || initial != SnapshotNodeKind.RegularFile))
            throw new IOException("The recovery destination cannot be replaced.");
        var originalHash = initial == SnapshotNodeKind.RegularFile ? Fingerprint(target) : null;
        var staging = Path.Combine(Path.GetDirectoryName(target)!, ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = BackupFileOperations.CreatePrivateFile(staging))
            { output.Write(encrypted.Span); output.Flush(true); }
            Validate(target);
            if (SnapshotFileAccess.Kind(target) != initial) throw new IOException("Recovery destination changed type.");
            if (originalHash is not null && Fingerprint(target) != originalHash)
                throw new IOException("Recovery destination changed contents.");
            File.Move(staging, target, replaceExisting);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
    /// <summary>Reads a bounded regular encrypted file. Authentication remains the transaction coordinator's responsibility.</summary>
    public static byte[] Read(string path)
    {
        var target = Validate(path);
        using var input = SnapshotFileAccess.OpenRead(target);
        if (input.Length is < ArchiveEncryption.HeaderSize or > MaximumBytes || !ArchiveEncryption.IsEncrypted(input))
            throw new InvalidDataException("Invalid recovery artifact framing or size.");
        var bytes = new byte[(int)input.Length];
        try
        {
            input.ReadExactly(bytes);
            if (input.ReadByte() != -1) throw new IOException("Recovery artifact changed length.");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    /// <summary>Hashes bounded encrypted framing for observed replacement conflicts, without authenticating its contents.</summary>
    private static string Fingerprint(string path)
    {
        using var input = SnapshotFileAccess.OpenRead(path);
        if (input.Length is < ArchiveEncryption.HeaderSize or > MaximumBytes || !ArchiveEncryption.IsEncrypted(input))
            throw new InvalidDataException("Existing recovery artifact framing or size is invalid.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        try
        {
            long total = 0; int count;
            while ((count = input.Read(buffer)) != 0)
            {
                total += count;
                if (total > MaximumBytes) throw new InvalidDataException("Recovery artifact exceeded its size limit.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static string Validate(string path)
    {
        var target = Path.GetFullPath(path);
        for (var parent = Path.GetDirectoryName(target); parent is not null; parent = Path.GetDirectoryName(parent))
            if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory)
                throw new IOException("Recovery artifact directory is missing, linked or unsafe.");
        return target;
    }
}
