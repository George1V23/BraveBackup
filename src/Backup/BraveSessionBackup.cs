using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Frozen native-session inclusion and explicit per-save consent supplied by the host.</summary>
public sealed class SessionBackupOptions
{
    public bool IncludeNativeFiles { get; init; }
    public Func<string, Task<bool>>? ConfirmUnencryptedNativeFiles { get; init; }
}

/// <summary>Enforces supported source names, format and optional native/portable replay consistency.</summary>
public static class BraveSessionBackup
{
    internal static readonly string[] Legacy = { "Current Session", "Current Tabs", "Last Session", "Last Tabs" };

    internal static bool ModernName(string name, bool sessionOnly = false)
    {
        var suffix = name.StartsWith("Session_", StringComparison.Ordinal) ? name[8..] :
            !sessionOnly && name.StartsWith("Tabs_", StringComparison.Ordinal) ? name[5..] : "";
        return suffix.Length is >= 1 and <= 19 && suffix.All(char.IsAsciiDigit) &&
               long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 &&
               suffix == number.ToString(CultureInfo.InvariantCulture);
    }

    internal static bool NativeName(string path) => Legacy.Contains(path, StringComparer.Ordinal) ||
                                                    path.StartsWith("Sessions/", StringComparison.Ordinal) &&
                                                    ModernName(path[9..]);

