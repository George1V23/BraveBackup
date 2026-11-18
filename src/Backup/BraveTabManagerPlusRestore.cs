using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Frozen destination package and Local store evidence for a saved-window restore.</summary>
internal sealed record TabManagerPlusRestoreSelection(string RegistrationPath, string PackageFingerprint, string OriginalDigest);

/// <summary>Connects the portable saved-window update to the guarded Linux directory transaction.</summary>
internal static class BraveTabManagerPlusRestore
{
    internal const string ExtensionId = SavedWindows.OfficialExtensionId;

    /// <summary>Read-only eligibility check for the registered package and its initialized Local database.</summary>
    internal static TabManagerPlusRestoreSelection Review(string profile, CancellationToken token)
    {
        var (path, fingerprint) = ReadPackage(profile, token);
        using var original = Capture(profile, token);
        SavedWindowRestore.ValidateDestination(ReadFiles(original), token);
        return new(path, fingerprint, LinuxFlatDirectoryImage.Digest(original));
    }

    /// <summary>Builds one directory mutation that adds only the verified sessions recovery log.</summary>
    internal static LinuxRestoreDestination Build(BraveRestoreMapping mapping, Guid installationId,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var reviewed = mapping.TabManagerPlus!;
        var profile = mapping.Profile.ProfileDirectory;
        void VerifyPackage(CancellationToken cancellation)
        {
            var (path, fingerprint) = ReadPackage(profile, cancellation);
            if (path != reviewed.RegistrationPath || fingerprint != reviewed.PackageFingerprint)
                throw Changed();
        }

        VerifyPackage(token);
        using var original = Capture(profile, token);
        if (LinuxFlatDirectoryImage.Digest(original) != reviewed.OriginalDigest) throw Changed();
        using var update = SavedWindowRestore.Prepare(ReadFiles(original), payload, token);
        var image = LinuxFlatDirectoryImage.AddFile(original, update.FileName, update.Bytes);
        return new(new RestoreMutation(Guid.NewGuid(), installationId, mapping.SourceProfileId, "tabmanagerplus", image),
            mapping.Installation, mapping.Profile, ExtensionId)
        {
            IsLocalExtensionDirectory = true,
            RequiredOriginalDigest = reviewed.OriginalDigest,
            VerifyApplyContext = VerifyPackage
        };
    }

    private static (string Path, string Fingerprint) ReadPackage(string profile, CancellationToken token)
    {
        try
        {
            var package = BraveExtensionRestoreAssessment.ReadRegisteredPackage(profile, ExtensionId, SavedWindows.SupportedVersion, token);
            if (package.RegistrationPath is not { } path || package.PackageFingerprint is not { } fingerprint ||
                !BraveTabManagerPlusCapture.VersionDirectory(Path.GetFileName(path)))
                throw Changed();
            var directory = Path.Combine(profile, "Extensions", path);
            using var manifest = LinuxRestoreFileCapture.Read(directory, "manifest.json", token);
            if (!manifest.Exists) throw Changed();
            BraveTabManagerPlusCapture.ValidateManifest(manifest.Content.Bytes);
            return (path, fingerprint);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Text.Json.JsonException)
        {
            throw Changed();
        }
    }

    private static RestoreFileImage Capture(string profile, CancellationToken token)
    {
        using var directory = RecoveryDirectoryIdentity.Open(Path.Combine(profile, BraveExtensionLocalRestore.ParentName, ExtensionId));
        return LinuxFlatDirectoryImage.Capture(directory, token);
    }

    private static Dictionary<string, ReadOnlyMemory<byte>> ReadFiles(RestoreFileImage image) =>
        LinuxFlatDirectoryImage.Decode(image).ToDictionary(file => file.Name, file => file.Content, StringComparer.Ordinal);

    private static BackupOperationException Changed() =>
        new("restore-tabmanagerplus-target", "The destination requires a registered Tab Manager Plus 7.0.0 package and unchanged Local store.");
}
