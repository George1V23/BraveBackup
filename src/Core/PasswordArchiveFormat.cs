using System.IO.Compression;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Validates protected v6/v7 and explicit CSV v8 password categories without extracting files.</summary>
internal static class PasswordArchiveFormat
{
    /// <summary>Authenticates and validates one representation, optionally deriving inventory before disposing secrets.</summary>
    internal static ArchivedPasswordInventory? ValidateCategory(Dictionary<string, ZipArchiveEntry> entries, string prefix,
        ArchiveCategory category, CancellationToken token, bool collectInventory)
    {
        if (category.Version == 3)
        {
            var rows = CsvPasswordArchiveFormat.ValidateCategory(entries, prefix, category, token);
            return collectInventory ? new ArchivedPasswordInventory.Csv(rows) : null;
        }
        if (category.Version == 2 && category.Files.Any(file => file?.Role == "raw-password-index"))
        {
            var missing = RawPasswordArchiveFormat.ValidateCategory(entries, prefix, category, token);
            return collectInventory ? new ArchivedPasswordInventory.RawFiles(category.Files.Length - 1, missing) : null;
        }
        if (category.Files.Length != 1) throw Invalid();
        var file = category.Files[0];
        var expected = prefix + "portable-v1.bin";
        if (file is null || file.Role != "portable-passwords" || file.Path != expected ||
            file.Length is <= 0 or > PasswordPayloadFormat.MaximumBytes || file.Sha256 is null ||
            file.Sha256.Length != 64 || file.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !entries.Remove(expected, out var entry) || entry.Length != file.Length) throw Invalid();
        var bytes = new byte[(int)file.Length];
        try
        {
            using var input = entry.Open();
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var read = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (read == 0) throw Invalid();
                offset += read;
            }
            if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256) throw Invalid();
            using var document = PasswordPayloadFormat.Decode(bytes, token);
            if (!collectInventory) return null;
            var stores = document.Stores.Select(store => new ArchivedPasswordStore(store.Kind, store.Logins.Count,
                store.Logins.Sum(login => login.Notes.Count), store.BlockedEntries, store.FederatedEntries)).ToArray();
            return new ArchivedPasswordInventory.Portable(Array.AsReadOnly(stores));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static InvalidDataException Invalid() => new("Invalid protected portable-password archive category.");
}
