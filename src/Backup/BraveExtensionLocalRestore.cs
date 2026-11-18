using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Frozen package registration and Local directory evidence from the reviewed destination.</summary>
internal sealed record ExtensionLocalRestoreSelection(string Id, string Version, string RegistrationPath,
    string PackageFingerprint, string OriginalDigest);

/// <summary>Reads supported archived Local LevelDB images without opening them as databases or executing extensions.</summary>
internal static class BraveExtensionLocalRestore
{
    internal const string ParentName = "Local Extension Settings";

    /// <summary>Checks the bounded archive structure and database records for the supported single-package case.</summary>
    internal static bool CanReadArchivedStore(ZipArchive zip, ExtensionSnapshotItem extension, ref long remainingBytes, CancellationToken token)
    {
        if (extension.LocalStorage != "present" || extension.Versions.Length != 1) return false;
        if (!TryGetStoreFiles(extension, out var files, out var size) || size > remainingBytes) return false;
        remainingBytes -= size;
        try { return WithArchiveStore(zip, files, _ => true, token); }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>Captures evidence for an existing safe Local store after a verified package match.</summary>
    internal static ExtensionLocalRestoreSelection? Review(ExtensionRegistrationCheck check, string profilePath,
        ref long remainingBytes, CancellationToken token)
    {
        if (!check.Archive.LocalValidated || check.Match != ExtensionRegistrationMatch.PackageMatches || remainingBytes <= 0)
            return null;
        try
        {
            using var image = Capture(profilePath, check.Archive.Id, token);
            remainingBytes -= image.Content.Bytes.Length;
            if (remainingBytes < 0) return null;
            return new(check.Archive.Id, check.RegisteredVersion!, check.RegistrationPath!, check.PackageFingerprint!,
                LinuxFlatDirectoryImage.Digest(image));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Uses the verified archive and frozen package/directory evidence to build one atomic target.</summary>
    internal static LinuxRestoreDestination Build(ZipArchive zip, ExtensionSnapshotItem extension, ArchivedExtension archived,
        BraveRestoreMapping mapping, Guid installationId, long remainingBytes, CancellationToken token)
    {
        var selected = mapping.Extension!;
        if (extension.Versions.Length != 1 || extension.LocalStorage != "present" ||
            !TryGetStoreFiles(extension, out var files, out var size) || size > remainingBytes)
            throw new BackupOperationException("restore-extension-storage", "The archived Local store is unsupported or exceeds the restore limit.");

        void VerifyRegistration(CancellationToken cancellation)
        {
            var result = BraveExtensionRestoreAssessment.Check(mapping.Profile.ProfileDirectory, [archived], cancellation).Single();
            if (result.Match != ExtensionRegistrationMatch.PackageMatches || result.RegisteredVersion != selected.Version ||
                result.RegistrationPath != selected.RegistrationPath || result.PackageFingerprint != selected.PackageFingerprint)
                throw new BackupOperationException("restore-extension-registration", "The registered extension changed after Recheck.");
        }

        VerifyRegistration(token);
        using var original = Capture(mapping.Profile.ProfileDirectory, selected.Id, token);
        if (LinuxFlatDirectoryImage.Digest(original) != selected.OriginalDigest)
            throw new BackupOperationException("restore-extension-target", "The destination Local store changed after Recheck.");
        RestoreFileImage replacement;
        try
        {
            replacement = WithArchiveStore(zip, files, contents => LinuxFlatDirectoryImage.Encode(contents, original.Metadata.Bytes), token);
        }
        catch (InvalidDataException)
        {
            throw new BackupOperationException("restore-extension-storage", "The archived Local store could not be validated.");
        }

        return new LinuxRestoreDestination(new(Guid.NewGuid(), installationId, mapping.SourceProfileId, "extensions", replacement),
            mapping.Installation, mapping.Profile, selected.Id)
        {
            IsLocalExtensionDirectory = true,
            RequiredOriginalDigest = selected.OriginalDigest,
            VerifyApplyContext = VerifyRegistration
        };
    }

    private static RestoreFileImage Capture(string profilePath, string id, CancellationToken token)
    {
        if (!BraveExtensionBackup.ValidId(id)) throw new InvalidDataException("Invalid restore extension ID.");
        using var directory = RecoveryDirectoryIdentity.Open(Path.Combine(profilePath, ParentName, id));
        return LinuxFlatDirectoryImage.Capture(directory, token);
    }

    private static bool TryGetStoreFiles(ExtensionSnapshotItem extension, out ExtensionSnapshotFile[] files, out long imageBytes)
    {
        files = extension.Files.Where(file => file.Area == "local").ToArray();
        imageBytes = 12;
        if (files.Length is 0 or > LinuxFlatDirectoryImage.MaximumFiles ||
            extension.Directories.Any(path => path.StartsWith("local/", StringComparison.Ordinal)))
            return false;
        foreach (var file in files)
        {
            if (!IsLevelDbFile(file.RelativePath) || (file.RelativePath == "LOCK" && file.Length != 0))
                return false;
            imageBytes += 8L + file.RelativePath.Length + LinuxRecoveryMetadata.MetadataSize + file.Length;
            if (imageBytes > LinuxFlatDirectoryImage.MaximumBytes) return false;
        }
        return true;
    }

    private static bool IsLevelDbFile(string name)
    {
        if (name is "CURRENT" or "LOCK" or "LOG" or "LOG.old") return true;
        if (name.StartsWith("MANIFEST-", StringComparison.Ordinal)) return IsFileNumber(name.AsSpan(9));
        var suffix = name.LastIndexOf('.');
        return suffix > 0 && (name[suffix..] is ".ldb" or ".sst" or ".log") && IsFileNumber(name.AsSpan(0, suffix));
    }

    private static bool IsFileNumber(ReadOnlySpan<char> number) =>
        number.Length is >= 6 and <= 20 && !number.ContainsAnyExceptInRange('0', '9') &&
        ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed != 0;

    private static T WithArchiveStore<T>(ZipArchive zip, ExtensionSnapshotFile[] files,
        Func<IReadOnlyDictionary<string, ReadOnlyMemory<byte>>, T> consume, CancellationToken token)
    {
        var bytes = new List<byte[]>(files.Length);
        var contents = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var entry = zip.GetEntry(file.ArchivePath);
                if (entry is null || entry.Length != file.Length) throw new InvalidDataException("Invalid archive Local store entry.");
                var payload = new byte[(int)file.Length];
                bytes.Add(payload);
                using var input = entry.Open();
                for (var offset = 0; offset < payload.Length; offset += 65536)
                {
                    token.ThrowIfCancellationRequested();
                    input.ReadExactly(payload.AsSpan(offset, Math.Min(65536, payload.Length - offset)));
                }
                if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(payload)) != file.Sha256)
                    throw new InvalidDataException("Archived Local store entry changed.");
                contents.Add(file.RelativePath, payload);
            }

            OfflineLevelDb.ValidateSnapshot(contents, token);
            return consume(contents);
        }
        finally
        {
            foreach (var payload in bytes) CryptographicOperations.ZeroMemory(payload);
        }
    }
}
