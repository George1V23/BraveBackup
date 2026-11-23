using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;

namespace BraveBackup.Core;

/// <summary>Builds an encrypted replacement image for the native Brave v43 local profile store in private memory.</summary>
public static class BravePasswordRestoreStage
{
    // Exact sqlite_schema definitions captured from a fresh Brave profile, Chromium 154.0.8037.98.
    private const string KnownSchema = "1CEF0E0581E3A033F521472CAF496352828BD2DA0AF632550065645A8A53DD3F";

    /// <summary>Stages a complete portable profile store using an already verified destination v11 key.</summary>
    /// <remarks>
    /// The caller must establish destination provider and Sync state and supply a stable database image
    /// with no outstanding sidecars. This component performs no profile I/O or keyring lookup.
    /// </remarks>
    public static byte[] Prepare(byte[] destination, ReadOnlySpan<byte> portablePayload, PasswordKeyMaterial destinationKey,
        DateTimeOffset created, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(destinationKey);
        token.ThrowIfCancellationRequested();
        if (destinationKey.Provider != PasswordEncryptionProvider.LinuxKeyringV11)
            throw Unsupported("The staged password writer requires a verified destination Secret Service v11 key.");

        try
        {
            using var source = PasswordPayloadFormat.Decode(portablePayload, token);
            if (source.Stores.Count != 1 || source.Stores[0].Kind != PortablePasswordStoreKind.Profile)
                throw Unsupported("Only a portable payload containing a single local profile store can be staged.");
            var logins = source.Stores[0].Logins;
            ValidateIdentities(logins, token);

            var image = SqliteSnapshotImage.Create(destination, ReadOnlySpan<byte>.Empty, token);
            try
            {
                // Validate structure, versions, existing row classes and owned note relationships.
                using var original = BravePasswordDatabase.Read(image, token);
                using var stage = SqliteMemoryImage.OpenWritableCopy(image, token);
                ValidateStagingSettings(stage.Connection);
                ValidateDestination(stage.Connection, token);
                Replace(stage.Connection, logins, destinationKey, created.UtcDateTime.ToFileTimeUtc() / 10, token);
                VerifyDatabase(stage.Connection, token);
                var replacement = stage.CopyCommittedImage();
                try
                {
                    VerifyResult(replacement, original, logins, destinationKey, token);
                    return replacement;
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(replacement);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(image);
            }
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or CryptographicException
                                      or OverflowException or ArgumentOutOfRangeException)
        {
            token.ThrowIfCancellationRequested();
            throw Unsupported("The staged password database could not be prepared and verified.");
        }
    }

    private static void ValidateIdentities(IReadOnlyList<PortablePasswordLogin> logins, CancellationToken token)
    {
        var identities = new HashSet<(string Origin, string Username, string Realm)>();
        foreach (var login in logins)
        {
            token.ThrowIfCancellationRequested();
            PasswordCsvWebOrigin origin;
            try
            {
                origin = BravePasswordCsvOrigin.Project(login.Origin, token);
            }
            catch (BackupOperationException)
            {
                throw Unsupported("A portable login URL is outside tested native restore support.");
            }

            if (login.Realm != $"{origin.Scheme}://{origin.GroupOrigin}/" ||
                login.Username?.Contains('\0') == true || login.DisplayName?.Contains('\0') == true ||
                login.Notes.Any(note => note.Name.Contains('\0')))
                throw Unsupported("A portable login or note is outside tested native restore support.");

            if (!identities.Add((login.Origin, login.Username ?? "", login.Realm)))
                throw Unsupported("Multiple portable logins map to the same native form identity.");
        }
    }

    private static void ValidateStagingSettings(SqliteConnection db)
    {
        if (!Equals(Scalar(db, "PRAGMA encoding"), "UTF-8") ||
            !Equals(Scalar(db, "PRAGMA foreign_keys"), 1L) ||
            !Equals(Scalar(db, "PRAGMA journal_mode"), "memory") ||
            !Equals(Scalar(db, "PRAGMA secure_delete"), 1L))
            throw Unsupported("The destination could not be opened with the required SQLite staging settings.");
    }

    /// <summary>Checks schema, encoding, Sync metadata and integrity using fixed read-only queries.</summary>
    internal static void ValidateDestination(SqliteConnection db, CancellationToken token)
    {
        if (!Equals(Scalar(db, "PRAGMA encoding"), "UTF-8"))
            throw Unsupported("Password restore requires a UTF-8 destination database.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var command = db.CreateCommand())
        {
            command.CommandText = """
                SELECT type,name,coalesce(sql,'') FROM sqlite_schema
                ORDER BY type COLLATE BINARY,name COLLATE BINARY
                """;
            using var rows = command.ExecuteReader();
            while (rows.Read())
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(Encoding.UTF8.GetBytes($"{rows.GetString(0)}\0{rows.GetString(1)}\0{rows.GetString(2)}\n"));
            }
        }
        if (Convert.ToHexString(hash.GetHashAndReset()) != KnownSchema)
            throw Unsupported("The destination database definition differs from the tested Brave v43 schema.");
        if (Scalar(db, "SELECT 1 FROM sync_entities_metadata UNION ALL SELECT 1 FROM sync_model_metadata LIMIT 1") is not null)
            throw Unsupported("The destination contains password Sync metadata.");
        VerifyDatabase(db, token);
    }

    private static void Replace(SqliteConnection db, IReadOnlyList<PortablePasswordLogin> logins,
        PasswordKeyMaterial material, long created, CancellationToken token)
    {
        using var aes = Aes.Create();
        var key = material.CopyKey();
        try { aes.Key = key; }
        finally { CryptographicOperations.ZeroMemory(key); }

        using var transaction = db.BeginTransaction();
        using var delete = db.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM logins WHERE blacklisted_by_user = 0 AND scheme = 0
            AND (federation_url IS NULL OR federation_url IN ('', 'null'))
            """;
        delete.ExecuteNonQuery();

        using var loginCommand = db.CreateCommand();
        loginCommand.Transaction = transaction;
        loginCommand.CommandText = """
            INSERT INTO logins(origin_url, username_element, username_value, password_element, password_value,
                               signon_realm, date_created, blacklisted_by_user, scheme, password_type,
                               federation_url, display_name)
            VALUES ($origin, '', $username, '', $password, $realm, $created, 0, 0, 0, '', $displayName)
            RETURNING id
            """;
        var origin = loginCommand.Parameters.Add("$origin", SqliteType.Text);
        var username = loginCommand.Parameters.Add("$username", SqliteType.Text);
        var password = loginCommand.Parameters.Add("$password", SqliteType.Blob);
        var realm = loginCommand.Parameters.Add("$realm", SqliteType.Text);
        loginCommand.Parameters.AddWithValue("$created", created);
        var displayName = loginCommand.Parameters.Add("$displayName", SqliteType.Text);

        using var noteCommand = db.CreateCommand();
        noteCommand.Transaction = transaction;
        noteCommand.CommandText = """
            INSERT INTO password_notes(parent_id, key, value, date_created, confidential)
            VALUES ($parent, $name, CAST($value AS TEXT), $created, $confidential)
            """;
        var parent = noteCommand.Parameters.Add("$parent", SqliteType.Integer);
        var name = noteCommand.Parameters.Add("$name", SqliteType.Text);
        var value = noteCommand.Parameters.Add("$value", SqliteType.Blob);
        var noteCreated = noteCommand.Parameters.Add("$created", SqliteType.Integer);
        var confidential = noteCommand.Parameters.Add("$confidential", SqliteType.Integer);

        foreach (var login in logins)
        {
            token.ThrowIfCancellationRequested();
            origin.Value = login.Origin;
            username.Value = login.Username ?? "";
            realm.Value = login.Realm;
            displayName.Value = (object?)login.DisplayName ?? DBNull.Value;
            password.Value = Encrypt(aes, login.Password.Utf8.Span);
            parent.Value = (long)loginCommand.ExecuteScalar()!;
            foreach (var note in login.Notes)
            {
                token.ThrowIfCancellationRequested();
                name.Value = note.Name;
                value.Value = Encrypt(aes, note.Value.Utf8.Span);
                noteCreated.Value = note.Created;
                confidential.Value = note.Confidential ? 1 : 0;
                noteCommand.ExecuteNonQuery();
            }
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static byte[] Encrypt(Aes aes, ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty) return [];
        var length = aes.GetCiphertextLengthCbc(plaintext.Length, PaddingMode.PKCS7);
        if (length > SensitiveUtf8.MaximumBytes - 3)
            throw Unsupported("A password or note is too large for the tested destination format.");
        var ciphertext = new byte[length + 3];
        "v11"u8.CopyTo(ciphertext);
        aes.EncryptCbc(plaintext, "                "u8, ciphertext.AsSpan(3), PaddingMode.PKCS7);
        return ciphertext;
    }

    private static void VerifyDatabase(SqliteConnection db, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
                throw Unsupported("The destination password database failed its integrity check.");
        }
        if (Scalar(db, "PRAGMA foreign_key_check") is not null)
            throw Unsupported("The destination password database contains invalid references.");
    }

    private static void VerifyResult(byte[] image, StoredPasswordDatabase original,
        IReadOnlyList<PortablePasswordLogin> expected, PasswordKeyMaterial key, CancellationToken token)
    {
        using var staged = BravePasswordDatabase.Read(image, token);
        if (staged.Rows.Count != expected.Count || staged.BlockedEntries != original.BlockedEntries ||
            staged.FederatedEntries != original.FederatedEntries)
            throw Unsupported("The staged password inventory differs from the requested replacement.");

        for (var index = 0; index < expected.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var login = expected[index];
            var row = staged.Rows[index]; // The reader orders by the destination IDs allocated during insertion.
            using var password = BravePasswordCrypto.Decode(row.EncryptedValue, key, token);
            if (row.Origin != login.Origin || row.Realm != login.Realm || row.Username != (login.Username ?? "") ||
                row.DisplayName != login.DisplayName || row.Notes.Count != login.Notes.Count ||
                !password.Utf8.Span.SequenceEqual(login.Password.Utf8.Span))
                throw Unsupported("A staged login differs from the portable data.");
            for (var noteIndex = 0; noteIndex < login.Notes.Count; noteIndex++)
            {
                var note = login.Notes[noteIndex];
                var stored = row.Notes[noteIndex];
                using var decoded = BravePasswordCrypto.Decode(stored.EncryptedValue, key, token);
                if (stored.Name != note.Name || stored.Created != note.Created || stored.Confidential != note.Confidential ||
                    !decoded.Utf8.Span.SequenceEqual(note.Value.Utf8.Span))
                    throw Unsupported("A staged note differs from the portable data.");
            }
        }
    }

    private static object? Scalar(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static BackupOperationException Unsupported(string message) => new("passwords-stage-unsupported", message);
}
