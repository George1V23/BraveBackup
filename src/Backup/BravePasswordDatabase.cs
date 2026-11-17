using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;
using Microsoft.Data.Sqlite;

namespace BraveBackup.Core;

/// <summary>One encrypted login and its encrypted notes. ToString deliberately excludes all contents.</summary>
public sealed class StoredPasswordRow(long id, string origin, string realm, string? username, string? displayName,
    byte[] value, IReadOnlyList<StoredPasswordNote> notes)
{
    public long Id { get; } = id;
    public string Origin { get; } = origin;
    public string Realm { get; } = realm;
    public string? Username { get; } = username;
    public string? DisplayName { get; } = displayName;
    public byte[] EncryptedValue { get; } = value;
    public IReadOnlyList<StoredPasswordNote> Notes { get; } = notes;
    public override string ToString() => "Stored password row (contents hidden)";
}

/// <summary>Encrypted source note; it is not converted to an empty note when its ciphertext is unavailable.</summary>
public sealed class StoredPasswordNote(string name, byte[] value, long created, bool confidential)
{
    public string Name { get; } = name;
    public byte[] EncryptedValue { get; } = value;
    public long Created { get; } = created;
    public bool Confidential { get; } = confidential;
    public override string ToString() => "Stored password note (contents hidden)";
}

/// <summary>Owned encrypted rows; disposal clears retained byte buffers but cannot erase managed metadata strings.</summary>
public sealed class StoredPasswordDatabase : IDisposable
{
    private readonly List<byte[]> _buffers;
    private bool _disposed;
    private readonly IReadOnlyList<StoredPasswordRow> _rows;
    public IReadOnlyList<StoredPasswordRow> Rows => !_disposed ? _rows : throw new ObjectDisposedException(nameof(StoredPasswordDatabase));
    public int BlockedEntries { get; }
    public int FederatedEntries { get; }
    internal StoredPasswordDatabase(List<StoredPasswordRow> rows, List<byte[]> buffers, int blocked, int federated)
    { _rows = rows.AsReadOnly(); _buffers = buffers; BlockedEntries = blocked; FederatedEntries = federated; }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var bytes in _buffers) CryptographicOperations.ZeroMemory(bytes);
        _buffers.Clear();
    }
    public override string ToString() => "Stored password database (contents hidden)";
}

