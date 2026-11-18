using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Observed package version and fingerprint of relative file paths and content hashes.</summary>
internal sealed record ArchivedExtensionPackage(string Version, string Fingerprint);

/// <summary>Archived identity and raw storage presence, with no executable or database bytes retained.</summary>
internal sealed record ArchivedExtension(string Id, ArchivedExtensionPackage[] Packages, string LocalStorage, string SyncStorage);

/// <summary>Compact archive inventory used by the Restore tab. Entries can be omitted when the inspection budget is exceeded.</summary>
internal sealed record ArchivedExtensionInventory(int Count, ArchivedExtension[]? Entries)
{
    /// <summary>Summarizes a validated inventory without retaining arbitrary manifest strings or raw payloads.</summary>
    internal static ArchivedExtensionInventory Describe(ExtensionSnapshotInventory inventory, ref int remaining, CancellationToken token)
    {
        var count = inventory.Extensions.Length;
        var entries = count + inventory.Extensions.Sum(extension => extension.Versions.Length);
        if (entries > remaining) return new(count, null);
        remaining -= entries;

        var extensions = new List<ArchivedExtension>(count);
        foreach (var extension in inventory.Extensions)
        {
            token.ThrowIfCancellationRequested();
            var files = extension.Files.Where(file => file.Area == "package")
                .ToLookup(file => file.RelativePath[..file.RelativePath.IndexOf('/')], StringComparer.OrdinalIgnoreCase);
            var packages = new List<ArchivedExtensionPackage>(extension.Versions.Length);
            foreach (var version in extension.Versions)
            {
                token.ThrowIfCancellationRequested();
                var prefix = version.Directory.Length + 1;
                var fingerprint = BraveExtensionRestoreAssessment.Fingerprint(files[version.Directory]
                    .Select(file => (file.RelativePath[prefix..], file.Sha256)), token);
                packages.Add(new(version.Version, fingerprint));
            }
            extensions.Add(new(extension.Id, packages.ToArray(), extension.LocalStorage, extension.SyncStorage));
        }

        return new(count, extensions.ToArray());
    }
}

/// <summary>On-disk registration/package evidence, independent of whether Brave loads or enables an extension.</summary>
internal enum ExtensionRegistrationMatch
{
    ArchiveHasNoPackage,
    RegistrationMissing,
    UnsupportedRegistration,
    VersionNotArchived,
    PackageDiffers,
    PackageMatches,
    CannotVerify
}

/// <summary>Result of a read-only comparison for one archived identity.</summary>
internal sealed record ExtensionRegistrationCheck(ArchivedExtension Archive, ExtensionRegistrationMatch Match, string? RegisteredVersion = null);

/// <summary>Compares the destination's registered internal extension version and package contents to an inspected archive.</summary>
internal static class BraveExtensionRestoreAssessment
{
    private const int InternalLocation = 1;
    private const int MaxTreeDepth = 64;
    private const long MaxTargetPackageBytes = 512L * 1024 * 1024;

    /// <summary>Reads registration metadata and hashes eligible package files. Storage and installation state are never changed.</summary>
    internal static ExtensionRegistrationCheck[] Check(string profilePath, IReadOnlyList<ArchivedExtension> archived, CancellationToken token)
    {
        if (!HostEnvironment.IsLinux) throw new PlatformNotSupportedException("Extension registration checks require Linux.");
        token.ThrowIfCancellationRequested();
        if (archived.Count == 0) return [];

        using var normal = LinuxRestoreFileCapture.Read(profilePath, "Preferences", token);
        using var secure = LinuxRestoreFileCapture.Read(profilePath, "Secure Preferences", token);
        if (!normal.Exists && !secure.Exists) throw new IOException("No destination extension registration store.");
        using var preferences = normal.Exists ? BraveSettingsBackup.ParsePreferences(normal.Content.Bytes, token) : null;
        using var securePreferences = secure.Exists ? BraveSettingsBackup.ParsePreferences(secure.Content.Bytes, token) : null;
        var registration = Registration(preferences);
        var secureRegistration = Registration(securePreferences);
        var bytesLeft = MaxTargetPackageBytes;
        var pathsLeft = ArchiveLimits.MaxEntries;
        var matches = new ExtensionRegistrationCheck[archived.Count];
        for (var index = 0; index < matches.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            matches[index] = Compare(profilePath, archived[index], registration, secureRegistration,
                ref bytesLeft, ref pathsLeft, token);
        }
        return matches;
    }

