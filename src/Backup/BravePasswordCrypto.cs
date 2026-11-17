using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>Explicit researched format context; a data prefix alone must never select a cross-platform algorithm.</summary>
public enum PasswordEncryptionProvider
{
    WindowsDpapiV10,
    LinuxKeyringV11
}

/// <summary>Owns a copy of an already-authorized provider key. This type performs no OS key lookup or prompting.</summary>
public sealed class PasswordKeyMaterial : IDisposable
{
    private readonly object _gate = new();
    private byte[]? _key;
    public PasswordEncryptionProvider Provider { get; }

    /// <summary>Copies an exact-length provider key. The caller remains responsible for clearing its input buffer.</summary>
    public PasswordKeyMaterial(PasswordEncryptionProvider provider, ReadOnlySpan<byte> key)
    {
        var size = provider switch
        {
            PasswordEncryptionProvider.WindowsDpapiV10 => 32,
            PasswordEncryptionProvider.LinuxKeyringV11 => 16,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        if (key.Length != size) throw new ArgumentException("Invalid provider key length.", nameof(key));
        Provider = provider;
        _key = key.ToArray();
    }

    /// <summary>Derives only the researched Linux v11 key from an explicitly supplied authoritative keyring secret.</summary>
    /// <remarks>No empty-secret fallback or keyring access occurs. A supplied empty secret is distinct from a missing secret.</remarks>
    public static PasswordKeyMaterial FromLinuxKeyringSecret(ReadOnlySpan<byte> secret)
    {
        if (secret.Length > 65536) throw new ArgumentException("Keyring secret exceeds its byte limit.", nameof(secret));
        var key = Rfc2898DeriveBytes.Pbkdf2(secret, "saltysalt"u8, 1, HashAlgorithmName.SHA1, 16);
        try { return new PasswordKeyMaterial(PasswordEncryptionProvider.LinuxKeyringV11, key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal byte[] CopyKey()
    {
        lock (_gate) return _key?.ToArray() ?? throw new ObjectDisposedException(nameof(PasswordKeyMaterial));
    }

    /// <summary>Clears the owned key. Already-started decoding owns and clears its separate temporary copy.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_key is not null) CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    public override string ToString() => "Password key material (contents hidden)";
}

/// <summary>One decoded login; secret bytes have a separate disposable owner and are never printed by ToString.</summary>
public sealed class DecodedPasswordRow(StoredPasswordRow source, SensitiveUtf8 password,
    IReadOnlyList<DecodedPasswordNote> notes)
{
    public long Id { get; } = source.Id;
    public string Origin { get; } = source.Origin;
    public string Realm { get; } = source.Realm;
    public string? Username { get; } = source.Username;
    public string? DisplayName { get; } = source.DisplayName;
    public SensitiveUtf8 Password { get; } = password;
    public IReadOnlyList<DecodedPasswordNote> Notes { get; } = notes;
    public override string ToString() => "Decoded password row (contents hidden)";
}

/// <summary>Decoded note preserving source identity, timestamp and confidentiality metadata.</summary>
public sealed class DecodedPasswordNote(StoredPasswordNote source, SensitiveUtf8 value)
{
    public string Name { get; } = source.Name;
    public long Created { get; } = source.Created;
    public bool Confidential { get; } = source.Confidential;
    public SensitiveUtf8 Value { get; } = value;
    public override string ToString() => "Decoded password note (contents hidden)";
}

/// <summary>Owns an all-or-nothing decoded result and clears all password/note byte arrays on disposal.</summary>
public sealed class DecodedPasswordDatabase : IDisposable
{
    private IReadOnlyList<DecodedPasswordRow>? _rows;
    private readonly List<SensitiveUtf8> _values;
    public IReadOnlyList<DecodedPasswordRow> Rows => _rows ?? throw new ObjectDisposedException(nameof(DecodedPasswordDatabase));
    public int BlockedEntries { get; }
    public int FederatedEntries { get; }
    internal DecodedPasswordDatabase(List<DecodedPasswordRow> rows, List<SensitiveUtf8> values, int blocked, int federated)
    { _rows = rows.AsReadOnly(); _values = values; BlockedEntries = blocked; FederatedEntries = federated; }

    public void Dispose()
    {
        foreach (var value in _values) value.Dispose();
        _values.Clear(); _rows = null;
    }
    public override string ToString() => "Decoded password database (contents hidden)";
}

/// <summary>Pure bounded Windows-v10/Linux-v11 decoding. No disk, browser, OS authentication or logging operations occur.</summary>
public static class BravePasswordCrypto
{
    /// <summary>Decodes one password/note with explicit provider context; unsupported formats never trigger another key attempt.</summary>
    public static SensitiveUtf8 Decode(ReadOnlySpan<byte> ciphertext, PasswordKeyMaterial material,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        token.ThrowIfCancellationRequested();
        if (ciphertext.Length > 4 * 1024 * 1024) throw Failure();
        var key = material.CopyKey();
        byte[]? plaintext = null;
        try
        {
            // Upstream represents an actual empty value as an empty blob, not a missing/decryption-failed value.
            if (ciphertext.IsEmpty) return new SensitiveUtf8(ReadOnlySpan<byte>.Empty);
            var expected = material.Provider == PasswordEncryptionProvider.WindowsDpapiV10 ? "v10"u8 : "v11"u8;
            if (!ciphertext.StartsWith(expected))
                throw new BackupOperationException("passwords-encryption-unsupported",
                    "A credential uses an unsupported or mismatched encryption provider; it was not omitted.");
            var content = ciphertext[3..];
            int length;
            if (material.Provider == PasswordEncryptionProvider.WindowsDpapiV10)
            {
                if (content.Length < 28) throw Failure();
                length = content.Length - 28;
                plaintext = new byte[length];
                using var aes = new AesGcm(key, 16);
                aes.Decrypt(content[..12], content.Slice(12, length), content[^16..], plaintext);
            }
            else
            {
                if (content.Length == 0 || content.Length % 16 != 0) throw Failure();
                plaintext = new byte[content.Length];
                using var aes = Aes.Create();
                aes.Key = key;
                length = aes.DecryptCbc(content, "                "u8, plaintext, PaddingMode.PKCS7);
            }
            token.ThrowIfCancellationRequested();
            return new SensitiveUtf8(plaintext.AsSpan(0, length));
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException) { throw Failure(); }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Decodes all eligible rows and every attached note, disposing partial results on any failure/cancellation.</summary>
    public static DecodedPasswordDatabase DecodeDatabase(StoredPasswordDatabase source, PasswordKeyMaterial material,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(material);
        token.ThrowIfCancellationRequested();
        var values = new List<SensitiveUtf8>();
        var rows = new List<DecodedPasswordRow>();
        long bytes = 0;
        var noteCount = 0;
        try
        {
            if (source.Rows.Count > 100_000) throw Failure();
            foreach (var row in source.Rows)
            {
                token.ThrowIfCancellationRequested();
                var password = Take(row.EncryptedValue);
                var notes = new List<DecodedPasswordNote>();
                foreach (var note in row.Notes)
                {
                    if (++noteCount > 100_000) throw Failure();
                    notes.Add(new DecodedPasswordNote(note, Take(note.EncryptedValue)));
                }
                rows.Add(new DecodedPasswordRow(row, password, notes.AsReadOnly()));
            }
            return new DecodedPasswordDatabase(rows, values, source.BlockedEntries, source.FederatedEntries);
        }
        catch { foreach (var value in values) value.Dispose(); throw; }

        SensitiveUtf8 Take(byte[] encrypted)
        {
            var value = Decode(encrypted, material, token);
            values.Add(value); bytes += value.Utf8.Length;
            if (bytes > 32 * 1024 * 1024) throw Failure();
            return value;
        }
    }

    private static BackupOperationException Failure() => new("passwords-decode-failed",
        "A password or note could not be decoded as supported data. No partial credential result was returned.");
}
