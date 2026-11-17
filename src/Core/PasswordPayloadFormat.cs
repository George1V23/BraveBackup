using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Bounded version-1 portable password payload. Bytes are plaintext until an archive encrypts them.</summary>
public static class PasswordPayloadFormat
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    private const int MaximumTextBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Validates completely before allocating an exact-size owned buffer. Performs no filesystem access.</summary>
    public static SensitiveBytes Encode(PortablePasswordDocument document, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        token.ThrowIfCancellationRequested();
        var size = Validate(document, token);
        var bytes = new byte[size];
        try
        {
            var writer = new Writer(bytes);
            writer.Raw("BBKPW001"u8);
            writer.Int(document.Stores.Count);
            foreach (var store in document.Stores)
            {
                token.ThrowIfCancellationRequested();
                writer.Int((int)store.Kind); writer.Int(store.BlockedEntries); writer.Int(store.FederatedEntries); writer.Int(store.Logins.Count);
                foreach (var login in store.Logins)
                {
                    token.ThrowIfCancellationRequested();
                    writer.Long(login.Id); writer.Text(login.Origin); writer.Text(login.Realm);
                    writer.Text(login.Username); writer.Text(login.DisplayName); writer.Blob(login.Password.Utf8.Span);
                    writer.Int(login.Notes.Count);
                    foreach (var note in login.Notes)
                    {
                        token.ThrowIfCancellationRequested();
                        writer.Text(note.Name); writer.Long(note.Created); writer.Int(note.Confidential ? 1 : 0); writer.Blob(note.Value.Utf8.Span);
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            return new SensitiveBytes(bytes);
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    /// <summary>Reads one exact payload, rejecting unknown versions, duplicate identities, malformed data and trailing bytes.</summary>
    public static PortablePasswordDocument Decode(ReadOnlySpan<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumBytes) throw Invalid();
        var owned = new List<SensitiveUtf8>();
        try
        {
            var reader = new Reader(bytes);
            if (!reader.Raw(8).SequenceEqual("BBKPW001"u8)) throw Invalid();
            var count = reader.Count(2);
            if (count == 0) throw Invalid();
            var stores = new List<PortablePasswordStore>();
            var totalRows = 0;
            var totalNotes = 0;
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var kind = (PortablePasswordStoreKind)reader.Int();
                var blocked = reader.Count(100_000);
                var federated = reader.Count(100_000);
                var rowCount = reader.Count(100_000);
                totalRows = checked(totalRows + rowCount + blocked + federated);
                if (totalRows > 100_000) throw Invalid();
                var logins = new List<PortablePasswordLogin>();
                for (var row = 0; row < rowCount; row++)
                {
                    token.ThrowIfCancellationRequested();
                    var id = reader.Long();
                    var origin = reader.Text() ?? throw Invalid(); var realm = reader.Text() ?? throw Invalid();
                    var username = reader.Text(); var name = reader.Text();
                    var password = reader.Secret(owned);
                    var noteCount = reader.Count(100_000);
                    totalNotes += noteCount;
                    if (totalNotes > 100_000) throw Invalid();
                    var notes = new List<PortablePasswordNote>();
                    for (var note = 0; note < noteCount; note++)
                    {
                        token.ThrowIfCancellationRequested();
                        var noteName = reader.Text() ?? throw Invalid(); var created = reader.Long();
                        var confidential = reader.Int();
                        if (confidential is not (0 or 1)) throw Invalid();
                        notes.Add(new PortablePasswordNote(noteName, created, confidential == 1, reader.Secret(owned)));
                    }
                    logins.Add(new PortablePasswordLogin(id, origin, realm, username, name, password, notes));
                }
                stores.Add(new PortablePasswordStore(kind, blocked, federated, logins));
            }
            if (!reader.Finished) throw Invalid();
            var document = new PortablePasswordDocument(stores);
            _ = Validate(document, token);
            return document;
        }
        catch (Exception error) when (error is DecoderFallbackException or OverflowException)
        { foreach (var value in owned) value.Dispose(); throw Invalid(); }
        catch { foreach (var value in owned) value.Dispose(); throw; }
    }

    private static int Validate(PortablePasswordDocument document, CancellationToken token)
    {
        long size = 12, secrets = 0;
        var rows = 0; var notes = 0;
        if (document.Stores.Count is < 1 or > 2) throw Invalid();
        var kinds = new HashSet<PortablePasswordStoreKind>();
        foreach (var store in document.Stores)
        {
            token.ThrowIfCancellationRequested();
            if (store.Kind is not (PortablePasswordStoreKind.Profile or PortablePasswordStoreKind.Account) || !kinds.Add(store.Kind) ||
                store.BlockedEntries < 0 || store.FederatedEntries < 0) throw Invalid();
            var rowTotal = (long)rows + store.Logins.Count + store.BlockedEntries + store.FederatedEntries;
            if (rowTotal > 100_000) throw Invalid();
            rows = (int)rowTotal; Add(16);
            var ids = new HashSet<long>();
            foreach (var login in store.Logins)
            {
                token.ThrowIfCancellationRequested();
                if (login.Id <= 0 || !ids.Add(login.Id) || login.Origin is null || login.Realm is null) throw Invalid();
                Add(12); Text(login.Origin); Text(login.Realm); Text(login.Username); Text(login.DisplayName); Secret(login.Password);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var note in login.Notes)
                {
                    token.ThrowIfCancellationRequested();
                    if (++notes > 100_000 || note.Name is null || !names.Add(note.Name)) throw Invalid();
                    Text(note.Name); Add(12); Secret(note.Value);
                }
            }
        }
        return (int)size;

        void Add(long amount) { size += amount; if (size > MaximumBytes) throw Invalid(); }
        void Text(string? value)
        {
            int length;
            try { length = value is null ? 0 : Utf8.GetByteCount(value); }
            catch (EncoderFallbackException) { throw Invalid(); }
            if (length > MaximumTextBytes) throw Invalid();
            Add(4L + length);
        }
        void Secret(SensitiveUtf8 value)
        {
            var length = value.Utf8.Length;
            secrets += length;
            if (secrets > 32L * 1024 * 1024) throw Invalid();
            Add(4L + length);
        }
    }

    private ref struct Writer(Span<byte> bytes)
    {
        private Span<byte> _remaining = bytes;
        public void Raw(ReadOnlySpan<byte> value) { value.CopyTo(_remaining); _remaining = _remaining[value.Length..]; }
        public void Int(int value) { BinaryPrimitives.WriteInt32LittleEndian(_remaining, value); _remaining = _remaining[4..]; }
        public void Long(long value) { BinaryPrimitives.WriteInt64LittleEndian(_remaining, value); _remaining = _remaining[8..]; }
        public void Blob(ReadOnlySpan<byte> value) { Int(value.Length); Raw(value); }
        public void Text(string? value)
        {
            if (value is null) { Int(-1); return; }
            var length = Utf8.GetByteCount(value); Int(length);
            _ = Utf8.GetBytes(value, _remaining); _remaining = _remaining[length..];
        }
    }
    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;
        public bool Finished => _remaining.IsEmpty;
        public ReadOnlySpan<byte> Raw(int length)
        {
            if (length < 0 || length > _remaining.Length) throw Invalid();
            var value = _remaining[..length]; _remaining = _remaining[length..]; return value;
        }
        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Raw(4));
        public long Long() => BinaryPrimitives.ReadInt64LittleEndian(Raw(8));
        public int Count(int maximum) { var value = Int(); if (value < 0 || value > maximum) throw Invalid(); return value; }
        public string? Text()
        {
            var length = Int(); if (length == -1) return null;
            if (length < 0 || length > MaximumTextBytes) throw Invalid();
            return Utf8.GetString(Raw(length));
        }
        public SensitiveUtf8 Secret(List<SensitiveUtf8> owned)
        {
            var length = Count(SensitiveUtf8.MaximumBytes);
            var value = new SensitiveUtf8(Raw(length)); owned.Add(value); return value;
        }
    }
    private static InvalidDataException Invalid() => new("Portable password payload is invalid, unsupported or over-limit.");
}
