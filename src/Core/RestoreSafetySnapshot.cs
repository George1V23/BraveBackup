using System.Security.Cryptography;
using System.Text;

namespace BraveBackup.Core;

/// <summary>Owned original or replacement image; absence is distinct from an existing empty file.</summary>
public sealed class RestoreFileImage : IDisposable
{
    public bool Exists { get; }
    public SensitiveBytes Content { get; }
    public SensitiveBytes Metadata { get; }
    public RestoreFileImage(bool exists, ReadOnlySpan<byte> content, ReadOnlySpan<byte> metadata = default)
    {
        if (content.Length > 64 * 1024 * 1024 || metadata.Length > 65536 || !exists && (!content.IsEmpty || !metadata.IsEmpty))
            throw new InvalidDataException("Invalid restore file image.");
        Exists = exists; Content = new SensitiveBytes(content.ToArray()); Metadata = new SensitiveBytes(metadata.ToArray());
    }
    /// <summary>Copies borrowed image bytes into an independently owned image.</summary>
    public RestoreFileImage Clone() => new(Exists, Content.Bytes.Span, Metadata.Bytes.Span);
    public void Dispose() { Content.Dispose(); Metadata.Dispose(); }
    public override string ToString() => "Restore file image (contents hidden)";
}
/// <summary>One frozen logical identity and its owned original file image; contains no executable destination path.</summary>
public sealed record RestoreSafetyEntry(Guid Id, Guid InstallationId, Guid ProfileId, string Category, RestoreFileImage Original);
/// <summary>Owns original images for one operation; disposal clears content and opaque metadata.</summary>
public sealed class RestoreSafetySnapshot(Guid operationId, IReadOnlyList<RestoreSafetyEntry> entries) : IDisposable
{
    public Guid OperationId { get; } = operationId;
    public IReadOnlyList<RestoreSafetyEntry> Entries { get; } = entries.ToArray();
    public void Dispose() { foreach (var entry in Entries) entry.Original.Dispose(); }
}
/// <summary>Bounded application-owned recovery snapshot encoding; never authorizes a write or stores plaintext on disk.</summary>
public static class RestoreSafetyFormat
{
    public const int MaximumBytes = 128 * 1024 * 1024;
    /// <summary>Serializes original bytes/absence and opaque metadata into owned plaintext for subsequent encryption.</summary>
    public static SensitiveBytes Encode(RestoreSafetySnapshot snapshot, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        ValidateIdentity(snapshot.OperationId, snapshot.Entries.Select(e => e.Id));
        long length = 28;
        foreach (var entry in snapshot.Entries)
        {
            ValidateEntry(entry);
            length += 61L + entry.Category.Length + entry.Original.Content.Bytes.Length + entry.Original.Metadata.Bytes.Length;
            if (length > MaximumBytes) throw Invalid();
        }
        var bytes = new byte[(int)length];
        try
        {
            using var output = new MemoryStream(bytes, writable: true); using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
            writer.Write("BBKSF001"u8); writer.Write(snapshot.OperationId.ToByteArray()); writer.Write(snapshot.Entries.Count);
            foreach (var entry in snapshot.Entries)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(entry.Id.ToByteArray()); writer.Write(entry.InstallationId.ToByteArray()); writer.Write(entry.ProfileId.ToByteArray());
                writer.Write(entry.Category.Length); writer.Write(Encoding.ASCII.GetBytes(entry.Category)); writer.Write(entry.Original.Exists);
                writer.Write(entry.Original.Content.Bytes.Length); writer.Write(entry.Original.Content.Bytes.Span);
                writer.Write(entry.Original.Metadata.Bytes.Length); writer.Write(entry.Original.Metadata.Bytes.Span);
            }
            token.ThrowIfCancellationRequested(); return new SensitiveBytes(bytes);
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }
    /// <summary>Decodes exact bounded plaintext, rejecting duplicate identities, malformed lengths and unknown framing.</summary>
    public static RestoreSafetySnapshot Decode(ReadOnlySpan<byte> plaintext, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); if (plaintext.Length > MaximumBytes) throw Invalid();
        var bytes = plaintext.ToArray(); var entries = new List<RestoreSafetyEntry>();
        try
        {
            using var input = new MemoryStream(bytes, writable: false); using var reader = new BinaryReader(input);
            if (!Read(reader, 8).AsSpan().SequenceEqual("BBKSF001"u8)) throw Invalid();
            var operation = new Guid(Read(reader, 16)); var count = reader.ReadInt32();
            if (count is < 1 or > 10000) throw Invalid();
            for (var index = 0; index < count; index++)
            {
                token.ThrowIfCancellationRequested();
                var id = new Guid(Read(reader, 16)); var installation = new Guid(Read(reader, 16)); var profile = new Guid(Read(reader, 16));
                var categoryLength = reader.ReadInt32(); if (categoryLength is < 1 or > 64) throw Invalid();
                var categoryBytes = Read(reader, categoryLength);
                if (categoryBytes.Any(c => c is not (>= (byte)'a' and <= (byte)'z') and not (>= (byte)'0' and <= (byte)'9') and not (byte)'-')) throw Invalid();
                var category = Encoding.ASCII.GetString(categoryBytes);
                var exists = reader.ReadByte(); if (exists > 1) throw Invalid();
                var contentLength = reader.ReadInt32(); if (contentLength is < 0 or > 64 * 1024 * 1024) throw Invalid();
                var content = Read(reader, contentLength); byte[]? metadata = null;
                try
                {
                    var metadataLength = reader.ReadInt32(); if (metadataLength is < 0 or > 65536) throw Invalid();
                    metadata = Read(reader, metadataLength);
                    var entry = new RestoreSafetyEntry(id, installation, profile, category, new RestoreFileImage(exists == 1, content, metadata));
                    entries.Add(entry); ValidateEntry(entry);
                }
                finally { CryptographicOperations.ZeroMemory(content); if (metadata is not null) CryptographicOperations.ZeroMemory(metadata); }
            }
            if (input.Position != input.Length) throw Invalid();
            ValidateIdentity(operation, entries.Select(e => e.Id));
            return new(operation, entries);
        }
        catch (Exception error) when (error is EndOfStreamException or ArgumentException or OverflowException)
        { foreach (var entry in entries) entry.Original.Dispose(); throw Invalid(); }
        catch { foreach (var entry in entries) entry.Original.Dispose(); throw; }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private static byte[] Read(BinaryReader reader, int length)
    {
        if (length > reader.BaseStream.Length - reader.BaseStream.Position) throw Invalid();
        return reader.ReadBytes(length);
    }
    private static void ValidateEntry(RestoreSafetyEntry entry)
    {
        if (entry.InstallationId == Guid.Empty || entry.ProfileId == Guid.Empty || entry.Category is null || entry.Category.Length is < 1 or > 64 ||
            entry.Category.Any(c => c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-')) throw Invalid();
        if (entry.Original.Content.Bytes.Length > 64 * 1024 * 1024 || entry.Original.Metadata.Bytes.Length > 65536 ||
            !entry.Original.Exists && (!entry.Original.Content.Bytes.IsEmpty || !entry.Original.Metadata.Bytes.IsEmpty)) throw Invalid();
    }
    internal static void ValidateIdentity(Guid operation, IEnumerable<Guid> identities)
    {
        var ids = identities.ToArray();
        if (operation == Guid.Empty || ids.Length is < 1 or > 10000 || ids.Any(id => id == Guid.Empty) || ids.Distinct().Count() != ids.Length) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid restore safety snapshot.");
}
