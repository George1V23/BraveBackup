using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Explicit mapping from an archived profile to a detected destination profile.</summary>
internal sealed record BookmarkRestoreMapping(Guid SourceProfileId, BraveAppInfo Installation, BraveProfileInfo Profile);

/// <summary>Builds a Linux restore session from validated native Bookmarks snapshots in a reviewed archive.</summary>
internal static class BraveBookmarksRestorePlan
{
    /// <summary>Validates archive identity and prepares a write-free session for a caller-authorized bookmarks restore.</summary>
    internal static LinuxRestoreSession Open(string archivePath, Guid reviewedArchiveId, string? archivePassword,
        string recoveryDirectory, string safetyPassword, IReadOnlyList<BookmarkRestoreMapping> mappings,
        IEnumerable<string> protectedDirectories, Func<ProcessPresence>? observe = null, CancellationToken token = default)
    {
        if (!HostEnvironment.IsLinux) throw new PlatformNotSupportedException("Bookmarks restore requires Linux.");
        if (reviewedArchiveId == Guid.Empty || mappings.Count is < 1 or > 10_000)
            throw new InvalidDataException("A reviewed archive and explicit profile mappings are required.");
        var selected = mappings.Select(mapping => mapping with
        {
            Installation = mapping.Installation with { UserDataDirectory = Path.GetFullPath(mapping.Installation.UserDataDirectory) },
            Profile = mapping.Profile with { ProfileDirectory = Path.GetFullPath(mapping.Profile.ProfileDirectory) }
        }).ToArray();
        if (selected.Select(mapping => mapping.SourceProfileId).Distinct().Count() != selected.Length)
            throw new InvalidDataException("Each source profile must have exactly one destination.");

        var path = Path.GetFullPath(archivePath);
        if (!string.Equals(Path.GetExtension(path), ".bravebackup", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a .bravebackup archive.");
        var sourceContext = BraveRestoreContext.Capture(path, selected.Select(mapping =>
            (mapping.Installation, mapping.Profile)).ToArray(), observe, token);
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
                long retained = 0;
                long retainedOriginals = 0;
                foreach (var mapping in selected)
                {
                    token.ThrowIfCancellationRequested();
                    if (!sources.TryGetValue(mapping.SourceProfileId, out var archived))
                        throw new InvalidDataException("A selected profile is missing from the archive.");
                    if (!Version.TryParse(archived.installation.BrowserVersion, out _) ||
                        archived.installation.BrowserVersion != mapping.Installation.Version ||
                        archived.installation.Product != mapping.Installation.Product.ToString())
                        throw new InvalidDataException("Bookmarks restore requires matching Brave products and recorded browser versions.");
                    var descriptor = archived.profile.Categories.SingleOrDefault(category => category.Id == "bookmarks")
                        ?.Files.Single(file => file.Role == "native-source")
                        ?? throw new InvalidDataException("The selected profile does not contain Bookmarks.");
                    retained += descriptor.Length;
                    if (retained > RestoreSafetyFormat.MaximumBytes)
                        throw new InvalidDataException("Selected bookmark snapshots exceed the restore retention limit.");

                    var bytes = ReadSnapshot(zip, descriptor, token);
                    try
                    {
                        ValidateNativeSnapshot(bytes, token);
                        var baseline = LinuxRestoreTargetLease.Capture(mapping.Profile.ProfileDirectory, "Bookmarks", token);
                        baselines.Add(baseline);
                        using var original = baseline.CopyOriginal();
                        retainedOriginals += original.Content.Bytes.Length + original.Metadata.Bytes.Length;
                        if (retainedOriginals > RestoreSafetyFormat.MaximumBytes)
                            throw new InvalidDataException("Bookmark originals exceed the restore retention limit.");
                        if (original.Exists) ValidateNativeSnapshot(original.Content.Bytes, token);
                        var metadata = original.Exists ? original.Metadata.Bytes.ToArray() : CaptureTemplateMetadata(mapping.Profile.ProfileDirectory);
                        var image = new RestoreFileImage(true, bytes, metadata);
                        destinations.Add(new(new RestoreMutation(Guid.NewGuid(), archived.installation.Id,
                            archived.profile.Id, "bookmarks", image), mapping.Installation, mapping.Profile, "Bookmarks"));
                    }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                sourceContext.VerifyOpenedSource(source, token);
            }

            var session = LinuxRestoreSession.Open(path, recoveryDirectory, destinations, safetyPassword,
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

    private static byte[] ReadSnapshot(ZipArchive zip, ArchiveFile descriptor, CancellationToken token)
    {
        var entry = zip.GetEntry(descriptor.Path);
        if (entry is null || entry.Length != descriptor.Length || descriptor.Length is < 1 or > LinuxRestoreFileCapture.MaximumBytes)
            throw new InvalidDataException("Native bookmarks snapshot has an unsupported size.");
        var bytes = new byte[(int)descriptor.Length];
        try
        {
            using var input = entry.Open();
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw new InvalidDataException("Native bookmarks snapshot is truncated.");
                offset += count;
            }
            if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != descriptor.Sha256)
                throw new InvalidDataException("Native bookmarks snapshot does not match its archive descriptor.");
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
