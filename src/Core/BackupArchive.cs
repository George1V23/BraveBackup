using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Writes and verifies archive-v1/v2/v3/v4/v5/v6/v7 ZIP content independently of any browser or UI.</summary>
public static class BackupArchive
{
    /// <summary>Maps the four approved compression choices without adding package dependencies.</summary>
    public static CompressionLevel Compression(string choice) => choice switch
    {
        "none" => CompressionLevel.NoCompression, "fast" => CompressionLevel.Fastest,
        "balanced" => CompressionLevel.Optimal, "high" => CompressionLevel.SmallestSize,
        _ => throw new InvalidDataException("Unsupported compression choice.")
    };

    /// <summary>Writes a single bounded payload and returns its manifest descriptor.</summary>
    public static ArchiveFile WriteFile(ZipArchive zip, string path, string role, byte[] bytes,
        string compression, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var entry = zip.CreateEntry(path, Compression(compression));
        using var stream = entry.Open();
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var take = Math.Min(64 * 1024, bytes.Length - offset);
            stream.Write(bytes, offset, take);
            offset += take;
        }

        return new ArchiveFile(path, role, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Writes the manifest after payloads have completed.</summary>
    public static void WriteManifest(ZipArchive zip, ArchiveManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ArchiveManifest.JsonOptions);
        if (bytes.Length > ArchiveLimits.MaxManifestBytes) throw new InvalidDataException("Manifest exceeds 16 MiB.");
        using var output = zip.CreateEntry("manifest.json", Compression(manifest.Compression)).Open();
        output.Write(bytes);
    }

    /// <summary>Authenticates, checks structure and hashes all payloads without extracting files.</summary>
    /// <remarks>Caller owns the seekable source. Legacy bookmark JSON remains a distinct format.</remarks>
    public static ArchiveManifest Validate(Stream source, string? password = null, CancellationToken token = default) =>
        Validate(source, password, token, null);

