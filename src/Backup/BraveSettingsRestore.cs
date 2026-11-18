using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BraveBackup.Core;

/// <summary>Builds a replacement Preferences image from a validated portable Settings payload.</summary>
internal static class BraveSettingsRestore
{
    internal const string FileName = "Preferences";
    internal const string SecureFileName = "Secure Preferences";

    /// <summary>Applies stored values to the plain preferences dictionary, preserving all other values.</summary>
    internal static byte[] Apply(ReadOnlyMemory<byte> destination, ReadOnlyMemory<byte>? secure,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        var settings = BraveSettingsBackup.ParsePayload(payload, token);
        using var current = BraveSettingsBackup.ParsePreferences(destination, token);
        using var protectedPrefs = secure is { } bytes ? BraveSettingsBackup.ParsePreferences(bytes, token) : null;

        if (settings.Values.Count == 0)
            throw new BackupOperationException("restore-settings-empty", "The selected profile has no stored Settings values to restore.");

        var updated = JsonObject.Create(current.RootElement)!;
        foreach (var (key, value) in settings.Values)
        {
            token.ThrowIfCancellationRequested();
            if (protectedPrefs is not null && BraveSettingsBackup.Find(protectedPrefs.RootElement, key) is not null)
                throw new BackupOperationException("restore-settings-protected",
                    "A selected Settings value is stored in the destination's Secure Preferences.");

            // Chromium stores dotted paths as nested dictionaries.
            var parts = key.Split('.');
            var parent = updated;
            foreach (var part in parts.AsSpan(0, parts.Length - 1))
            {
                if (!parent.TryGetPropertyValue(part, out var child))
                {
                    var added = new JsonObject();
                    parent.Add(part, added);
                    parent = added;
                }
                else if (child is JsonObject existing)
                {
                    parent = existing;
                }
                else
                {
                    throw new BackupOperationException("restore-settings-parent",
                        "A destination Settings path conflicts with an existing non-dictionary value.");
                }
            }
            if (parent.ContainsKey(parts[^1]) && BraveSettingsBackup.Find(current.RootElement, key) is { } existingValue)
            {
                var kind = value.ValueKind == JsonValueKind.False ? JsonValueKind.True : value.ValueKind;
                if (!BraveSettingsBackup.IsValidValue(kind, existingValue))
                    throw new BackupOperationException("restore-settings-value",
                        "A destination Settings value does not have the expected type or size.");
            }
            parent[parts[^1]] = JsonNode.Parse(value.GetRawText());
        }

        using var buffer = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                updated.WriteTo(writer);
            token.ThrowIfCancellationRequested();
            if (buffer.Length > ArchiveLimits.MaxPreferencesBytes)
                throw new BackupOperationException("restore-settings-size", "Updated Preferences exceeds the 32 MiB limit.");
            return buffer.ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
        }
    }
}
