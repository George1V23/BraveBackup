using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>A fresh, synthetic Password Manager entry used to prove the destination's write provider.</summary>
internal sealed class PasswordRestoreChallenge
{
    internal const string Site = "https://bravebackup-restore.invalid/";
    private readonly long _created = TimeProvider.System.GetTimestamp();
    internal bool IsExpired => TimeProvider.System.GetElapsedTime(_created) > TimeSpan.FromMinutes(30);
    internal string Username { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    internal string Password { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public override string ToString() => "Temporary password restore verification";
}

/// <summary>Evidence tied to the verified closed profile database.</summary>
internal sealed record PasswordRestoreSelection(PasswordRestoreChallenge Challenge, string OriginalDigest);

/// <summary>Checks profile prerequisites and prepares the reviewed password replacement.</summary>
internal static class BravePasswordRestoreTarget
{
    internal const string FileName = "Login Data";
    private static readonly string[] Sidecars = ["-journal", "-wal", "-shm"];

    internal static PasswordRestoreSelection Review(string profile, PasswordRestoreChallenge challenge,
        CancellationToken token, Func<string, CancellationToken, byte[]>? lookup = null)
    {
        VerifyContext(profile, token);
        using var original = LinuxRestoreFileCapture.Read(profile, FileName, token);
        using var key = VerifyProvider(original, challenge, token, lookup);
        VerifyContext(profile, token);
        return new(challenge, Digest(original));
    }

    internal static RestoreFileImage Prepare(string profile, RestoreFileImage original, byte[] payload,
        PasswordRestoreSelection selection, CancellationToken token,
        Func<string, CancellationToken, byte[]>? lookup = null)
    {
        VerifyContext(profile, token);
        if (!original.Exists || Digest(original) != selection.OriginalDigest)
            throw Failure("The destination password database changed after Recheck.");
        using var key = VerifyProvider(original, selection.Challenge, token, lookup);
        var input = original.Content.Bytes.ToArray();
        try
        {
            var output = BravePasswordRestoreStage.Prepare(input, payload, key, DateTimeOffset.UtcNow, token);
            try
            {
                VerifyContext(profile, token);
                return new RestoreFileImage(true, output, original.Metadata.Bytes.Span);
            }
            finally { CryptographicOperations.ZeroMemory(output); }
        }
        finally { CryptographicOperations.ZeroMemory(input); }
    }

    /// <summary>Rejects pending SQLite transactions and profiles configured for a Brave Sync chain.</summary>
    internal static void VerifyContext(string profile, CancellationToken token)
    {
        VerifySidecars(profile, token);
        CheckPreferences("Preferences", required: true);
        CheckPreferences(BraveSettingsRestore.SecureFileName, required: false);

        void CheckPreferences(string name, bool required)
        {
            using var snapshot = LinuxRestoreFileCapture.Read(profile, name, token);
            if (!snapshot.Exists)
            {
                if (required) throw Failure("The destination needs readable Preferences before restoring passwords.");
                return;
            }
            using var document = BraveSettingsBackup.ParsePreferences(snapshot.Content.Bytes, token);
            foreach (var path in new[] { "brave_sync_v2.seed", "brave_sync.seed", "brave_sync.v2.seed" })
            {
                if (BraveSettingsBackup.Find(document.RootElement, path) is { } value &&
                    (value.ValueKind != System.Text.Json.JsonValueKind.String || !value.ValueEquals(string.Empty)))
                    throw Failure("Password restore requires a destination outside a Brave Sync chain.");
            }
        }
    }

    /// <summary>Checks sidecars for both forward publication and recovery.</summary>
    internal static void VerifySidecars(string profile, CancellationToken token)
    {
        foreach (var suffix in Sidecars)
        {
            using var sidecar = LinuxRestoreFileCapture.Read(profile, FileName + suffix, token);
            if (sidecar.Exists && sidecar.Content.Bytes.Length != 0)
                throw Failure("The destination password database has a nonempty SQLite sidecar.");
        }
    }

    private static PasswordKeyMaterial VerifyProvider(RestoreFileImage original, PasswordRestoreChallenge challenge,
        CancellationToken token, Func<string, CancellationToken, byte[]>? lookup)
    {
        if (challenge.IsExpired)
            throw Failure("The temporary login expired. Remove it in Brave and generate a new one.");
        if (!original.Exists) throw Failure("No local profile password database was found.");
        var image = original.Content.Bytes.ToArray();
        try
        {
            using var memory = new SqliteMemoryImage(image, token);
            BravePasswordRestoreStage.ValidateDestination(memory.Connection, token);
            using var database = BravePasswordDatabase.Read(image, token);
            return MatchChallenge(database, challenge, token, lookup);
        }
        finally { CryptographicOperations.ZeroMemory(image); }
    }

    private static PasswordKeyMaterial MatchChallenge(StoredPasswordDatabase database, PasswordRestoreChallenge challenge,
        CancellationToken token, Func<string, CancellationToken, byte[]>? lookup)
    {
        var matches = database.Rows.Where(row => row.Origin == PasswordRestoreChallenge.Site &&
            row.Realm == PasswordRestoreChallenge.Site && row.Username == challenge.Username).Take(2).ToArray();
        if (matches.Length != 1)
            throw Failure("Save the temporary login in the mapped profile, close Brave, and run Recheck.");
        if (!matches[0].EncryptedValue.AsSpan().StartsWith("v11"u8))
            throw Failure("Brave did not save the temporary login using the supported Secret Service v11 provider.");
        var key = BraveLinuxPasswordKey.Unlock(lookup, token);
        try
        {
            using var decoded = BravePasswordCrypto.Decode(matches[0].EncryptedValue, key, token);
            if (!CryptographicOperations.FixedTimeEquals(decoded.Utf8.Span, Encoding.ASCII.GetBytes(challenge.Password)))
                throw Failure("The saved temporary login does not match the generated verification login.");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    private static string Digest(RestoreFileImage original)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(original.Metadata.Bytes.Span);
        hash.AppendData(original.Content.Bytes.Span);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static BackupOperationException Failure(string message) => new("restore-password-destination", message);
}
