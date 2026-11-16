using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Application-owned raw extension snapshot metadata, with no installation or restore side effects.</summary>
public sealed record ExtensionSnapshotInventory(int SchemaVersion, ExtensionSnapshotItem[] Extensions);

/// <summary>Observed package versions and explicit supported storage presence for one archive-local extension ID.</summary>
public sealed record ExtensionSnapshotItem(
    string Id,
    ExtensionSnapshotVersion[] Versions,
    string LocalStorage,
    string SyncStorage,
    string[] Directories,
    ExtensionSnapshotFile[] Files);

/// <summary>Manifest metadata for an observed version directory; does not identify the active version.</summary>
public sealed record ExtensionSnapshotVersion(string Directory, string Version, string Name);

/// <summary>One raw regular file, its source-relative identity and archive descriptor.</summary>
public sealed record ExtensionSnapshotFile(
    string Area,
    string RelativePath,
    string ArchivePath,
    long Length,
    string Sha256);

/// <summary>Browser-independent integrity and structure rules for the extensions-v1 archive category.</summary>
public static class ExtensionSnapshotFormat
{
    /// <summary>Checks canonical relative paths before writing or trusting snapshot metadata.</summary>
    public static bool SafePath(string? path) => !string.IsNullOrEmpty(path) && path.Length <= 768 &&
                                                 path.Split('/').All(part =>
                                                     part.Length > 0 && part is not ("." or "..") &&
                                                     !part.Any(character =>
                                                         char.IsControl(character) || character is '\\' or ':' or '*'
                                                             or '?' or '"' or '<' or '>' or '|') &&
                                                     !part.EndsWith('.') && !part.EndsWith(' '));

    /// <summary>Validates raw file hashes and inventory linkage without parsing binary data as JSON.</summary>
    public static void ValidateCategory(ZipArchive zip, Dictionary<string, ZipArchiveEntry> remaining,
        string prefix, ArchiveCategory category, CancellationToken token)
    {
        if (category.Files.Length is < 1 or > ArchiveLimits.MaxEntries)
            throw new InvalidDataException("Invalid extension file count.");
        var files = new Dictionary<string, ArchiveFile>(StringComparer.Ordinal);
        foreach (var file in category.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || file.Path is null || !files.TryAdd(file.Path, file) || file.Length < 0 ||
                file.Length > (file.Role == "extension-inventory"
                    ? ArchiveLimits.MaxManifestBytes
                    : ArchiveLimits.MaxBookmarkBytes) ||
                file.Sha256 is null || file.Sha256.Length != 64 ||
                file.Sha256.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                !remaining.Remove(file.Path, out var entry) || entry.Length != file.Length)
                throw new InvalidDataException("Invalid extension payload descriptor.");
            VerifyHash(entry, file, token);
        }