/// <summary>Reads the researched v43 schema from private memory only; no OS key access or password decryption occurs.</summary>
public static class BravePasswordDatabase
{
    /// <summary>Validates the complete image and returns owned encrypted HTML-login rows, exclusion counts and notes.</summary>
    public static StoredPasswordDatabase Read(byte[] image, CancellationToken token = default)
    {
        var buffers = new List<byte[]>();
        long budget = 0;
        try
        {
            using var memory = new SqliteMemoryImage(image, token);
            var db = memory.Connection;
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT name,type,rootpage,sql FROM sqlite_schema LIMIT 257";
                using var reader = command.ExecuteReader();
                var objects = 0;
                var tables = new HashSet<string>(StringComparer.Ordinal);
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (++objects > 256 || reader.GetString(1) is "view" or "trigger" ||
                        (reader.GetString(1) == "table" && reader.GetInt64(2) == 0)) throw Invalid();
                    if (reader.GetString(1) == "table") tables.Add(reader.GetString(0));
                }
                if (!new[] { "meta", "logins", "password_notes" }.All(tables.Contains)) throw Invalid();
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check";
                using var reader = command.ExecuteReader();
                if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read()) throw Invalid();
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT key,value FROM meta WHERE key IN ('version','last_compatible_version')";
                using var reader = command.ExecuteReader();
                var versions = new Dictionary<string, string>(StringComparer.Ordinal);
                while (reader.Read())
                    if (reader.GetValue(0) is not string key || reader.GetValue(1) is not string value || !versions.TryAdd(key, value)) throw Invalid();
                if (versions.Count != 2 || versions.GetValueOrDefault("version") != "43" ||
                    versions.GetValueOrDefault("last_compatible_version") != "40")
                    throw new BackupOperationException("passwords-schema-unsupported", "This password database schema is not supported; no credentials were omitted or migrated.");
            }
            var logins = new Dictionary<long, Login>();
            var notes = new Dictionary<long, List<StoredPasswordNote>>();
            var blocked = 0;
            var federated = 0;
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,origin_url,signon_realm,username_value,display_name,password_value,blacklisted_by_user,federation_url,scheme FROM logins ORDER BY id LIMIT 100001";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    if (logins.Count >= 100_000) throw Invalid();
                    var id = Integer(reader, 0);
                    var origin = Text(reader, 1, false)!;
                    var realm = Text(reader, 2, false)!;
                    var username = Text(reader, 3, true);
                    var name = Text(reader, 4, true);
                    var value = Blob(reader, 5, true);
                    var isBlocked = Integer(reader, 6);
                    var federation = Text(reader, 7, true);
                    var scheme = Integer(reader, 8);
                    if (id <= 0 || isBlocked is not (0 or 1)) throw Invalid();
                    var excluded = isBlocked == 1;
                    if (excluded) blocked++;
                    else if (federation is not (null or "" or "null"))
                    {
                        if (!Uri.TryCreate(federation, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw Invalid();
                        excluded = true; federated++;
                    }
                    else if (scheme != 0)
                        throw new BackupOperationException("passwords-scheme-unsupported", "An untested credential scheme requires explicit fallback handling; it was not skipped.");
                    if ((excluded && value.Length != 0) || (!excluded && reader.IsDBNull(5))) throw Invalid();
                    if (!logins.TryAdd(id, new Login(id, origin, realm, username, name, value, excluded))) throw Invalid();
                }
            }
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT id,parent_id,key,value,date_created,confidential FROM password_notes ORDER BY id LIMIT 100001";
                using var reader = command.ExecuteReader();
                var ids = new HashSet<long>();
                var names = new HashSet<(long, string)>();
                while (reader.Read())
                {
                    token.ThrowIfCancellationRequested();
                    var id = Integer(reader, 0); var parent = Integer(reader, 1);
                    var key = Text(reader, 2, false)!; var value = Blob(reader, 3, false);
                    var created = Integer(reader, 4); var confidential = Integer(reader, 5);
                    if (ids.Count >= 100_000 || id <= 0 || !ids.Add(id) || !logins.ContainsKey(parent) ||
                        !names.Add((parent, key)) || confidential is not (0 or 1)) throw Invalid();
                    if (!notes.TryGetValue(parent, out var list)) notes.Add(parent, list = new List<StoredPasswordNote>());
                    list.Add(new StoredPasswordNote(key, value, created, confidential == 1));
                }
            }
            var rows = logins.Values.Where(row => !row.Excluded).Select(row => new StoredPasswordRow(row.Id,
                row.Origin, row.Realm, row.Username, row.Name, row.Value,
                notes.TryGetValue(row.Id, out var list) ? list.AsReadOnly() : Array.Empty<StoredPasswordNote>())).ToList();
            return new StoredPasswordDatabase(rows, buffers, blocked, federated);
        }
        catch (Exception error) when (error is SqliteException or InvalidDataException or InvalidCastException or OverflowException)
        {
            foreach (var bytes in buffers) CryptographicOperations.ZeroMemory(bytes);
            token.ThrowIfCancellationRequested();
            throw Invalid();
        }
        catch { foreach (var bytes in buffers) CryptographicOperations.ZeroMemory(bytes); throw; }

        string? Text(SqliteDataReader reader, int index, bool optional)
        {
            if (optional && reader.IsDBNull(index)) return null;
            if (reader.GetValue(index) is not string text || text.Length > 1024 * 1024) throw Invalid();
            budget += Encoding.UTF8.GetByteCount(text);
            if (budget > 32 * 1024 * 1024) throw Invalid();
            return text;
        }
        byte[] Blob(SqliteDataReader reader, int index, bool nullable)
        {
            if (nullable && reader.IsDBNull(index)) return Array.Empty<byte>();
            if (reader.GetValue(index) is not byte[] bytes) throw Invalid();
            buffers.Add(bytes); budget += bytes.Length;
            if (budget > 32 * 1024 * 1024 || bytes.Length > 4 * 1024 * 1024) throw Invalid();
            return bytes;
        }
    }

    private sealed record Login(long Id, string Origin, string Realm, string? Username, string? Name, byte[] Value, bool Excluded);
    private static long Integer(SqliteDataReader reader, int index) => reader.GetValue(index) is long value ? value : throw Invalid();
    private static BackupOperationException Invalid() => new("passwords-database-invalid", "Password data is corrupt, unsupported or over-limit; no credentials were silently omitted.");
}
