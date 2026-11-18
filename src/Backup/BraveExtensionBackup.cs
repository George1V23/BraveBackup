using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Optional host confirmation and controlled process observation; no password is retained here.</summary>
public sealed class ExtensionBackupOptions
{
    public Func<ProcessPresence>? ObserveBrowserState { get; init; }
    public Func<string, Task<bool>>? ConfirmUnencryptedData { get; init; }
}

/// <summary>Strict extension manifest and archive policy; the existing display-only reader stays unchanged.</summary>
public static class BraveExtensionBackup
{
    /// <summary>Reads required manifest metadata without executing extension code or resolving active versions.</summary>
    public static ExtensionSnapshotVersion ReadManifest(ReadOnlyMemory<byte> bytes, string directory)
    {
        if (bytes.Length > 1024 * 1024)
            throw new BackupOperationException("extension-manifest-size", "An extension manifest exceeds 1 MiB.");
        if (bytes.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) bytes = bytes[3..];
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw InvalidManifest();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name))
                throw InvalidManifest();
        if (!root.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(name.GetString()) || name.GetString()!.Length > 32768 ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            !ValidVersion(version.GetString()) || !root.TryGetProperty("manifest_version", out var manifestVersion) ||
            manifestVersion.ValueKind != JsonValueKind.Number || !manifestVersion.TryGetInt32(out var format) ||
            format < 1)
            throw InvalidManifest();
        return new ExtensionSnapshotVersion(directory, version.GetString()!, name.GetString()!);
    }

    /// <summary>Checks exact extension IDs and archived manifest metadata after Core has checked all hashes.</summary>
    public static void ValidateCategory(ZipArchive zip, ArchiveCategory category, CancellationToken token) =>
        ReadValidatedInventory(zip, category, token);

    /// <summary>Returns inventory after checking Brave identities and each archived manifest.</summary>
    internal static ExtensionSnapshotInventory ReadValidatedInventory(ZipArchive zip, ArchiveCategory category,
        CancellationToken token)
    {
        var descriptor = category.Files.Single(file => file.Role == "extension-inventory");
        using var input = zip.GetEntry(descriptor.Path)!.Open();
        var inventory = JsonSerializer.Deserialize<ExtensionSnapshotInventory>(input, ArchiveManifest.JsonOptions)!;
        foreach (var item in inventory.Extensions)
        {
            token.ThrowIfCancellationRequested();
            if (!ValidId(item.Id)) throw new InvalidDataException("Invalid extension ID.");
            foreach (var version in item.Versions)
            {
                var file = item.Files.Single(file =>
                    file.Area == "package" && file.RelativePath == version.Directory + "/manifest.json");
                if (file.Length > 1024 * 1024) throw InvalidManifest();
                var bytes = new byte[checked((int)file.Length)];
                try
                {
                    using var manifest = zip.GetEntry(file.ArchivePath)!.Open();
                    manifest.ReadExactly(bytes);
                    if (ReadManifest(bytes, version.Directory) != version) throw InvalidManifest();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }
        return inventory;
    }

    internal static bool ValidId(string id) => id.Length == 32 && id.All(character => character is >= 'a' and <= 'p');

    internal static bool ValidVersion(string? value)
    {
        if (value is null) return false;
        var parts = value.Split('.');
        return parts.Length is >= 1 and <= 4 && parts.All(part => part.Length > 0 &&
                                                                  (part.Length == 1 || part[0] != '0') &&
                                                                  part.All(char.IsAsciiDigit) &&
                                                                  ushort.TryParse(part, out _)) &&
               parts.Any(part => part != "0");
    }

    private static BackupOperationException InvalidManifest() => new("extension-manifest-invalid",
        "An extension manifest is missing, ambiguous or invalid. No extension data was omitted.");
}

/// <summary>Captures approved raw profile subtrees and rechecks their shape, hashes and stopped-browser state.</summary>
internal sealed class ExtensionCaptureSession
{
    private static readonly string[] Roots = { "Extensions", "Local Extension Settings", "Sync Extension Settings" };
    private readonly Func<ProcessPresence> _observe;
    private readonly CancellationToken _token;

    private readonly List<(string Profile, Dictionary<string, Stamp> Tree, Dictionary<string, string> Hashes)>
        _captured = new();

    private long _lastProbe;
    private int _sourcePathCount;
    public long RawBytes { get; private set; }
    public int FileCount { get; private set; }
    public int PackageCount { get; private set; }
    public int VersionCount { get; private set; }
    public int StorageOnlyCount { get; private set; }
    public int AbsentAreas { get; private set; }
    public int EmptyAreas { get; private set; }
    public int PresentAreas { get; private set; }

    public ExtensionCaptureSession(IReadOnlyList<BraveBackupSelection> scope, ExtensionBackupOptions options,
        CancellationToken token)
    {
        _token = token;
        var prefixes = new[] { "brave" }
            .Concat(scope.Select(item => Path.GetFileNameWithoutExtension(item.Installation.ProgramPath)))
            .Where(value => !string.IsNullOrEmpty(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _observe = options.ObserveBrowserState ?? (() => ProcessPresenceProbe.Observe(prefixes, token));
    }

    public void EnsureStopped(bool force = false)
    {
        _token.ThrowIfCancellationRequested();
        var now = Environment.TickCount64;
        if (!force && now - _lastProbe < 250) return;
        _lastProbe = now;
        ProcessPresence observed;
        try { observed = _observe(); }
        catch (OperationCanceledException) { throw; }
        catch { observed = ProcessPresence.Unknown; }
        switch (observed)
        {
            case ProcessPresence.Running:
                throw new BackupOperationException("extensions-browser-running",
                    "Close all Brave instances before backing up Extensions. The app will not close them for you.");
            case ProcessPresence.Unknown:
                throw new BackupOperationException("extensions-browser-unknown",
                    "The app could not establish that Brave is closed. Extensions backup was stopped; close Brave and try again.");
            case ProcessPresence.Stopped: return;
            default:
                throw new BackupOperationException("extensions-browser-unknown",
                    "Browser process state is unavailable.");
        }
    }

    public ArchiveCategory Capture(string profile, string prefix, ZipArchive zip, string compression,
        int availableEntries)
    {
        EnsureStopped(true);
        var tree = Scan(profile);
        _sourcePathCount += tree.Count;
        if (_sourcePathCount > ArchiveLimits.MaxEntries)
            throw Failure("extension-tree-limit", "Extension snapshot metadata exceeds the global path limit.");
        long manifestTextBytes = 0;
        var ids = tree.Keys.Select(path => path.Split('/')).Where(parts => parts.Length >= 2).Select(parts => parts[1])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length > 10000) throw Failure("extension-count-limit", "Too many extension identities in one profile.");
        foreach (var pair in tree)
        {
            var parts = pair.Key.Split('/');
            if (parts.Length >= 2 && !BraveExtensionBackup.ValidId(parts[1]))
                throw Failure("extension-layout", "An extension directory has an unsupported identity.");
            if (pair.Value.Kind == SnapshotNodeKind.RegularFile && (parts.Length < (parts[0] == "Extensions" ? 4 : 3)))
                throw Failure("extension-layout", "An extension source has an unsupported directory layout.");
        }

        if (tree.Values.Count(stamp => stamp.Kind == SnapshotNodeKind.RegularFile) + 1 > availableEntries)
            throw Failure("entry-limit", "Too many files for one archive.");
        var inventoryItems = new List<ExtensionSnapshotItem>();
        var descriptors = new List<ArchiveFile>();
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var versions = new List<ExtensionSnapshotVersion>();
            var directories = new List<string>();
            var files = new List<ExtensionSnapshotFile>();
            foreach (var pair in tree.Where(pair =>
                             pair.Key.StartsWith("Extensions/" + id + "/", StringComparison.Ordinal) &&
                             pair.Key.Split('/').Length == 3 && pair.Value.Kind == SnapshotNodeKind.Directory)
                         .OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var versionDirectory = pair.Key.Split('/')[2];
                var manifestKey = pair.Key + "/manifest.json";
                if (!tree.TryGetValue(manifestKey, out var manifestStamp) ||
                    manifestStamp.Kind != SnapshotNodeKind.RegularFile || manifestStamp.Length > 1024 * 1024)
                    throw Failure("extension-manifest-invalid",
                        "An extension version has no supported regular manifest file.");
                using var input = Open(profile, manifestKey);
                var bytes = new byte[checked((int)manifestStamp.Length)];
                try
                {
                    input.ReadExactly(bytes);
                    versions.Add(BraveExtensionBackup.ReadManifest(bytes, versionDirectory));
                    manifestTextBytes += System.Text.Encoding.UTF8.GetByteCount(versions[^1].Name) +
                                         versions[^1].Directory.Length + versions[^1].Version.Length;
                    if (manifestTextBytes > ArchiveLimits.MaxManifestBytes)
                        throw Failure("extension-inventory-size", "Extension inventory text exceeds 16 MiB.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            if (tree.ContainsKey("Extensions/" + id) && versions.Count == 0)
                throw Failure("extension-layout", "A discovered extension has no version directories.");
            foreach (var (root, area) in new[] { (Roots[0], "package"), (Roots[1], "local"), (Roots[2], "sync") })
            {
                var basePath = root + "/" + id;
                foreach (var pair in tree
                             .Where(pair =>
                                 pair.Key == basePath || pair.Key.StartsWith(basePath + "/", StringComparison.Ordinal))
                             .OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var relative = pair.Key == basePath ? "" : pair.Key[(basePath.Length + 1)..];
                    if (area == "package" && relative.Length == 0) continue;
                    var areaPath = relative.Length == 0 ? area : area + "/" + relative;
                    if (pair.Value.Kind == SnapshotNodeKind.Directory) directories.Add(areaPath);
                    else
                    {
                        EnsureStopped();
                        var archivePath = prefix + "data/" + id + "/" + areaPath;
                        if (System.Text.Encoding.UTF8.GetByteCount(archivePath) > 1024)
                            throw Failure("extension-path", "An extension path exceeds the archive limit.");
                        var descriptor = CopyFile(profile, pair.Key, pair.Value, zip, archivePath, compression);
                        descriptors.Add(descriptor);
                        hashes.Add(pair.Key, descriptor.Sha256);
                        files.Add(new ExtensionSnapshotFile(area, relative, archivePath, descriptor.Length,
                            descriptor.Sha256));
                        RawBytes += descriptor.Length;
                        FileCount++;
                        if (RawBytes > ArchiveLimits.MaxBytes - ArchiveLimits.MaxManifestBytes)
                            throw Failure("size-limit", "Extension data exceeds the archive size limit.");
                    }
                }
            }

            string State(string area) => !directories.Contains(area) ? "absent" :
                files.Any(file => file.Area == area) ? "present" : "empty";

            var local = State("local");
            var sync = State("sync");
            foreach (var state in new[] { local, sync })
            {
                if (state == "absent") AbsentAreas++;
                else if (state == "empty") EmptyAreas++;
                else PresentAreas++;
            }

            if (versions.Count > 0) PackageCount++;
            else StorageOnlyCount++;
            VersionCount += versions.Count;
            inventoryItems.Add(new ExtensionSnapshotItem(id, versions.ToArray(), local, sync, directories.ToArray(),
                files.ToArray()));
        }

        var inventory = new ExtensionSnapshotInventory(1, inventoryItems.ToArray());
        ExtensionSnapshotFormat.ValidateInventory(inventory, prefix,
            descriptors.ToDictionary(file => file.Path, StringComparer.Ordinal));
        var metadata = JsonSerializer.SerializeToUtf8Bytes(inventory, ArchiveManifest.JsonOptions);
        try
        {
            if (metadata.Length > ArchiveLimits.MaxManifestBytes)
                throw Failure("extension-inventory-size", "Extension inventory exceeds 16 MiB.");
            descriptors.Insert(0,
                BackupArchive.WriteFile(zip, prefix + "inventory-v1.json", "extension-inventory", metadata, compression,
                    _token));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadata);
        }

        _captured.Add((profile, tree, hashes));
        return new ArchiveCategory("extensions", 1, descriptors.ToArray());
    }

    public void VerifySources()
    {
        EnsureStopped(true);
        foreach (var capture in _captured)
        {
            Match(capture.Tree, Scan(capture.Profile));
            foreach (var pair in capture.Hashes)
            {
                EnsureStopped();
                using var input = Open(capture.Profile, pair.Key);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[65536];
                try
                {
                    int read;
                    long length = 0;
                    while ((read = input.Read(buffer)) != 0)
                    {
                        EnsureStopped();
                        length += read;
                        if (length > capture.Tree[pair.Key].Length)
                            throw Failure("extension-source-changed", "Extension source grew during verification.");
                        hash.AppendData(buffer, 0, read);
                    }

                    if (Convert.ToHexStringLower(hash.GetHashAndReset()) != pair.Value)
                        throw Failure("extension-source-changed",
                            "Extension data changed during capture. No archive was published.");
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(buffer);
                }
            }

            Match(capture.Tree, Scan(capture.Profile));
        }

        EnsureStopped(true);
    }

    private ArchiveFile CopyFile(string profile, string relative, Stamp stamp, ZipArchive zip, string path,
        string compression)
    {
        using var input = Open(profile, relative);
        if (input.Length != stamp.Length)
            throw Failure("extension-source-changed", "Extension source changed during capture.");
        using var output = zip.CreateEntry(path, BackupArchive.Compression(compression)).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        try
        {
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                EnsureStopped();
                length += count;
                if (length > stamp.Length)
                    throw Failure("extension-source-changed", "Extension source grew during capture.");
                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);
            }

            if (length != stamp.Length ||
                StampFor(Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar))) != stamp)
                throw Failure("extension-source-changed", "Extension source changed during capture.");
            return new ArchiveFile(path, "extension-file", length, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private Dictionary<string, Stamp> Scan(string profile)
    {
        EnsureSafeAncestors(profile);
        if (SnapshotFileAccess.Kind(profile) != SnapshotNodeKind.Directory)
            throw Failure("extension-profile-missing", "The selected profile directory is unavailable.");
        var result = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(Roots.Reverse());
        while (pending.TryPop(out var relative))
        {
            EnsureStopped();
            if (!ExtensionSnapshotFormat.SafePath(relative) || relative.Split('/').Length > 64)
                throw Failure("extension-path", "An extension path is unsafe or too deep.");
            var full = Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
            var stamp = StampFor(full);
            if (!result.TryAdd(relative, stamp) || result.Count > ArchiveLimits.MaxEntries)
                throw Failure("extension-tree-limit", "Extension source has too many paths or case-colliding paths.");
            if (stamp.Kind == SnapshotNodeKind.Directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(full))
                    pending.Push(relative + "/" + Path.GetFileName(child));
            else if (stamp.Kind == SnapshotNodeKind.Unsafe ||
                     stamp.Kind == SnapshotNodeKind.Missing && !Roots.Contains(relative))
                throw Failure("extension-source-unsafe",
                    "An extension source is a link, special file or disappeared during capture.");
            else if (stamp.Length > ArchiveLimits.MaxBookmarkBytes)
                throw Failure("extension-file-size", "An extension file exceeds 256 MiB.");
        }

        return result;
    }

    private static Stamp StampFor(string path)
    {
        var kind = SnapshotFileAccess.Kind(path);
        return kind switch
        {
            SnapshotNodeKind.RegularFile => new Stamp(kind, new FileInfo(path).Length,
                File.GetLastWriteTimeUtc(path).Ticks),
            SnapshotNodeKind.Directory => new Stamp(kind, 0, Directory.GetLastWriteTimeUtc(path).Ticks),
            _ => new Stamp(kind, 0, 0)
        };
    }

    private static FileStream Open(string profile, string relative)
    {
        var path = Path.Combine(profile, relative.Replace('/', Path.DirectorySeparatorChar));
        EnsureSafeAncestors(Path.GetDirectoryName(path)!);
        return SnapshotFileAccess.OpenRead(path);
    }

    private static void EnsureSafeAncestors(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (SnapshotFileAccess.Kind(current) != SnapshotNodeKind.Directory)
                throw Failure("extension-source-unsafe", "Extension capture must not traverse links or special files.");
    }

    private static void Match(Dictionary<string, Stamp> expected, Dictionary<string, Stamp> actual)
    {
        if (expected.Count != actual.Count ||
            expected.Any(pair => !actual.TryGetValue(pair.Key, out var stamp) || stamp != pair.Value))
            throw Failure("extension-source-changed",
                "Extension files or directories changed during capture. No archive was published.");
    }

    private sealed record Stamp(SnapshotNodeKind Kind, long Length, long ModifiedTicks);

    private static BackupOperationException Failure(string code, string message) => new(code, message);
}