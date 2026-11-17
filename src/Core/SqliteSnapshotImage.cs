using System.Buffers.Binary;
using System.Security.Cryptography;

namespace BraveBackup.Core;

/// <summary>
/// Builds an owned SQLite image from already stable captured bytes using the documented
/// WAL framing/checksum rules. This does not establish consistency of a live file copy.
/// Unsupported/truncated/stale WAL tails fail conservatively instead of being skipped.
/// </summary>
public static class SqliteSnapshotImage
{
    public const int MaximumBytes = 64 * 1024 * 1024;

    /// <summary>Applies frames through the last complete commit; valid uncommitted tail frames are excluded.</summary>
    public static byte[] Create(ReadOnlySpan<byte> database, ReadOnlySpan<byte> wal,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (database.Length is < 512 or > MaximumBytes || !database[..16].SequenceEqual("SQLite format 3\0"u8)) throw Invalid();
        var pageSize = BinaryPrimitives.ReadUInt16BigEndian(database[16..]);
        var size = pageSize == 1 ? 65536 : pageSize;
        if (size is < 512 or > 65536 || (size & (size - 1)) != 0 || database.Length % size != 0 ||
            database[18] is not (1 or 2) || database[19] is not (1 or 2)) throw Invalid();
        if (wal.IsEmpty) return database.ToArray();
        if (database[18] != 2 || database[19] != 2 || wal.Length is < 32 or > MaximumBytes ||
            (wal.Length - 32) % (24 + size) != 0) throw Invalid();
        var magic = Big(wal);
        if (magic is not (0x377f0682 or 0x377f0683) || Big(wal[4..]) != 3007000 || Big(wal[8..]) != size) throw Invalid();
        var bigEndian = (magic & 1) != 0;
        uint a = 0, b = 0;
        Checksum(wal[..24], bigEndian, ref a, ref b, token);
        if (a != Big(wal[24..]) || b != Big(wal[28..])) throw Invalid();
        var lastCommit = 32;
        var pages = database.Length / size;
        var frames = 0;
        for (var offset = 32; offset < wal.Length; offset += 24 + size)
        {
            token.ThrowIfCancellationRequested();
            var header = wal.Slice(offset, 24);
            var page = Big(header);
            var commitPages = Big(header[4..]);
            if (++frames > 100_000 || page == 0 || page > MaximumBytes / size ||
                commitPages > MaximumBytes / size || !header.Slice(8, 8).SequenceEqual(wal.Slice(16, 8))) throw Invalid();
            Checksum(header[..8], bigEndian, ref a, ref b, token);
            Checksum(wal.Slice(offset + 24, size), bigEndian, ref a, ref b, token);
            if (a != Big(header[16..]) || b != Big(header[20..])) throw Invalid();
            if (commitPages != 0)
            {
                pages = (int)commitPages;
                lastCommit = offset + 24 + size;
            }
        }
        var result = new byte[checked(pages * size)];
        try
        {
            database[..Math.Min(result.Length, database.Length)].CopyTo(result);
            var present = new bool[pages];
            Array.Fill(present, true, 0, Math.Min(pages, database.Length / size));
            for (var offset = 32; offset < lastCommit; offset += 24 + size)
            {
                token.ThrowIfCancellationRequested();
                var page = (int)Big(wal[offset..]);
                if (page > pages) continue;
                wal.Slice(offset + 24, size).CopyTo(result.AsSpan((page - 1) * size, size));
                present[page - 1] = true;
            }
            if (present.Any(value => !value)) throw Invalid();
            return result;
        }
        catch { CryptographicOperations.ZeroMemory(result); throw; }
    }

    private static uint Big(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt32BigEndian(bytes);
    private static void Checksum(ReadOnlySpan<byte> bytes, bool big, ref uint a, ref uint b, CancellationToken token)
    {
        for (var i = 0; i < bytes.Length; i += 8)
        {
            if (i % 65536 == 0) token.ThrowIfCancellationRequested();
            var x = big ? Big(bytes[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]);
            var y = big ? Big(bytes[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[(i + 4)..]);
            a = unchecked(a + x + b);
            b = unchecked(b + y + a);
        }
    }
    private static InvalidDataException Invalid() => new("Unsupported, corrupt or over-limit SQLite snapshot.");
}
