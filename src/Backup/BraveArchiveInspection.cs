using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Read-only inspection metadata. A null manifest means an encrypted source still needs unlocking.</summary>
public sealed record BraveArchiveInspectionResult(string SourcePath, bool Encrypted, ArchiveManifest? Manifest)
{
    /// <summary>Digest of validated source bytes, used to bind later restore review to the inspected archive.</summary>
    public string? SourceSha256 { get; init; }

    /// <summary>Verified package fingerprints and storage presence from the inspected archive.</summary>
    internal IReadOnlyDictionary<Guid, ArchivedExtensionInventory> ExtensionInventories { get; init; } =
        new Dictionary<Guid, ArchivedExtensionInventory>();

    /// <summary>Persisted session counts and verified provenance without archived URLs or titles.</summary>
    internal IReadOnlyDictionary<Guid, ArchivedSessionInventory> Sessions { get; init; } =
        new Dictionary<Guid, ArchivedSessionInventory>();

    /// <summary>Validated password representation and counts, with no credential data.</summary>
    internal IReadOnlyDictionary<Guid, ArchivedPasswordInventory> Passwords { get; init; } =
        new Dictionary<Guid, ArchivedPasswordInventory>();
}

/// <summary>Inspects existing archive contracts without extracting payloads or accessing destination profiles.</summary>
public static class BraveArchiveInspection
{
    /// <summary>Detects encryption, validates an unlocked archive and rejects source changes; retains no decrypted payload.</summary>
    public static BraveArchiveInspectionResult Read(string path, string? password = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var sourcePath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(sourcePath), ".bravebackup", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .bravebackup file.");
        using var source = SnapshotFileAccess.OpenRead(sourcePath);
        if (source.Length > ArchiveLimits.MaxBytes + 3L * 1024 * 1024)
            throw new InvalidDataException("Archive exceeds its supported size.");
        var encrypted = ArchiveEncryption.IsEncrypted(source);
        if (encrypted && password is null) return new(sourcePath, true, null);
        var before = Hash(source, token);
        source.Position = 0;
        var extensionInventories = new Dictionary<Guid, ArchivedExtensionInventory>();
        var sessions = new Dictionary<Guid, ArchivedSessionInventory>();
        var passwords = new Dictionary<Guid, ArchivedPasswordInventory>();
        var remainingExtensionEntries = ArchiveLimits.MaxEntries;
        long remainingLocalBytes = RestoreSafetyFormat.MaximumBytes;
        var manifest = BraveSettingsBackup.ValidateArchive(source, password, token, (profileId, inventory, zip) =>
        {
            extensionInventories.Add(profileId,
                ArchivedExtensionInventory.Describe(inventory, ref remainingExtensionEntries, token,
                    extension => HostEnvironment.IsLinux &&
                        BraveExtensionLocalRestore.CanReadArchivedStore(zip, extension, ref remainingLocalBytes, token)));
        }, (profileId, session) => sessions.Add(profileId, ArchivedSessionInventory.FromValidated(session)), passwords.Add);
        if (Hash(source, token) != before)
            throw new IOException("The source archive changed during inspection.");
        using (var current = SnapshotFileAccess.OpenRead(sourcePath))
            if (Hash(current, token) != before) throw new IOException("The source archive changed during inspection.");
        token.ThrowIfCancellationRequested();
        return new(sourcePath, encrypted, manifest)
        {
            SourceSha256 = before, ExtensionInventories = extensionInventories, Sessions = sessions, Passwords = passwords
        };
    }

    private static string Hash(Stream source, CancellationToken token)
    {
        source.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        try
        {
            long total = 0; int count;
            while ((count = source.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested(); total += count;
                if (total > ArchiveLimits.MaxBytes + 3L * 1024 * 1024) throw new InvalidDataException("Archive exceeds its supported size.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