    /// <summary>Validates browser-specific provenance and, when present, replays the exact archived native source.</summary>
    public static void ValidateCategory(ZipArchive zip, ArchiveCategory category, CancellationToken token)
    {
        var descriptor = category.Files.Single(file => file.Role == "portable-session");
        using var document = SessionArchiveFormat.ReadJson(zip, descriptor, token);
        var model = PortableSessionFormat.Parse(document.RootElement);
        if (!model.Source.Path.StartsWith("Sessions/", StringComparison.Ordinal) ||
            !ModernName(model.Source.Path[9..], true) || model.Source.FormatVersion != 3)
            throw new InvalidDataException("Unsupported native session provenance.");
        if (!model.IncludeNativeFiles) return;
        var metadata = category.Files.Single(file => file.Role == "native-session-inventory");
        using var inventoryDocument = SessionArchiveFormat.ReadJson(zip, metadata, token);
        var inventory = inventoryDocument.Deserialize<NativeSessionInventory>(ArchiveManifest.JsonOptions)!;
        if (inventory.Files.Any(file => !NativeName(file.RelativePath)) ||
            inventory.AbsentLegacyFiles.Any(name => !Legacy.Contains(name, StringComparer.Ordinal)) ||
            !inventory.AbsentLegacyFiles.Concat(inventory.Files.Where(file => Legacy.Contains(file.RelativePath))
                    .Select(file => file.RelativePath))
                .ToHashSet(StringComparer.Ordinal).SetEquals(Legacy))
            throw new InvalidDataException("Invalid supported native session scope.");
        var source = inventory.Files.Single(file => file.RelativePath == model.Source.Path);
        var bytes = new byte[checked((int)source.Length)];
        try
        {
            using var input = zip.GetEntry(source.ArchivePath)!.Open();
            input.ReadExactly(bytes);
            var replay = BraveSessionDecoder.Decode(bytes, source.RelativePath, model.CapturedUtc, true, token);
            if (replay.ActiveWindowId != model.ActiveWindowId || replay.Windows.Length != model.Windows.Length ||
                replay.Windows.Where((window, index) =>
                    window.Id != model.Windows[index].Id || window.Type != model.Windows[index].Type ||
                    window.SelectedTabIndex != model.Windows[index].SelectedTabIndex ||
                    !window.Tabs.SequenceEqual(model.Windows[index].Tabs) ||
                    !window.Groups.SequenceEqual(model.Windows[index].Groups)).Any())
                throw new InvalidDataException("Native/portable session replay mismatch.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>Read-only stable persisted session capture with optional raw family snapshots and final source checks.</summary>
internal sealed class SessionCaptureSession(bool includeNative, CancellationToken token)
{
    private readonly List<(string Profile, Dictionary<string, Stamp> Files, Dictionary<string, string> Hashes)>
        _sources = new();

    public long Bytes { get; private set; }
    public int WindowCount { get; private set; }
    public int TabCount { get; private set; }
    public int GroupCount { get; private set; }
    public int NativeCount { get; private set; }
    public int MissingGroupMetadata { get; private set; }
    public int Profiles { get; private set; }

    public ArchiveCategory Capture(string profile, string prefix, ZipArchive zip, string compression,
        int availableEntries)
    {
        byte[]? selected = null;
        Dictionary<string, Stamp>? snapshot = null;
        PortableSession? model = null;
        string source = "";
        for (var attempt = 0;; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                snapshot = Discover(profile);
                var candidates = snapshot.Keys
                    .Where(name => name.StartsWith("Sessions/Session_", StringComparison.Ordinal))
                    .OrderByDescending(name => long.Parse(name[17..], CultureInfo.InvariantCulture)).ToArray();
                if (candidates.Length == 0)
                    throw Failure("session-unavailable",
                        "No supported modern persisted Session file is available. Legacy/raw-only data cannot replace the requested portable session.");
                source = candidates[0];
                selected = Read(profile, source, snapshot[source]);
                model = BraveSessionDecoder.Decode(selected, source, DateTimeOffset.UtcNow, includeNative, token);
                Match(snapshot, Discover(profile));
                break;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                if (selected != null) CryptographicOperations.ZeroMemory(selected);
                selected = null;
                if (error is BackupOperationException known &&
                    known.Code is not ("session-malformed" or "session-marker-missing" or "session-source-changed"))
                    throw;
                if (attempt >= 2)
                    throw error is BackupOperationException
                        ? error
                        : Failure("session-source-unreadable",
                            "A stable persisted session could not be read. Retry after Brave writes its session; the app will not close it.");
            }
        }

        try
        {
            var expectedFiles = includeNative ? snapshot!.Count : 0;
            if (1 + (includeNative ? expectedFiles + 1 : 0) > availableEntries)
                throw Failure("entry-limit", "Too many native session files for one archive.");
            var descriptors = new List<ArchiveFile>();
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var portable = PortableSessionFormat.SerializeBounded(model!, PortableSessionFormat.MaxPortableBytes);
            try
            {
                descriptors.Add(BackupArchive.WriteFile(zip, prefix + "portable-v1.json", "portable-session", portable,
                    compression, token));
                Bytes += portable.Length;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(portable);
            }

            hashes.Add(source, model!.Source.Sha256);
            if (includeNative)
            {
                var files = new List<NativeSessionFile>();
                foreach (var pair in snapshot!)
                {
                    token.ThrowIfCancellationRequested();
                    var path = prefix + "native/" + pair.Key;
                    ArchiveFile file;
                    if (pair.Key == source)
                        file = BackupArchive.WriteFile(zip, path, "native-session-file", selected!, compression, token);
                    else file = Copy(profile, pair.Key, pair.Value, zip, path, compression);
                    descriptors.Add(file);
                    hashes[pair.Key] = file.Sha256;
                    Bytes += file.Length;
                    NativeCount++;
                    files.Add(new NativeSessionFile(pair.Key, path, file.Length, file.Sha256));
                }

                var inventory = new NativeSessionInventory(1,
                    BraveSessionBackup.Legacy.Where(name => !snapshot!.ContainsKey(name)).ToArray(), files.ToArray());
                var metadata = PortableSessionFormat.SerializeBounded(inventory, ArchiveLimits.MaxManifestBytes);
                try
                {
                    descriptors.Add(BackupArchive.WriteFile(zip, prefix + "native-inventory-v1.json",
                        "native-session-inventory", metadata, compression, token));
                    Bytes += metadata.Length;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(metadata);
                }
            }

            if (Bytes > ArchiveLimits.MaxBytes - ArchiveLimits.MaxManifestBytes)
                throw Failure("session-limit", "Session data exceeds archive size limits.");
            _sources.Add((profile, snapshot!, hashes));
            Profiles++;
            WindowCount += model.Windows.Length;
            TabCount += model.Windows.Sum(window => window.Tabs.Length);
            GroupCount += model.Windows.Sum(window => window.Groups.Length);
            MissingGroupMetadata += model.Windows.Sum(window => window.Groups.Count(group => group.Title is null));
            return new ArchiveCategory("sessions", 1, descriptors.ToArray());
        }
        finally
        {
            if (selected != null) CryptographicOperations.ZeroMemory(selected);
        }
    }

    public void VerifySources()
    {
        foreach (var capture in _sources)
        {
            Match(capture.Files, Discover(capture.Profile));
            foreach (var file in capture.Hashes)
            {
                var bytes = Read(capture.Profile, file.Key, capture.Files[file.Key]);
                try
                {
                    if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Value) throw Changed();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            Match(capture.Files, Discover(capture.Profile));
        }
    }

    private Dictionary<string, Stamp> Discover(string profile)
    {
        SafeAncestors(profile);
        var result = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        foreach (var root in new[] { "Sessions", "Sessions_Encrypted" })
        {
            var full = Path.Combine(profile, root);
            var kind = SnapshotFileAccess.Kind(full);
            if (kind == SnapshotNodeKind.Missing) continue;
            if (kind != SnapshotNodeKind.Directory)
                throw Failure("session-source-unsafe", "Session capture rejects linked or non-directory source roots.");
            foreach (var path in Directory.EnumerateFileSystemEntries(full))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(path);
                if (!name.StartsWith("Session_", StringComparison.Ordinal) &&
                    !name.StartsWith("Tabs_", StringComparison.Ordinal)) continue;
                if (!BraveSessionBackup.ModernName(name))
                    throw Failure("session-source-name", "A session-family filename is malformed or ambiguous.");
                if (root == "Sessions_Encrypted")
                    throw Failure("session-encrypted-source",
                        "Encrypted session storage is present. Its authoritative relationship to plaintext cannot be established; no stale plaintext fallback was used.");
                if (!includeNative && !name.StartsWith("Session_", StringComparison.Ordinal)) continue;
                var stamp = Inspect(path);
                result.Add(root + "/" + name, stamp);
                if (result.Count > 1024)
                    throw Failure("session-limit", "Too many session-family files in one profile.");
            }
        }

        if (includeNative)
            foreach (var name in BraveSessionBackup.Legacy)
            {
                var path = Path.Combine(profile, name);
                if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.Missing) result.Add(name, Inspect(path));
            }

        if (result.Count > 1024) throw Failure("session-limit", "Too many native session files in one profile.");
        return result;
    }

    private byte[] Read(string profile, string name, Stamp stamp)
    {
        var path = Path.Combine(profile, name.Replace('/', Path.DirectorySeparatorChar));
        SafeAncestors(Path.GetDirectoryName(path)!);
        using var source = SnapshotFileAccess.OpenRead(path);
        if (source.Length != stamp.Length) throw Changed();
        var bytes = new byte[checked((int)stamp.Length)];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = source.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw Changed();
                offset += count;
            }

            if (source.ReadByte() != -1 || Inspect(path) != stamp) throw Changed();
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private ArchiveFile Copy(string profile, string name, Stamp stamp, ZipArchive zip, string path, string compression)
    {
        var full = Path.Combine(profile, name.Replace('/', Path.DirectorySeparatorChar));
        SafeAncestors(Path.GetDirectoryName(full)!);
        using var input = SnapshotFileAccess.OpenRead(full);
        using var output = zip.CreateEntry(path, BackupArchive.Compression(compression)).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long length = 0;
        try
        {
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                length += count;
                if (length > stamp.Length) throw Changed();
                hash.AppendData(buffer, 0, count);
                output.Write(buffer, 0, count);
            }

            if (length != stamp.Length || Inspect(full) != stamp) throw Changed();
            return new ArchiveFile(path, "native-session-file", length,
                Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static Stamp Inspect(string path)
    {
        if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.RegularFile)
            throw Failure("session-source-unsafe", "Session capture requires regular files without links.");
        var file = new FileInfo(path);
        if (file.Length > ArchiveLimits.MaxBookmarkBytes)
            throw Failure("session-limit", "A session file exceeds 256 MiB.");
        return new Stamp(file.Length, file.LastWriteTimeUtc.Ticks);
    }

    private static void SafeAncestors(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory)
                throw Failure("session-source-unsafe", "Session capture must not traverse links or special files.");
    }

    private static void Match(Dictionary<string, Stamp> expected, Dictionary<string, Stamp> actual)
    {
        if (expected.Count != actual.Count ||
            expected.Any(pair => !actual.TryGetValue(pair.Key, out var stamp) || stamp != pair.Value)) throw Changed();
    }

    private sealed record Stamp(long Length, long ModifiedTicks);

    private static BackupOperationException Changed() => Failure("session-source-changed",
        "Session data changed during capture. No partial archive was published; retry after persistence settles.");

    private static BackupOperationException Failure(string code, string message) => new(code, message);
}