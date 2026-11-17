using System.IO.Compression;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Validates protected password categories of archive versions 6/7 without extracting files.</summary>
internal static class PasswordArchiveFormat
{
    internal static void ValidateCategory(Dictionary<string, ZipArchiveEntry> entries, string prefix,
        ArchiveCategory category, CancellationToken token)
    {
        if (category.Version == 2 && category.Files.Any(file => file?.Role == "raw-password-index"))
        { RawPasswordArchiveFormat.ValidateCategory(entries, prefix, category, token); return; }
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
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static InvalidDataException Invalid() => new("Invalid protected portable-password archive category.");
}
