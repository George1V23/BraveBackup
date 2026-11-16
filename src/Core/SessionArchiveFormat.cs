using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Validates portable session structure and optional raw payload linkage without browser-specific decoding.</summary>
public static class SessionArchiveFormat
{
    /// <summary>Checks each session descriptor/hash and then the declared portable/native relationships.</summary>
    public static void ValidateCategory(ZipArchive zip, Dictionary<string, ZipArchiveEntry> remaining,
        string prefix, ArchiveCategory category, CancellationToken token)
    {
        if (category.Files.Length is < 1 or > 1026) throw new InvalidDataException("Invalid session payload count.");
        var files = new Dictionary<string, ArchiveFile>(StringComparer.Ordinal);
        foreach (var file in category.Files)
        {
            token.ThrowIfCancellationRequested();
            var max = file?.Role switch
            {
                "portable-session" => PortableSessionFormat.MaxPortableBytes,
                "native-session-inventory" => ArchiveLimits.MaxManifestBytes,
                "native-session-file" => ArchiveLimits.MaxBookmarkBytes, _ => 0
            };
            if (file is null || file.Path is null || max == 0 || file.Length < 0 || file.Length > max ||
                !PortableSessionFormat.ValidHash(file.Sha256) || !files.TryAdd(file.Path, file) ||
                !remaining.Remove(file.Path, out var entry) || entry.Length != file.Length)
                throw new InvalidDataException("Invalid session payload descriptor.");
            using var input = entry.Open();
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
                    if (length > file.Length)
                        throw new InvalidDataException("Session payload exceeds declared length.");
                    hash.AppendData(buffer, 0, count);
                }

                if (length != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                    throw new InvalidDataException("Session payload integrity check failed.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(buffer);
            }
        }

        if (!files.Remove(prefix + "portable-v1.json", out var portable) || portable.Role != "portable-session")
            throw new InvalidDataException("Missing portable session payload.");
        using var portableDocument = ReadJson(zip, portable, token);
        var model = PortableSessionFormat.Parse(portableDocument.RootElement);
        if (!model.IncludeNativeFiles)
        {
            if (files.Count != 0) throw new InvalidDataException("Undeclared native session inclusion.");
            return;
        }

        if (!files.Remove(prefix + "native-inventory-v1.json", out var metadata) ||
            metadata.Role != "native-session-inventory")
            throw new InvalidDataException("Missing native session inventory.");
        using var inventoryDocument = ReadJson(zip, metadata, token);
        var inventory = inventoryDocument.Deserialize<NativeSessionInventory>(ArchiveManifest.JsonOptions);
        if (inventory is null || inventory.SchemaVersion != 1 || inventory.AbsentLegacyFiles is null ||
            inventory.AbsentLegacyFiles.Length > 1024 || inventory.Files is null ||
            inventory.Files.Length is < 1 or > 1024)
            throw new InvalidDataException("Invalid native session inventory.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in inventory.AbsentLegacyFiles)
            if (!PortableSessionFormat.SafeNativeName(name) || !names.Add(name))
                throw new InvalidDataException("Invalid absent native session name.");
        var hasSource = false;
        foreach (var file in inventory.Files)
        {
            token.ThrowIfCancellationRequested();
            if (file is null || !PortableSessionFormat.SafeNativeName(file.RelativePath) ||
                !names.Add(file.RelativePath) ||
                file.ArchivePath != prefix + "native/" + file.RelativePath ||
                !files.Remove(file.ArchivePath, out var descriptor) ||
                descriptor.Role != "native-session-file" || descriptor.Length != file.Length ||
                descriptor.Sha256 != file.Sha256)
                throw new InvalidDataException("Invalid native session linkage.");
            if (file.RelativePath == model.Source.Path)
            {
                if (file.Length != model.Source.Length || file.Sha256 != model.Source.Sha256)
                    throw new InvalidDataException("Native/portable source mismatch.");
                hasSource = true;
            }
        }

        if (!hasSource || files.Count != 0)
            throw new InvalidDataException("Missing source or unreferenced native session payloads.");
    }

    /// <summary>Reads declared bounded JSON, rejecting duplicate property names before deserialization.</summary>
    public static JsonDocument ReadJson(ZipArchive zip, ArchiveFile file, CancellationToken token)
    {
        var bytes = new byte[checked((int)file.Length)];
        try
        {
            using var input = zip.GetEntry(file.Path)!.Open();
            for (var offset = 0; offset < bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                if (count == 0) throw new InvalidDataException("Truncated session JSON.");
                offset += count;
            }

            using var buffer = new MemoryStream(bytes, writable: false);
            var document = JsonDocument.Parse(buffer);
            try
            {
                DuplicateCheck(document.RootElement);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void DuplicateCheck(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate session JSON property.");
                DuplicateCheck(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                DuplicateCheck(item);
    }
}