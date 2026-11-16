using System.Buffers.Binary;

namespace BraveBackup.Core;

/// <summary>Bounds central-directory allocation before the framework materializes untrusted ZIP entries.</summary>
internal static class ArchiveZipGuard
{
    public static void Validate(Stream source, CancellationToken token)
    {
        if (source.Length < 22) throw new InvalidDataException("Truncated ZIP.");
        var tailLength = (int)Math.Min(65557, source.Length);
        var tail = new byte[tailLength];
        source.Position = source.Length - tailLength; source.ReadExactly(tail);
        var end = -1;
        for (var index = tail.Length - 22; index >= 0; index--)
        {
            if (U32(tail, index) == 0x06054b50 && index + 22 + U16(tail, index + 20) == tail.Length)
            { end = index; break; }
        }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0 ||
            U16(tail, end + 8) != U16(tail, end + 10)) throw new InvalidDataException("Unsupported ZIP layout.");
        long count = U16(tail, end + 10);
        long size = U32(tail, end + 12);
        long offset = U32(tail, end + 16);
        var directoryEnd = source.Length - tailLength + end;
        if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
        {
            var locator = new byte[20];
            if (directoryEnd < 20) throw new InvalidDataException("Missing ZIP64 locator.");
            source.Position = directoryEnd - 20; source.ReadExactly(locator);
            if (U32(locator, 0) != 0x07064b50 || U32(locator, 4) != 0 || U32(locator, 16) != 1)
                throw new InvalidDataException("Unsupported ZIP64 layout.");
            var recordOffset = U64(locator, 8);
            if (recordOffset < 0 || recordOffset > directoryEnd - 76) throw new InvalidDataException("Invalid ZIP64 offset.");
            var record = new byte[56]; source.Position = recordOffset; source.ReadExactly(record);
            if (U32(record, 0) != 0x06064b50 || U64(record, 4) < 44 ||
                U64(record, 4) != directoryEnd - 20 - recordOffset - 12 ||
                U32(record, 16) != 0 || U32(record, 20) != 0 || U64(record, 24) != U64(record, 32))
                throw new InvalidDataException("Invalid ZIP64 record.");
            count = U64(record, 32); size = U64(record, 40); offset = U64(record, 48);
            directoryEnd = recordOffset;
        }
        if (count is < 1 or > ArchiveLimits.MaxEntries || size is < 0 or > 64 * 1024 * 1024 ||
            offset < 0 || offset > directoryEnd || size != directoryEnd - offset)
            throw new InvalidDataException("ZIP metadata exceeds supported bounds.");
        source.Position = offset;
        var header = new byte[46];
        long seen = 0;
        while (source.Position < directoryEnd)
        {
            token.ThrowIfCancellationRequested();
            if (++seen > count || directoryEnd - source.Position < header.Length)
                throw new InvalidDataException("Invalid central directory count.");
            source.ReadExactly(header);
            var nameLength = U16(header, 28);
            var remaining = (long)nameLength + U16(header, 30) + U16(header, 32);
            var mode = (U32(header, 38) >> 16) & 0xF000;
            if (U32(header, 0) != 0x02014b50 || nameLength is 0 or > 1024 ||
                U16(header, 34) != 0 || (mode != 0 && mode != 0x8000) ||
                remaining > directoryEnd - source.Position)
                throw new InvalidDataException("Invalid central directory metadata.");
            source.Seek(remaining, SeekOrigin.Current);
        }
        if (seen != count) throw new InvalidDataException("Invalid central directory count.");
        source.Position = 0;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static long U64(byte[] bytes, int offset)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        if (value > long.MaxValue) throw new InvalidDataException("ZIP integer exceeds supported range.");
        return (long)value;
    }
}
