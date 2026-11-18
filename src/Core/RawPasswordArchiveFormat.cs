using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Archive-v7 uninterpreted password-file preservation, without SQLite recovery or portability claims.</summary>
internal static class RawPasswordArchiveFormat
{
    /// <summary>Returns the number of missing main databases recorded in the validated raw-file index.</summary>
    internal static int ValidateCategory(Dictionary<string, ZipArchiveEntry> entries, string prefix,
        ArchiveCategory category, CancellationToken token)
    {
        if (category.Files.Length is < 2 or > 9) throw Invalid();
        var index = category.Files.Where(file => file?.Role == "raw-password-index").ToArray();
        if (index.Length != 1 || index[0].Path != prefix + "raw-index-v1.json" || index[0].Length is <= 0 or > 4096) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        ZipArchiveEntry? indexZipEntry = null;
        foreach (var file in category.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || file.Path is null) throw Invalid();
            var isIndex = file.Role == "raw-password-index";
            if (!isIndex)
            {
                if (file.Role != "native-password-file" || !file.Path.StartsWith(prefix + "raw/", StringComparison.Ordinal)) throw Invalid();
                var name = file.Path[(prefix.Length + 4)..];
                if (!AllowedName(name) || !names.Add(name) || file.Length is < 0 or > 64L * 1024 * 1024) throw Invalid();
                total += file.Length;
                if (total > 128L * 1024 * 1024) throw Invalid();
            }
            if (file.Sha256 is null || file.Sha256.Length != 64 || file.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                !entries.Remove(file.Path, out var entry) || entry.Length != file.Length) throw Invalid();
            if (isIndex) indexZipEntry = entry;
            using var input = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            try
            {
                long read = 0; int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested(); read += count;
                    if (read > file.Length) throw Invalid();
                    hash.AppendData(buffer.AsSpan(0, count));
                }
                if (read != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256) throw Invalid();
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        var bytes = new byte[(int)index[0].Length];
        try
        {
            using var indexInput = indexZipEntry!.Open(); indexInput.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in root.EnumerateObject())
                if (!properties.Add(property.Name) || property.Name is not ("schemaVersion" or "captureKind" or "portableCaptureCompleted" or "reasonCode" or "missingMainDatabases")) throw Invalid();
            if (properties.Count != 5 || root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("captureKind").GetString() != "raw-native-password-files" ||
                root.GetProperty("portableCaptureCompleted").ValueKind != JsonValueKind.False ||
                !AllowedReason(root.GetProperty("reasonCode").GetString())) throw Invalid();
            var missing = root.GetProperty("missingMainDatabases");
            if (missing.ValueKind != JsonValueKind.Array || missing.GetArrayLength() > 2) throw Invalid();
            var actualMissing = missing.EnumerateArray().Select(value => value.GetString()).ToArray();
            var expectedMissing = new[] { "Login Data", "Login Data For Account" }.Where(store =>
                !names.Contains(store) && names.Any(name => name.StartsWith(store + "-", StringComparison.Ordinal)));
            if (!actualMissing.SequenceEqual(expectedMissing)) throw Invalid();
            return actualMissing.Length;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw Invalid(); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static bool AllowedReason(string? code) => code is "passwords-source-invalid" or "passwords-database-invalid" or
        "passwords-schema-unsupported" or "passwords-scheme-unsupported" or "passwords-journal-unsupported" or
        "passwords-encryption-unsupported" or "passwords-decode-failed" or "passwords-key-unlock-failed" or
        "passwords-key-envelope-unsupported" or "passwords-key-source-invalid" or "passwords-capture-limit";

    private static bool AllowedName(string name) => new[] { "Login Data", "Login Data For Account" }
        .Any(store => new[] { "", "-wal", "-shm", "-journal" }.Any(suffix => name == store + suffix));
    private static InvalidDataException Invalid() => new("Invalid raw/native password archive category.");
}
