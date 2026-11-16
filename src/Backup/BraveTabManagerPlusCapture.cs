using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>
/// Captures a supported installed extension's saved windows without opening its database.
/// Retains source stamps/hashes for a required final VerifyUnchanged before publication.
/// This component does not create archives, restore data, close Brave or write any file.
/// </summary>
public sealed class BraveTabManagerPlusCapture : IDisposable
{
    private readonly string _profile;
    private readonly Func<ProcessPresence> _observe;
    private readonly CancellationToken _token;
    private Dictionary<string, Stamp> _tree = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _hashes = new(StringComparer.Ordinal);
    private SavedWindowCollection? _data;
    private bool _disposed;
    private long _lastProbe;

    private BraveTabManagerPlusCapture(string profile, Func<ProcessPresence>? observe, CancellationToken token)
    {
        _profile = Path.GetFullPath(profile);
        _token = token;
        _observe = observe ?? (() => ProcessPresenceProbe.Observe(new[] { "brave" }, token));
    }

    public SavedWindowCollection Data => !_disposed && _data is not null
        ? _data
        : throw new ObjectDisposedException(nameof(BraveTabManagerPlusCapture));

    public DateTimeOffset CapturedUtc { get; private set; }
    public int SourceFileCount => _hashes.Count;

    /// <summary>
    /// Reads regular files into bounded memory, checks installation/storage versions and
    /// parses saved windows. An optional host process observer supports custom executable
    /// names and controlled tests; running, unknown and failed observations stop capture.
    /// </summary>
    public static BraveTabManagerPlusCapture Read(string profile,
        Func<ProcessPresence>? observeBrowserState = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BraveTabManagerPlusCapture? result = null;
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);
        byte[]? sessions = null;
        byte[]? storedVersion = null;
        try
        {
            result = new BraveTabManagerPlusCapture(profile, observeBrowserState, cancellationToken);
            result.EnsureStopped(true);
            result._tree = result.Scan();
            foreach (var entry in result._tree.Where(entry => entry.Value.Kind == SnapshotNodeKind.RegularFile))
            {
                result.EnsureStopped();
                var bytes = result.ReadFile(entry.Key, entry.Value);
                try
                {
                    result._hashes.Add(entry.Key, SHA256.HashData(bytes));
                    if (entry.Key.StartsWith("Extensions/", StringComparison.Ordinal))
                        ValidateManifest(bytes);
                    else
                    {
                        files.Add(Path.GetFileName(entry.Key), bytes);
                        bytes = null;
                    }
                }
                finally
                {
                    if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                }
            }

            storedVersion = OfflineLevelDb.ReadValue(files, "version", cancellationToken);
            if (storedVersion is null) throw Unsupported();
            using (var version = JsonDocument.Parse(storedVersion))
                if (version.RootElement.ValueKind != JsonValueKind.String ||
                    version.RootElement.GetString() != SavedWindows.SupportedVersion)
                    throw Unsupported();
            sessions = OfflineLevelDb.ReadValue(files, "sessions", cancellationToken);
            if (sessions is null)
                throw Failure("tabmanagerplus-data-unavailable",
                    "The saved-window collection is missing or deleted; it was not reported as empty.");
            result._data = SavedWindows.Parse(sessions, SavedWindows.SupportedVersion, cancellationToken);
            result.VerifyUnchanged();
            result.CapturedUtc = DateTimeOffset.UtcNow;
            return result;
        }
        catch (OperationCanceledException)
        {
            result?.Dispose();
            throw;
        }
        catch (BackupOperationException)
        {
            result?.Dispose();
            throw;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException
                                          or InvalidOperationException or ArgumentException
                                          or UnauthorizedAccessException)
        {
            result?.Dispose();
            throw Failure("tabmanagerplus-source-invalid",
                "Tab Manager Plus storage is unreadable, unsupported or invalid. No saved windows were omitted or repaired.");
        }
        finally
        {
            if (sessions is not null) CryptographicOperations.ZeroMemory(sessions);
            if (storedVersion is not null) CryptographicOperations.ZeroMemory(storedVersion);
            foreach (var bytes in files.Values)
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(bytes, out var array))
                    CryptographicOperations.ZeroMemory(array.AsSpan());
        }
    }

    /// <summary>Rechecks stopped state, source shape/stamps and every captured hash; must run before archive publication.</summary>
    public void VerifyUnchanged()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            EnsureStopped(true);
            Match(_tree, Scan());
            foreach (var entry in _hashes)
            {
                EnsureStopped();
                var bytes = ReadFile(entry.Key, _tree[entry.Key]);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), entry.Value)) throw Changed();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }

            Match(_tree, Scan());
            EnsureStopped(true);
        }
        catch (BackupOperationException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Changed();
        }
    }

    /// <summary>Releases validation metadata and references to decoded browsing data; managed strings are not securely erased.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _data = null;
        _tree.Clear();
        foreach (var hash in _hashes.Values) CryptographicOperations.ZeroMemory(hash);
        _hashes.Clear();
    }

    private void EnsureStopped(bool force = false)
    {
        _token.ThrowIfCancellationRequested();
        var now = Environment.TickCount64;
        if (!force && now - _lastProbe < 250) return;
        _lastProbe = now;
        ProcessPresence state;
        try
        {
            state = _observe();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            state = ProcessPresence.Unknown;
        }

        if (state != ProcessPresence.Stopped)
            throw Failure("tabmanagerplus-browser-not-stopped",
                "Close all Brave instances before capturing Tab Manager Plus. Capture requires an established stopped state; the app will not close Brave.");
    }

    private Dictionary<string, Stamp> Scan()
    {
        var tree = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        SafeAncestors(_profile);
        var packages = "Extensions/" + SavedWindows.OfficialExtensionId;
        AddDirectory(packages);
        var versions = 0;
        foreach (var directory in Directory.EnumerateFileSystemEntries(Full(packages)))
        {
            _token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (++versions > 16 || !VersionDirectory(name)) throw Unsupported();
            AddDirectory(packages + "/" + name);
            AddFile(packages + "/" + name + "/manifest.json");
        }

        if (versions == 0) throw Unsupported();
        var storage = "Local Extension Settings/" + SavedWindows.OfficialExtensionId;
        AddDirectory(storage);
        foreach (var file in Directory.EnumerateFileSystemEntries(Full(storage)))
        {
            _token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (name.Length is 0 or > 128 || name.Contains('\\')) throw Unsafe();
            AddFile(storage + "/" + name);
        }

        return tree;

        void AddDirectory(string relative)
        {
            var full = Full(relative);
            SafeAncestors(full);
            tree.Add(relative, new Stamp(SnapshotNodeKind.Directory, 0, Directory.GetLastWriteTimeUtc(full).Ticks));
        }

        void AddFile(string relative)
        {
            if (tree.Count >= 4096) throw Limit();
            var stamp = Inspect(Full(relative));
            if (relative.StartsWith("Extensions/", StringComparison.Ordinal) && stamp.Length > 1024 * 1024)
                throw Unsupported();
            tree.Add(relative, stamp);
            // Includes manifests and unused database files, bounding capture before allocation.
            if (tree.Values.Sum(item => item.Length) > OfflineLevelDb.MaximumSnapshotBytes) throw Limit();
        }
    }

    private byte[] ReadFile(string relative, Stamp expected)
    {
        _token.ThrowIfCancellationRequested();
        var path = Full(relative);
        SafeAncestors(Path.GetDirectoryName(path)!);
        if (Inspect(path) != expected) throw Changed();
        using var input = SnapshotFileAccess.OpenRead(path);
        if (input.Length != expected.Length) throw Changed();
        var bytes = new byte[checked((int)expected.Length)];
        try
        {
            for (var offset = 0; offset < bytes.Length;)
            {
                _token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw Changed();
                offset += count;
            }

            if (input.ReadByte() != -1 || Inspect(path) != expected) throw Changed();
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private string Full(string relative) => Path.Combine(_profile, relative.Replace('/', Path.DirectorySeparatorChar));

    private static Stamp Inspect(string path)
    {
        if (SnapshotFileAccess.Kind(path) != SnapshotNodeKind.RegularFile) throw Unsafe();
        var info = new FileInfo(path);
        if (info.Length > OfflineLevelDb.MaximumSnapshotBytes) throw Limit();
        return new Stamp(SnapshotNodeKind.RegularFile, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static void SafeAncestors(string path)
    {
        for (var parent = Path.GetFullPath(path); parent is not null; parent = Path.GetDirectoryName(parent))
            if (SnapshotFileAccess.Kind(parent) != SnapshotNodeKind.Directory)
                throw Unsafe();
    }

    private static bool VersionDirectory(string name)
    {
        if (name == SavedWindows.SupportedVersion) return true;
        var prefix = SavedWindows.SupportedVersion + "_";
        return name.StartsWith(prefix, StringComparison.Ordinal) && name.Length <= 32 &&
               uint.TryParse(name[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static void ValidateManifest(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > 1024 * 1024) throw Unsupported();
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() !=
            root.EnumerateObject().Count() ||
            !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != SavedWindows.SupportedVersion ||
            !root.TryGetProperty("manifest_version", out var format) || format.ValueKind != JsonValueKind.Number ||
            !format.TryGetInt32(out var number) || number != 3)
            throw Unsupported();
    }

    private static void Match(Dictionary<string, Stamp> expected, Dictionary<string, Stamp> actual)
    {
        if (expected.Count != actual.Count || expected.Any(pair =>
                !actual.TryGetValue(pair.Key, out var stamp) || stamp != pair.Value)) throw Changed();
    }

    private sealed record Stamp(SnapshotNodeKind Kind, long Length, long ModifiedTicks);

    private static BackupOperationException Failure(string code, string message) => new(code, message);

    private static BackupOperationException Changed() => Failure("tabmanagerplus-source-changed",
        "Tab Manager Plus source data changed during capture. Retry with Brave closed; no stale snapshot was accepted.");

    private static BackupOperationException Unsupported() => Failure("tabmanagerplus-version-unsupported",
        "Tab Manager Plus requires an unambiguous researched 7.0.0 installation and matching stored version. No legacy fallback was used.");

    private static BackupOperationException Unsafe() => Failure("tabmanagerplus-source-unsafe",
        "Tab Manager Plus capture requires existing ordinary directories and regular files, without links or special files.");

    private static BackupOperationException Limit() => Failure("tabmanagerplus-source-limit",
        "Tab Manager Plus capture exceeds the supported file-count or size limits.");
}