    private static JsonElement Registration(JsonDocument? preferences)
    {
        if (preferences is null || BraveSettingsBackup.Find(preferences.RootElement, "extensions.settings") is not { } record)
            return default;
        if (record.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid destination extension registration.");
        return record;
    }

    private static ExtensionRegistrationCheck Compare(string profilePath, ArchivedExtension extension,
        JsonElement registration, JsonElement secureRegistration, ref long bytesLeft, ref int pathsLeft, CancellationToken token)
    {
        if (extension.Packages.Length == 0)
            return new(extension, ExtensionRegistrationMatch.ArchiveHasNoPackage);
        var normal = ReadRecord(registration, extension.Id);
        var secure = ReadRecord(secureRegistration, extension.Id);
        if (normal is null && secure is null) return new(extension, ExtensionRegistrationMatch.RegistrationMissing);
        if (normal is not null && secure is not null) return new(extension, ExtensionRegistrationMatch.CannotVerify);
        var record = (secure ?? normal)!.Value;
        if (record.ValueKind != JsonValueKind.Object ||
            !record.TryGetProperty("location", out var location) || location.ValueKind != JsonValueKind.Number ||
            !location.TryGetInt32(out var locationValue) || locationValue != InternalLocation ||
            !record.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.String ||
            !record.TryGetProperty("manifest", out var manifest) || manifest.ValueKind != JsonValueKind.Object ||
            !manifest.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String)
            return new(extension, ExtensionRegistrationMatch.UnsupportedRegistration);

        var relative = path.GetString()!;
        var parts = relative.Split('/');
        if (parts.Length != 2 || parts[0] != extension.Id || !ExtensionSnapshotFormat.SafePath(relative))
            return new(extension, ExtensionRegistrationMatch.UnsupportedRegistration);

        var registeredVersion = version.GetString()!;
        if (!BraveExtensionBackup.ValidVersion(registeredVersion))
            return new(extension, ExtensionRegistrationMatch.UnsupportedRegistration);
        var candidates = extension.Packages.Where(package => package.Version == registeredVersion).ToArray();
        if (candidates.Length == 0)
            return new(extension, ExtensionRegistrationMatch.VersionNotArchived, registeredVersion);

        var root = Path.Combine(profilePath, "Extensions", parts[0], parts[1]);
        try
        {
            var files = new List<(string RelativePath, string Hash)>();
            Walk(root, string.Empty, files, ref bytesLeft, ref pathsLeft, 0, token);
            var fingerprint = Fingerprint(files, token);
            return new(extension, candidates.Any(package => package.Fingerprint == fingerprint)
                ? ExtensionRegistrationMatch.PackageMatches : ExtensionRegistrationMatch.PackageDiffers, registeredVersion);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(extension, ExtensionRegistrationMatch.CannotVerify, registeredVersion);
        }
    }

    private static JsonElement? ReadRecord(JsonElement registration, string id) =>
        registration.ValueKind == JsonValueKind.Object && registration.TryGetProperty(id, out var record) ? record : null;

    private static void Walk(string path, string relative, List<(string, string)> files,
        ref long bytesLeft, ref int pathsLeft, int depth, CancellationToken token)
    {
        if (depth >= MaxTreeDepth) throw new IOException("Extension package path exceeds the comparison limit.");
        using var directory = RecoveryDirectoryIdentity.Open(path);
        var directoryStamp = Directory.GetLastWriteTimeUtc(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            token.ThrowIfCancellationRequested();
            if (pathsLeft-- <= 0) throw new IOException("Extension comparison path limit exceeded.");
            var name = Path.GetFileName(entry);
            var child = relative + name;
            if (!ExtensionSnapshotFormat.SafePath(child)) throw new IOException("Unsafe extension package path.");
            if (SnapshotFileAccess.Kind(entry) == SnapshotNodeKind.Directory)
            {
                Walk(entry, child + "/", files, ref bytesLeft, ref pathsLeft, depth + 1, token);
                continue;
            }

            using var input = directory.OpenReadChild(name);
            var length = input.Length;
            if (length > ArchiveLimits.MaxBookmarkBytes || length > bytesLeft)
                throw new IOException("Extension comparison byte limit exceeded.");
            bytesLeft -= length;
            var stamp = File.GetLastWriteTimeUtc(entry);
            var hash = HashFile(input, length, token);
            directory.VerifyChildIdentity(name, input.SafeFileHandle);
            if (File.GetLastWriteTimeUtc(entry) != stamp || input.Length != length)
                throw new IOException("Extension package changed during comparison.");
            files.Add((child, hash));
        }
        directory.Verify();
        if (Directory.GetLastWriteTimeUtc(path) != directoryStamp)
            throw new IOException("Extension package directory changed during comparison.");
    }

    private static string HashFile(Stream input, long length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long remaining = length;
            while (remaining != 0)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(buffer, 0, (int)Math.Min(remaining, buffer.Length));
                if (count == 0) throw new IOException("Extension package was truncated during comparison.");
                remaining -= count;
                hash.AppendData(buffer.AsSpan(0, count));
            }
            if (input.ReadByte() != -1) throw new IOException("Extension package grew during comparison.");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    /// <summary>Hashes the sorted relative path and SHA-256 pairs. The backup path rules exclude framing delimiters.</summary>
    internal static string Fingerprint(IEnumerable<(string RelativePath, string Hash)> files, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (path, digest) in files.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(Encoding.UTF8.GetBytes(path + "\0" + digest + "\0"));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
