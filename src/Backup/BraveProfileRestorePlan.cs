using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Maps an explicitly selected category to an existing destination profile.</summary>
internal sealed record BraveRestoreMapping(Guid SourceProfileId, BraveAppInfo Installation, BraveProfileInfo Profile, string Category)
{
    internal ExtensionLocalRestoreSelection? Extension { get; init; }
}

/// <summary>Builds a Linux restore session from supported categories in a reviewed archive.</summary>
internal static class BraveProfileRestorePlan
{
    /// <summary>Validates archive identity and prepares a write-free session for a caller-authorized restore.</summary>
    internal static LinuxRestoreSession Open(string archivePath, Guid reviewedArchiveId, string? archivePassword,
        string recoveryDirectory, string safetyPassword, IReadOnlyList<BraveRestoreMapping> mappings,
        IEnumerable<string> protectedDirectories, Func<ProcessPresence>? observe = null, CancellationToken token = default) =>
        Open(archivePath, reviewedArchiveId, archivePassword, recoveryDirectory, safetyPassword, mappings,
            protectedDirectories, Guid.NewGuid(), null, observe, token);

    /// <summary>Gets the exact file written by each supported category.</summary>
    internal static string TargetFileName(string category) => category switch
    {
        "bookmarks" => "Bookmarks",
        "settings" => BraveSettingsRestore.FileName,
        _ => throw new InvalidDataException("The selected category does not support restore.")
    };

    /// <summary>Checks the platform and recorded product/version required by the supported writers.</summary>
    internal static bool IsCompatible(ArchiveInstallation source, BraveAppInfo target) =>
        HostEnvironment.IsLinux && Version.TryParse(source.BrowserVersion, out _) &&
        source.BrowserVersion == target.Version && source.Product == target.Product.ToString();

