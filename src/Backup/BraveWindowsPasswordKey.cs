using System.Security.Cryptography;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Reads only the researched Windows DPAPI v10 key envelope from supplied Local State bytes.</summary>
public static class BraveWindowsPasswordKey
{
    /// <summary>Unlocks an existing key only on explicit invocation. No files, browser or key stores are changed.</summary>
    /// <remarks>The caller must capture and verify the source separately. The unprotector returns owned bytes, cleared here.</remarks>
    public static PasswordKeyMaterial Unlock(ReadOnlyMemory<byte> localState,
        Func<byte[], byte[]>? unprotect = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (localState.IsEmpty || localState.Length > 4 * 1024 * 1024) throw InvalidEnvelope();
        byte[]? envelope = null;
        byte[]? wrapped = null;
        byte[]? key = null;
        try
        {
            using var document = JsonDocument.Parse(localState, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidEnvelope();
            var crypt = Unique(root, "os_crypt");
            if (crypt.ValueKind != JsonValueKind.Object) throw InvalidEnvelope();
            var encoded = Unique(crypt, "encrypted_key");
            if (encoded.ValueKind != JsonValueKind.String) throw InvalidEnvelope();
            var text = encoded.GetString()!;
            if (text.Length > 90000) throw InvalidEnvelope();
            envelope = Convert.FromBase64String(text);
            if (envelope.Length <= 5 || envelope.Length > 65541 || !envelope.AsSpan().StartsWith("DPAPI"u8))
                throw InvalidEnvelope();
            wrapped = envelope.AsSpan(5).ToArray();
            token.ThrowIfCancellationRequested();
            key = unprotect is null ? WindowsDpapi.Unprotect(wrapped) : unprotect(wrapped);
            token.ThrowIfCancellationRequested();
            if (key is null || key.Length != 32)
                throw new BackupOperationException("passwords-key-unlock-failed", "The browser key could not be unlocked as a supported Windows v10 key.");
            return new PasswordKeyMaterial(PasswordEncryptionProvider.WindowsDpapiV10, key);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { throw InvalidEnvelope(); }
        catch (Exception error) when (error is CryptographicException or PlatformNotSupportedException)
        { throw new BackupOperationException("passwords-key-unlock-failed", "The current platform or user could not unlock the supported browser key."); }
        finally
        {
            if (envelope is not null) CryptographicOperations.ZeroMemory(envelope);
            if (wrapped is not null) CryptographicOperations.ZeroMemory(wrapped);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static JsonElement Unique(JsonElement parent, string name)
    {
        var found = false;
        var result = default(JsonElement);
        foreach (var property in parent.EnumerateObject())
        {
            if (!property.NameEquals(name)) continue;
            if (found) throw InvalidEnvelope();
            found = true;
            result = property.Value;
        }
        if (!found) throw InvalidEnvelope();
        return result;
    }

    private static BackupOperationException InvalidEnvelope() => new("passwords-key-envelope-unsupported",
        "Local State does not contain one supported existing Windows DPAPI key envelope.");
}
