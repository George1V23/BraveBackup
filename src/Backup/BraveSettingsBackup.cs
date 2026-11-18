using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Captures only the seven approved stored Brave preference paths, without editing the profile.</summary>
public static class BraveSettingsBackup
{
    private static readonly (string Path, JsonValueKind Kind)[] Rules =
    {
        ("bookmark_bar.show_on_all_tabs", JsonValueKind.True),
        ("bookmark_bar.visibility_state", JsonValueKind.Number),
        ("browser.show_home_button", JsonValueKind.True),
        ("homepage_is_newtabpage", JsonValueKind.True),
        ("homepage", JsonValueKind.String),
        ("session.restore_on_startup", JsonValueKind.Number),
        ("session.startup_urls", JsonValueKind.Array)
    };

    /// <summary>Reads a bounded snapshot with limited retries and returns application-owned allowlisted JSON.</summary>
    public static byte[] Capture(string profileDirectory, CancellationToken token = default)
    {
        var path = Path.Combine(profileDirectory, "Preferences");
        for (var attempt = 0;; attempt++)
        {
            token.ThrowIfCancellationRequested();
            byte[]? snapshot = null;
            try
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var stamp = File.GetLastWriteTimeUtc(path);
                var length = source.Length;
                if (length > ArchiveLimits.MaxPreferencesBytes)
                    throw new BackupOperationException("settings-source-size",
                        "Preferences exceeds the 32 MiB settings capture limit.");
                snapshot = new byte[checked((int)length)];
                for (var offset = 0; offset < snapshot.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var read = source.Read(snapshot, offset, Math.Min(65536, snapshot.Length - offset));
                    if (read == 0) throw new IOException("Preferences changed during capture.");
                    offset += read;
                }

                if (source.Length != length || source.ReadByte() != -1 || File.GetLastWriteTimeUtc(path) != stamp)
                    throw new IOException("Preferences changed during capture.");
                return Extract(snapshot, token);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                if (error is BackupOperationException) throw;
                if (attempt >= 2)
                    throw new BackupOperationException("settings-source-unreadable",
                        "Preferences could not be read as valid supported settings. Close Brave or check the profile and try again.");
            }
            finally
            {
                if (snapshot != null) CryptographicOperations.ZeroMemory(snapshot);
            }
        }
    }

    /// <summary>Extracts stored values only; absence is recorded without guessing defaults or effective policy.</summary>
    public static byte[] Extract(ReadOnlyMemory<byte> snapshot, CancellationToken token = default)
    {
        using var document = ParsePreferences(snapshot, token);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var absent = new List<string>();
        foreach (var rule in Rules)
        {
            token.ThrowIfCancellationRequested();
            var value = Find(document.RootElement, rule.Path);
            if (!value.HasValue) absent.Add(rule.Path);
            else
            {
                CheckValue(rule.Kind, value.Value);
                values.Add(rule.Path, value.Value);
            }
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new PortableSettings(1, values, absent.ToArray()),
            ArchiveManifest.JsonOptions);
        if (payload.Length > ArchiveLimits.MaxSettingsBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new BackupOperationException("settings-payload-size",
                "Supported settings exceed the 1 MiB payload limit.");
        }

        return payload;
    }

    /// <summary>Checks archive integrity and the Brave-specific settings allowlist without extracting files.</summary>
    public static ArchiveManifest ValidateArchive(Stream source, string? password = null,
        CancellationToken token = default) => ValidateArchive(source, password, token, null);

    /// <summary>Validates the archive and supplies verified category metadata to optional inspection readers.</summary>
    internal static ArchiveManifest ValidateArchive(Stream source, string? password, CancellationToken token,
        Action<Guid, ExtensionSnapshotInventory, ZipArchive>? readExtensions,
        Action<Guid, PortableSession>? readSessions = null,
        Action<Guid, ArchivedPasswordInventory>? readPasswords = null)
    {
        token.ThrowIfCancellationRequested();
        if (source.Length > ArchiveLimits.MaxBytes + 3 * 1024 * 1024L)
            throw new InvalidDataException("Archive exceeds its supported size.");
        var manifest = BackupArchive.Validate(source, password, token, readPasswords);
        source.Position = 0;
        using var decrypted = ArchiveEncryption.IsEncrypted(source)
            ? ArchiveEncryption.OpenReader(source, password ?? string.Empty, token)
            : null;
        var content = decrypted ?? source;
        content.Position = 0;
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var (profileId, category) in manifest.Installations.SelectMany(item => item.Profiles)
                     .SelectMany(profile => profile.Categories.Select(category => (profile.Id, category))))
        {
            token.ThrowIfCancellationRequested();
            if (category.Id == "tabmanagerplus")
            {
                BraveTabManagerPlusBackup.ValidateCategory(zip, category, token);
                continue;
            }

            if (category.Id == "sessions")
            {
                var session = BraveSessionBackup.ReadValidated(zip, category, token);
                readSessions?.Invoke(profileId, session);
                continue;
            }

            if (category.Id == "extensions")
            {
                var inventory = BraveExtensionBackup.ReadValidatedInventory(zip, category, token);
                readExtensions?.Invoke(profileId, inventory, zip);
                continue;
            }

            if (category.Id != "settings") continue;
            var descriptor = category.Files.Single();
            var bytes = new byte[checked((int)descriptor.Length)];
            try
            {
                using var input = zip.GetEntry(descriptor.Path)!.Open();
                input.ReadExactly(bytes);
                ParsePayload(bytes, token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        return manifest;
    }

    /// <summary>Validates the exact approved key union/types and returns the number of stored settings values.</summary>
    public static int ValidatePayload(ReadOnlyMemory<byte> payload) =>
        ParsePayload(payload, CancellationToken.None).Values.Count;

    /// <summary>Reads the validated portable Settings values for backup verification or confirmed restore.</summary>
    internal static PortableSettings ParsePayload(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (payload.Length > ArchiveLimits.MaxSettingsBytes)
            throw new InvalidDataException("Settings payload exceeds 1 MiB.");
        using var document = JsonDocument.Parse(payload);
        RejectDuplicates(document.RootElement, token);
        var settings = PortableSettings.Parse(document.RootElement);
        var all = settings.Values.Keys.Concat(settings.AbsentKeys).ToHashSet(StringComparer.Ordinal);
        if (!all.SetEquals(Rules.Select(rule => rule.Path)))
            throw new InvalidDataException("Invalid approved settings key set.");
        foreach (var rule in Rules)
            if (settings.Values.TryGetValue(rule.Path, out var value))
                CheckValue(rule.Kind, value);
        return settings;
    }

    /// <summary>Reads a bounded native preferences dictionary and rejects ambiguous key names.</summary>
    internal static JsonDocument ParsePreferences(ReadOnlyMemory<byte> snapshot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (snapshot.Length > ArchiveLimits.MaxPreferencesBytes)
            throw new BackupOperationException("settings-source-size", "Preferences exceeds the 32 MiB settings limit.");
        if (snapshot.Span.StartsWith("\uFEFF"u8)) snapshot = snapshot[3..];
        var document = JsonDocument.Parse(snapshot);
        try
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new BackupOperationException("settings-source-invalid", "Preferences must contain a JSON object.");
            RejectDuplicates(document.RootElement, token);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    /// <summary>Looks up a dotted path as exact-case nested dictionary keys.</summary>
    internal static JsonElement? Find(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object)
                throw new BackupOperationException("settings-source-invalid",
                    "A supported settings path has an invalid parent value.");
            if (!current.TryGetProperty(part, out var value)) return null;
            current = value;
        }

        return current;
    }

    private static void CheckValue(JsonValueKind kind, JsonElement value)
    {
        if (!IsValidValue(kind, value))
            throw new BackupOperationException("settings-value-invalid",
                "A supported setting has an invalid type or exceeds its value limit.");
    }

    /// <summary>Checks whether a value fits the approved primitive kind and size limits.</summary>
    internal static bool IsValidValue(JsonValueKind kind, JsonElement value) =>
        kind switch
        {
            JsonValueKind.True => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            JsonValueKind.Number => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            JsonValueKind.String => value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 32768,
            JsonValueKind.Array => value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 1024 &&
                                   value.EnumerateArray().All(item =>
                                       item.ValueKind == JsonValueKind.String && item.GetString()!.Length <= 32768),
            _ => false
        };

    private static void RejectDuplicates(JsonElement value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (value.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject())
            {
                if (!keys.Add(item.Name))
                    throw new BackupOperationException("settings-source-ambiguous",
                        "Preferences contains ambiguous duplicate keys.");
                RejectDuplicates(item.Value, token);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                RejectDuplicates(item, token);
    }
}