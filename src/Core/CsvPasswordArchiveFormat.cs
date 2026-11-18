using System.IO.Compression;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Validates archive-v8 explicit CSV categories in memory, without extraction or save-consent claims.</summary>
internal static class CsvPasswordArchiveFormat
{
    /// <summary>Returns the data-row count from the validated CSV.</summary>
    internal static int ValidateCategory(Dictionary<string, ZipArchiveEntry> entries, string prefix,
        ArchiveCategory category, CancellationToken token)
    {
        if (category.Files.Length != 1) throw Invalid();
        var file = category.Files[0];
        if (file is null || file.Role != "plaintext-password-csv" || file.Path != prefix + "passwords.csv" ||
            file.Length is <= 0 or > PasswordCsvFormat.MaximumBytes || file.Sha256 is null ||
            file.Sha256.Length != 64 || file.Sha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !entries.Remove(file.Path, out var entry) || entry.Length != file.Length) throw Invalid();
        var bytes = new byte[(int)file.Length];
        try
        {
            using var input = entry.Open();
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw Invalid();
                offset += count;
            }
            if (input.ReadByte() != -1 || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256) throw Invalid();
            return PasswordCsvValidation.Validate(bytes, token);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static InvalidDataException Invalid() => new("Invalid password CSV archive category.");
}