    /// <summary>Opens a plan bound to the digest and operation ID retained by final review.</summary>
    internal static LinuxRestoreSession Open(string archivePath, Guid reviewedArchiveId, string? archivePassword,
        string recoveryDirectory, string safetyPassword, IReadOnlyList<BraveRestoreMapping> mappings,
        IEnumerable<string> protectedDirectories, Guid operationId, string? reviewedDigest,
        Func<ProcessPresence>? observe = null, CancellationToken token = default)
    {
        if (!HostEnvironment.IsLinux) throw new PlatformNotSupportedException("Profile restore requires Linux.");
        if (reviewedArchiveId == Guid.Empty || mappings.Count is < 1 or > 10_000)
            throw new InvalidDataException("A reviewed archive and explicit profile mappings are required.");
        var selected = mappings.Select(mapping => mapping with
        {
            Installation = mapping.Installation with { UserDataDirectory = Path.GetFullPath(mapping.Installation.UserDataDirectory) },
            Profile = mapping.Profile with
            {
                ProfileDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mapping.Profile.ProfileDirectory))
            }
        }).ToArray();
        var profiles = selected.GroupBy(mapping => mapping.SourceProfileId).ToArray();
        var targets = profiles.Select(group => (group.First().Installation, group.First().Profile)).ToArray();
        if (profiles.Any(group => group.Any(mapping =>
                mapping.Installation != group.First().Installation || mapping.Profile != group.First().Profile)) ||
            targets.Select(target => target.Profile.ProfileDirectory).Distinct(StringComparer.Ordinal).Count() != targets.Length ||
            selected.Any(mapping => (mapping.Category == "extensions") != (mapping.Extension is not null) ||
                (mapping.Extension is { } extension && !BraveExtensionBackup.ValidId(extension.Id))) ||
            selected.Select(mapping => (mapping.SourceProfileId, mapping.Category, mapping.Extension?.Id))
                .Distinct().Count() != selected.Length)
            throw new InvalidDataException("Each source profile must have one distinct destination and unique targets.");

        var path = Path.GetFullPath(archivePath);
        if (!string.Equals(Path.GetExtension(path), ".bravebackup", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .bravebackup archive.");
        var sourceContext = BraveRestoreContext.Capture(path, targets, observe, token);
        if (reviewedDigest is not null) sourceContext.VerifyReviewedDigest(reviewedDigest);
        var destinations = new List<LinuxRestoreDestination>();
        var baselines = new List<LinuxRestoreTargetLease>();
        try
        {
            using (var source = SnapshotFileAccess.OpenRead(path))
            {
                sourceContext.VerifyOpenedSource(source, token);
                source.Position = 0;
                var manifest = BraveSettingsBackup.ValidateArchive(source, archivePassword, token);
                if (manifest.ArchiveId != reviewedArchiveId) throw new InvalidDataException("The selected archive does not match the review.");
                source.Position = 0;
                using var decrypted = ArchiveEncryption.IsEncrypted(source)
                    ? ArchiveEncryption.OpenReader(source, archivePassword ?? string.Empty, token)
                    : null;
                var content = decrypted ?? source;
                content.Position = 0;
                using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
                var sources = manifest.Installations.SelectMany(installation =>
                    installation.Profiles.Select(profile => (installation, profile))).ToDictionary(pair => pair.profile.Id);
                var extensionSources = new Dictionary<Guid, Dictionary<string, (ExtensionSnapshotItem Item, ArchivedExtension Package)>>();
                long retained = 0;
                long retainedOriginals = 0;
                foreach (var mapping in selected)
                {
                    token.ThrowIfCancellationRequested();
                    if (!sources.TryGetValue(mapping.SourceProfileId, out var archived))
                        throw new InvalidDataException("A selected profile is missing from the archive.");
                    if (!IsCompatible(archived.installation, mapping.Installation))
                        throw new InvalidDataException("Restore requires matching Brave products and recorded browser versions.");

                    if (mapping.Extension is { } extension)
                    {
                        if (!extensionSources.TryGetValue(mapping.SourceProfileId, out var extensionItems))
                        {
                            var category = archived.profile.Categories.SingleOrDefault(category => category.Id == "extensions")
                                ?? throw new InvalidDataException("The selected archive profile lacks Extensions.");
                            var inventory = BraveExtensionBackup.ReadValidatedInventory(zip, category, token);
                            var budget = ArchiveLimits.MaxEntries;
                            var packages = ArchivedExtensionInventory.Describe(inventory, ref budget, token).Entries
                                ?? throw new InvalidDataException("The archived extension inventory exceeds the comparison limit.");
                            extensionItems = inventory.Extensions.Zip(packages).ToDictionary(pair => pair.First.Id,
                                pair => (pair.First, pair.Second), StringComparer.Ordinal);
                            extensionSources.Add(mapping.SourceProfileId, extensionItems);
                        }

                        if (!extensionItems.TryGetValue(extension.Id, out var entry))
                            throw new InvalidDataException("The reviewed extension is missing from the archive.");
                        var target = BraveExtensionLocalRestore.Build(zip, entry.Item, entry.Package, mapping,
                            archived.installation.Id, RestoreSafetyFormat.MaximumBytes - retained, token);
                        destinations.Add(target);
                        retained += target.Mutation.Replacement.Content.Bytes.Length + target.Mutation.Replacement.Metadata.Bytes.Length;
                        if (retained > RestoreSafetyFormat.MaximumBytes)
                            throw new InvalidDataException("Replacements exceed the restore retention limit.");
                        continue;
                    }

                    var fileName = TargetFileName(mapping.Category);
                    var bookmarks = mapping.Category == "bookmarks";
                    var role = bookmarks ? "native-source" : "portable-settings";
                    var descriptor = archived.profile.Categories.SingleOrDefault(category => category.Id == mapping.Category)
                        ?.Files.Single(file => file.Role == role)
                        ?? throw new InvalidDataException("The selected archive profile lacks the requested category.");
                    if (retained + descriptor.Length > RestoreSafetyFormat.MaximumBytes)
                        throw new InvalidDataException("Selected snapshots exceed the restore retention limit.");

                    var bytes = ReadSnapshot(zip, descriptor,
                        bookmarks ? LinuxRestoreFileCapture.MaximumBytes : ArchiveLimits.MaxSettingsBytes, token);
                    try
                    {
                        var baseline = LinuxRestoreTargetLease.Capture(mapping.Profile.ProfileDirectory, fileName, token);
                        baselines.Add(baseline);
                        using var original = baseline.CopyOriginal();
                        retainedOriginals += original.Content.Bytes.Length + original.Metadata.Bytes.Length;
                        if (retainedOriginals > RestoreSafetyFormat.MaximumBytes)
                            throw new InvalidDataException("Original files exceed the restore retention limit.");
                        var image = BuildReplacement(mapping, original, bytes, token);
                        destinations.Add(new(new RestoreMutation(Guid.NewGuid(), archived.installation.Id,
                            archived.profile.Id, mapping.Category, image), mapping.Installation, mapping.Profile, fileName));
                        retained += image.Content.Bytes.Length + image.Metadata.Bytes.Length;
                        if (retained > RestoreSafetyFormat.MaximumBytes)
                            throw new InvalidDataException("Replacements exceed the restore retention limit.");
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                sourceContext.VerifyOpenedSource(source, token);
            }

            var session = LinuxRestoreSession.Open(operationId, path, recoveryDirectory, destinations, safetyPassword,
                protectedDirectories, observe, token);
            try
            {
                session.VerifySource(sourceContext);
                sourceContext.Verify(token);
                foreach (var baseline in baselines) baseline.VerifyOriginal(token);
                return session;
            }
            catch { session.Dispose(); throw; }
        }
        finally
        {
            foreach (var target in destinations) target.Mutation.Replacement.Dispose();
            foreach (var baseline in baselines) baseline.Dispose();
        }
    }

    private static RestoreFileImage BuildReplacement(BraveRestoreMapping mapping, RestoreFileImage original,
        byte[] payload, CancellationToken token)
    {
        if (mapping.Category == "bookmarks")
        {
            ValidateNativeSnapshot(payload, token);
            if (original.Exists) ValidateNativeSnapshot(original.Content.Bytes, token);
            var metadata = original.Exists ? original.Metadata.Bytes.ToArray() : CaptureTemplateMetadata(mapping.Profile.ProfileDirectory);
            return new RestoreFileImage(true, payload, metadata);
        }

        if (!original.Exists)
            throw new BackupOperationException("restore-settings-missing",
                "The selected destination does not contain an existing Preferences file.");

        using var secure = LinuxRestoreFileCapture.Read(mapping.Profile.ProfileDirectory, BraveSettingsRestore.SecureFileName, token);
        ReadOnlyMemory<byte>? protectedPrefs = secure.Exists ? secure.Content.Bytes : (ReadOnlyMemory<byte>?)null;
        var settings = BraveSettingsRestore.Apply(original.Content.Bytes, protectedPrefs, payload, token);
        try { return new RestoreFileImage(true, settings, original.Metadata.Bytes.Span); }
        finally { CryptographicOperations.ZeroMemory(settings); }
    }

    private static byte[] ReadSnapshot(ZipArchive zip, ArchiveFile descriptor, int maximumBytes, CancellationToken token)
    {
        var entry = zip.GetEntry(descriptor.Path);
        if (entry is null || entry.Length != descriptor.Length || descriptor.Length < 1 || descriptor.Length > maximumBytes)
            throw new InvalidDataException("Restore snapshot has an unsupported size.");
        var bytes = new byte[(int)descriptor.Length];
        try
        {
            using var input = entry.Open();
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw new InvalidDataException("Restore snapshot is truncated.");
                offset += count;
            }
            if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Sha256)
                throw new InvalidDataException("Restore snapshot does not match its archive descriptor.");
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    private static void ValidateNativeSnapshot(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (bytes.Span.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
        try
        {
            using var json = JsonDocument.Parse(bytes);
            CheckUniqueKeys(json.RootElement, token);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Native bookmarks snapshot must be a JSON object.");
            if (json.RootElement.TryGetProperty("sync_metadata", out var sync) &&
                (sync.ValueKind != JsonValueKind.String || sync.GetString() != string.Empty))
                throw new InvalidDataException("Bookmarks restore does not support browser Sync metadata.");
            var parsed = BraveBookmarksParser.Parse(json.RootElement.GetRawText());
            var roots = parsed.Roots.Select(root => root.Key).ToHashSet(StringComparer.Ordinal);
            if (parsed.Version != 1 || !roots.SetEquals(["bookmark_bar", "other", "synced"]))
                throw new InvalidDataException("Native bookmarks snapshot uses unsupported roots or version.");
        }
        catch (JsonException) { throw new InvalidDataException("Native bookmarks snapshot is malformed."); }
    }

    private static void CheckUniqueKeys(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Native bookmarks snapshot has duplicate property names.");
                CheckUniqueKeys(property.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) CheckUniqueKeys(item, token);
        }
    }

    private static byte[] CaptureTemplateMetadata(string profilePath)
    {
        using var directory = RecoveryDirectoryIdentity.Open(profilePath);
        using var preferences = directory.OpenReadChild("Preferences");
        return LinuxRecoveryMetadata.Capture(preferences.SafeFileHandle);
    }
}