        var inventoryPath = prefix + "inventory-v1.json";
        if (!files.Remove(inventoryPath, out var inventoryFile) || inventoryFile.Role != "extension-inventory")
            throw new InvalidDataException("Missing extension inventory.");
        var bytes = new byte[checked((int)inventoryFile.Length)];
        ExtensionSnapshotInventory inventory;
        try
        {
            using var input = zip.GetEntry(inventoryPath)!.Open();
            input.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            RejectDuplicates(document.RootElement);
            inventory = document.Deserialize<ExtensionSnapshotInventory>(ArchiveManifest.JsonOptions)
                        ?? throw new InvalidDataException("Missing extension inventory content.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        ValidateInventory(inventory, prefix, files);
    }

    /// <summary>Checks that every inventory reference matches one declared file and every state is explicit.</summary>
    public static void ValidateInventory(ExtensionSnapshotInventory inventory, string prefix,
        Dictionary<string, ArchiveFile> files)
    {
        if (inventory.SchemaVersion != 1 || inventory.Extensions is null || inventory.Extensions.Length > 10000)
            throw new InvalidDataException("Unsupported extension inventory.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory.Extensions)
        {
            if (item is null || !SafePath(item.Id) || item.Id.Contains('/') || !ids.Add(item.Id) ||
                item.Versions is null || item.Directories is null || item.Files is null ||
                item.Versions.Length > ArchiveLimits.MaxEntries || item.Directories.Length > ArchiveLimits.MaxEntries)
                throw new InvalidDataException("Invalid extension identity or directory metadata.");
            var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var version in item.Versions)
                if (version is null || !SafePath(version.Directory) || version.Directory.Contains('/') ||
                    !versions.Add(version.Directory) ||
                    string.IsNullOrEmpty(version.Version) || version.Version.Length > 256 ||
                    string.IsNullOrEmpty(version.Name) || version.Name.Length > 32768)
                    throw new InvalidDataException("Invalid extension version metadata.");
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in item.Directories)
                if (!SafePath(directory) || !directories.Add(directory) || !ValidAreaPath(directory, versions))
                    throw new InvalidDataException("Invalid extension snapshot directory.");
            foreach (var directory in directories)
            {
                var parent = directory.Contains('/') ? directory[..directory.LastIndexOf('/')] : null;
                if (parent is not null && parent != "package" && !directories.Contains(parent))
                    throw new InvalidDataException("Missing snapshot directory parent.");
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in item.Files)
            {
                if (file is null || file.Area is not ("package" or "local" or "sync") || !SafePath(file.RelativePath) ||
                    !ValidAreaPath(file.Area + "/" + file.RelativePath, versions) ||
                    !paths.Add(file.Area + "/" + file.RelativePath))
                    throw new InvalidDataException("Invalid extension snapshot file path.");
                var parent = file.RelativePath.Contains('/')
                    ? file.Area + "/" + file.RelativePath[..file.RelativePath.LastIndexOf('/')]
                    : file.Area;
                if (!directories.Contains(parent)) throw new InvalidDataException("Missing snapshot file parent.");
                var expected = prefix + "data/" + item.Id + "/" + file.Area + "/" + file.RelativePath;
                if (file.ArchivePath != expected || Encoding.UTF8.GetByteCount(expected) > 1024 ||
                    !files.Remove(expected, out var descriptor) || descriptor.Role != "extension-file" ||
                    descriptor.Length != file.Length || descriptor.Sha256 != file.Sha256)
                    throw new InvalidDataException("Invalid extension inventory linkage.");
            }

            foreach (var version in versions)
                if (!directories.Contains("package/" + version) ||
                    !paths.Contains("package/" + version + "/manifest.json"))
                    throw new InvalidDataException("Missing extension manifest snapshot.");
            CheckState(item.LocalStorage, "local", directories, item.Files);
            CheckState(item.SyncStorage, "sync", directories, item.Files);
            if (versions.Count == 0 && item.LocalStorage == "absent" && item.SyncStorage == "absent")
                throw new InvalidDataException("Empty extension inventory identity.");
            if (paths.Any(directories.Contains)) throw new InvalidDataException("Snapshot file/directory collision.");
        }

        if (files.Count != 0) throw new InvalidDataException("Unreferenced extension payloads.");
    }

    private static bool ValidAreaPath(string path, HashSet<string> versions)
    {
        var parts = path.Split('/');
        return parts[0] is "local" or "sync" ||
               parts.Length >= 2 && parts[0] == "package" && versions.Contains(parts[1]);
    }

    private static void CheckState(string state, string area, HashSet<string> directories,
        ExtensionSnapshotFile[] files)
    {
        var actual = !directories.Contains(area) ? "absent" :
            files.Any(file => file.Area == area) ? "present" : "empty";
        if (state != actual) throw new InvalidDataException("Invalid extension storage presence state.");
    }

    private static void VerifyHash(ZipArchiveEntry entry, ArchiveFile file, CancellationToken token)
    {
        using var input = entry.Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        try
        {
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                length += read;
                if (length > file.Length) throw new InvalidDataException("Extension file exceeds declared length.");
                hash.AppendData(buffer, 0, read);
            }

            if (length != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                throw new InvalidDataException("Extension payload integrity failure.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate inventory property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                RejectDuplicates(item);
    }
}