    /// <summary>Validates the archive and optionally reports value-free password inventory by source profile ID.</summary>
    /// <remarks>Discard all reported metadata if validation fails. The callback receives no credential data or paths.</remarks>
    public static ArchiveManifest Validate(Stream source, string? password, CancellationToken token,
        Action<Guid, ArchivedPasswordInventory>? readPasswords)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length > ArchiveLimits.MaxBytes + 3 * 1024 * 1024L)
            throw new InvalidDataException("Archive exceeds its supported size.");
        source.Position = 0;
        var encryptedEnvelope = ArchiveEncryption.IsEncrypted(source);
        using var decrypted = encryptedEnvelope
            ? ArchiveEncryption.OpenReader(source, password ?? string.Empty, token)
            : null;
        var content = decrypted ?? source;
        if (content.Length > ArchiveLimits.MaxBytes) throw new InvalidDataException("Archive exceeds 100 GiB.");
        ArchiveZipGuard.Validate(content, token);
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        if (zip.Entries.Count is < 1 or > ArchiveLimits.MaxEntries)
            throw new InvalidDataException("Invalid archive entry count.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        long expanded = 0;
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length < 0 || entry.Length > ArchiveLimits.MaxBytes - expanded ||
                !entries.TryAdd(entry.FullName, entry) || entry.FullName.Contains('\\') ||
                entry.FullName.StartsWith('/') || entry.FullName.Split('/').Any(part => part is "" or "." or ".."))
                throw new InvalidDataException("Invalid or duplicate ZIP entry.");
            expanded += entry.Length;
        }

        if (!entries.Remove("manifest.json", out var manifestEntry) ||
            manifestEntry.Length > ArchiveLimits.MaxManifestBytes)
            throw new InvalidDataException("Missing or oversized manifest.");
        ArchiveManifest manifest;
        using (var input = manifestEntry.Open())
        {
            var bytes = ReadBounded(input, (int)manifestEntry.Length, token);
            using var json = JsonDocument.Parse(bytes);
            RejectDuplicateProperties(json.RootElement);
            manifest = json.Deserialize<ArchiveManifest>(ArchiveManifest.JsonOptions)
                       ?? throw new InvalidDataException("Missing archive manifest.");
        }

        ValidateManifest(manifest);
        if (manifest.RequiredCapabilities.Any(capability => capability is "passwords-v1" or "passwords-v2") && !encryptedEnvelope)
            throw new InvalidDataException("Portable password payloads require an encrypted archive envelope.");
        var ids = new HashSet<Guid>();
        foreach (var installation in manifest.Installations)
        {
            if (installation is null || installation.Id == Guid.Empty || !ids.Add(installation.Id) ||
                installation.Profiles is null || installation.Profiles.Length == 0 ||
                string.IsNullOrEmpty(installation.Product) || string.IsNullOrEmpty(installation.Channel) ||
                installation.BrowserVersion is null) throw new InvalidDataException("Invalid installation metadata.");
            foreach (var profile in installation.Profiles)
            {
                if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id) ||
                    profile.DisplayName is null ||
                    string.IsNullOrEmpty(profile.DirectoryName) || profile.DirectoryName is "." or ".." ||
                    profile.DirectoryName.IndexOfAny(['/', '\\', ':']) >= 0 ||
                    profile.Categories is null || profile.Categories.Length != manifest.RequiredCapabilities.Length)
                    throw new InvalidDataException("Invalid profile metadata.");
                var categoryIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var category in profile.Categories)
                {
                    if (category is null || !categoryIds.Add(category.Id) || category.Version != (category.Id == "passwords" && manifest.FormatVersion == 8 ? 3 : category.Id == "passwords" && manifest.FormatVersion == 7 ? 2 : 1) ||
                        category.Files is null ||
                        !manifest.RequiredCapabilities.Contains(category.Id + "-v" + category.Version, StringComparer.Ordinal))
                        throw new InvalidDataException("Unsupported or duplicate archive category.");
                    if (category.Id == "passwords")
                    {
                        var summary = PasswordArchiveFormat.ValidateCategory(entries,
                            $"installations/{installation.Id:D}/profiles/{profile.Id:D}/passwords/", category,
                            token, collectInventory: readPasswords is not null);
                        if (summary is not null) readPasswords?.Invoke(profile.Id, summary);
                        continue;
                    }

                    if (category.Id == "tabmanagerplus")
                    {
                        ExtensionSavedWindowsArchiveFormat.ValidateCategory(zip, entries,
                            $"installations/{installation.Id:D}/profiles/{profile.Id:D}/tabmanagerplus/", category,
                            token);
                        continue;
                    }

                    if (category.Id == "sessions")
                    {
                        SessionArchiveFormat.ValidateCategory(zip, entries,
                            $"installations/{installation.Id:D}/profiles/{profile.Id:D}/sessions/", category, token);
                        continue;
                    }

                    if (category.Id == "extensions")
                    {
                        ExtensionSnapshotFormat.ValidateCategory(zip, entries,
                            $"installations/{installation.Id:D}/profiles/{profile.Id:D}/extensions/", category, token);
                        continue;
                    }

                    var settings = category.Id == "settings";
                    if (category.Files.Length != (settings ? 1 : 2))
                        throw new InvalidDataException("Invalid category payload count.");
                    var roles = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var file in category.Files)
                    {
                        token.ThrowIfCancellationRequested();
                        if (file is null || !roles.Add(file.Role) || (settings
                                ? file.Role != "portable-settings"
                                : file.Role is not ("native-source" or "portable-v1")))
                            throw new InvalidDataException("Invalid category payload role.");
                        var name = settings ? "allowlisted-v1.json" :
                            file.Role == "native-source" ? "source.json" : "portable-v1.json";
                        var expected =
                            $"installations/{installation.Id:D}/profiles/{profile.Id:D}/{category.Id}/{name}";
                        if (file.Path != expected || file.Length < 0 || file.Length >
                            (settings ? ArchiveLimits.MaxSettingsBytes : ArchiveLimits.MaxBookmarkBytes) ||
                            file.Sha256 is null || file.Sha256.Length != 64 ||
                            file.Sha256.Any(character =>
                                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
                            !entries.Remove(expected, out var entry) || entry.Length != file.Length)
                            throw new InvalidDataException("Missing or invalid payload metadata.");
                        using var stream = entry.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[64 * 1024];
                        long read = 0;
                        try
                        {
                            int count;
                            while ((count = stream.Read(buffer)) != 0)
                            {
                                token.ThrowIfCancellationRequested();
                                read += count;
                                if (read > file.Length)
                                    throw new InvalidDataException("Payload exceeds declared length.");
                                hash.AppendData(buffer, 0, count);
                            }

                            if (read != file.Length || Convert.ToHexStringLower(hash.GetHashAndReset()) != file.Sha256)
                                throw new InvalidDataException("Payload integrity check failed.");
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(buffer);
                        }

                        // Parse only after length/hash verification. Allocation is bounded by the category limit.
                        using var jsonStream = entry.Open();
                        var jsonBytes = ReadBounded(jsonStream, checked((int)file.Length), token);
                        try
                        {
                            var jsonContent = jsonBytes.AsMemory();
                            if (jsonContent.Span.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
                                jsonContent = jsonContent[3..];
                            using var payloadJson = JsonDocument.Parse(jsonContent);
                            if (payloadJson.RootElement.ValueKind != JsonValueKind.Object)
                                throw new InvalidDataException("Invalid JSON payload.");
                            if (file.Role == "portable-settings")
                            {
                                RejectDuplicateProperties(payloadJson.RootElement);
                                PortableSettings.Parse(payloadJson.RootElement);
                            }

                            if (file.Role == "portable-v1")
                            {
                                var model = payloadJson.Deserialize<BackupModel>(new JsonSerializerOptions
                                    { RespectRequiredConstructorParameters = true });
                                if (model is null || model.SchemaVersion != 1 || model.Bookmarks is null)
                                    throw new InvalidDataException("Unsupported portable bookmark payload.");
                                foreach (var bookmark in model.Bookmarks) ValidateBookmark(bookmark);
                            }
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(jsonBytes);
                        }
                    }
                }
            }
        }

        if (entries.Count != 0) throw new InvalidDataException("Undeclared archive payloads.");
        return manifest;
    }

    private static void ValidateBookmark(BackupBookmark item)
    {
        if (item is null || item.Name is null || item.Children is null ||
            item.Type is not (BackupBookmarkType.Folder or BackupBookmarkType.Url) ||
            (item.Type == BackupBookmarkType.Url && (item.Url is null || item.Children.Count != 0)) ||
            (item.Type == BackupBookmarkType.Folder && item.Url is not null))
            throw new InvalidDataException("Invalid portable bookmark tree.");
        foreach (var child in item.Children) ValidateBookmark(child);
    }

    private static void ValidateManifest(ArchiveManifest manifest)
    {
        if (manifest.Format != "BraveBackupArchive" || manifest.FormatVersion is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8) ||
            manifest.ArchiveId == Guid.Empty ||
            manifest.CreatedUtc == default || manifest.CreatedUtc.Offset != TimeSpan.Zero ||
            string.IsNullOrEmpty(manifest.ApplicationVersion) || manifest.RequiredCapabilities is null ||
            !(manifest.FormatVersion == 1
                ? manifest.RequiredCapabilities.SequenceEqual(new[] { "bookmarks-v1" })
                : manifest.FormatVersion == 2
                    ? manifest.RequiredCapabilities.SequenceEqual(new[] { "settings-v1" }) ||
                      manifest.RequiredCapabilities.SequenceEqual(new[] { "bookmarks-v1", "settings-v1" })
                    : manifest.FormatVersion == 3
                        ? manifest.RequiredCapabilities.Contains("extensions-v1") &&
                          manifest.RequiredCapabilities.SequenceEqual(
                              new[] { "bookmarks-v1", "settings-v1", "extensions-v1" }.Where(manifest
                                  .RequiredCapabilities.Contains))
                        : manifest.FormatVersion == 4
                            ? manifest.RequiredCapabilities.Contains("sessions-v1") &&
                              manifest.RequiredCapabilities.SequenceEqual(
                                  new[] { "bookmarks-v1", "settings-v1", "extensions-v1", "sessions-v1" }.Where(
                                      manifest.RequiredCapabilities.Contains))
                            : manifest.FormatVersion == 5
                            ? manifest.RequiredCapabilities.Contains("tabmanagerplus-v1") &&
                              manifest.RequiredCapabilities.SequenceEqual(
                                  new[]
                                      {
                                          "bookmarks-v1", "settings-v1", "extensions-v1", "sessions-v1",
                                          "tabmanagerplus-v1"
                                      }
                                      .Where(manifest.RequiredCapabilities.Contains))
                            : manifest.FormatVersion == 6
                            ? manifest.RequiredCapabilities.Contains("passwords-v1") &&
                              manifest.RequiredCapabilities.SequenceEqual(
                                  new[] { "bookmarks-v1", "settings-v1", "extensions-v1", "sessions-v1", "tabmanagerplus-v1", "passwords-v1" }
                                      .Where(manifest.RequiredCapabilities.Contains))
                            : manifest.FormatVersion == 7
                            ? manifest.RequiredCapabilities.Contains("passwords-v2") &&
                              manifest.RequiredCapabilities.SequenceEqual(
                                  new[] { "bookmarks-v1", "settings-v1", "extensions-v1", "sessions-v1", "tabmanagerplus-v1", "passwords-v2" }
                                      .Where(manifest.RequiredCapabilities.Contains))
                            : manifest.RequiredCapabilities.Contains("passwords-v3") &&
                              manifest.RequiredCapabilities.SequenceEqual(
                                  new[] { "bookmarks-v1", "settings-v1", "extensions-v1", "sessions-v1", "tabmanagerplus-v1", "passwords-v3" }
                                      .Where(manifest.RequiredCapabilities.Contains))) ||
            manifest.Installations is null || manifest.Installations.Length == 0)
            throw new InvalidDataException("Unsupported or invalid archive manifest.");
        Compression(manifest.Compression);
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate manifest JSON property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
    }

    private static byte[] ReadBounded(Stream stream, int length, CancellationToken token)
    {
        var bytes = new byte[length];
        for (var offset = 0; offset < length;)
        {
            token.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, offset, Math.Min(64 * 1024, length - offset));
            if (read == 0) throw new InvalidDataException("Truncated manifest.");
            offset += read;
        }

        if (stream.ReadByte() != -1) throw new InvalidDataException("Manifest exceeds declared length.");
        return bytes;
    }
}