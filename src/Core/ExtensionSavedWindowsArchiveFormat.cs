using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Browser-independent container integrity and generic saved-window envelope validation.</summary>
public static class ExtensionSavedWindowsArchiveFormat
{
    public const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>Checks the single declared portable payload, its hash and bounded envelope without extraction.</summary>
    public static void ValidateCategory(ZipArchive zip, Dictionary<string, ZipArchiveEntry> remaining,
        string prefix, ArchiveCategory category, CancellationToken token)
    {
        if (category.Files.Length != 1) throw Invalid();
        var file = category.Files[0];
        if (file is null || file.Path != prefix + "saved-windows-v1.json" || file.Role != "portable-extension-windows" ||
            file.Length is < 1 or > MaximumBytes || !PortableSessionFormat.ValidHash(file.Sha256) ||
            !remaining.Remove(file.Path, out var entry) || entry.Length != file.Length) throw Invalid();
        using (var input = entry.Open())
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[65536];
            long length = 0;
            try
            {
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    length += count;
                    if (length > file.Length) throw Invalid();
                    hash.AppendData(buffer, 0, count);
                }
                if (length != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256) throw Invalid();
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        using var document = SessionArchiveFormat.ReadJson(zip, file, token);
        ValidatePayload(document.RootElement, token);
    }

    /// <summary>Checks generic metadata, collection identities and resource ceilings; extension semantics belong in Backup.</summary>
    public static void ValidatePayload(JsonElement root, CancellationToken token)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Object || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("captureKind").GetString() != "extension-saved-windows" ||
                root.GetProperty("source").GetString() != "local-extension-settings") throw Invalid();
            var date = root.GetProperty("capturedUtc").GetDateTimeOffset();
            var id = root.GetProperty("extensionId").GetString();
            var version = root.GetProperty("extensionVersion").GetString();
            var count = root.GetProperty("sourceFileCount").GetInt32();
            var windows = root.GetProperty("windows");
            if (date == default || date.Offset != TimeSpan.Zero || id is null || id.Length != 32 ||
                id.Any(c => c is < 'a' or > 'p') || string.IsNullOrEmpty(version) || version.Length > 64 ||
                count is < 1 or > 4096 || windows.ValueKind != JsonValueKind.Array || windows.GetArrayLength() > 10_000) throw Invalid();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            long tabs = 0;
            foreach (var window in windows.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var key = window.GetProperty("id").GetString();
                var list = window.GetProperty("tabs");
                if (string.IsNullOrEmpty(key) || key.Length > 512 || !ids.Add(key) || list.ValueKind != JsonValueKind.Array)
                    throw Invalid();
                tabs += list.GetArrayLength();
                if (tabs > 100_000) throw Invalid();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
    }

    private static InvalidDataException Invalid() => new("Invalid portable saved-window archive data.");
}
