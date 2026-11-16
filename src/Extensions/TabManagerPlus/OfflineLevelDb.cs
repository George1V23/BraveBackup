using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BraveBackup.Extensions.TabManagerPlus;

/// <summary>
/// Reads one key from a captured LevelDB file set without opening a database or accessing
/// the filesystem. Inputs must remain unchanged during the call. Missing/deleted keys
/// return null; malformed, unsupported or over-budget snapshots throw InvalidDataException.
/// This is storage decoding only, not validation of an installed extension or saved windows.
/// </summary>
public static class OfflineLevelDb
{
    public const int MaximumSnapshotBytes = 256 * 1024 * 1024;
    public const int MaximumRecordBytes = 32 * 1024 * 1024;
    private const ulong MaximumSequence = (1UL << 56) - 1;

    /// <summary>Returns an owned copy of the current value, applying manifest state and WAL updates.</summary>
    public static byte[]? ReadValue(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files,
        string key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length > 4096) throw new ArgumentOutOfRangeException(nameof(key));
        return new Reader(files, Encoding.UTF8.GetBytes(key), cancellationToken).Read();
    }

    private static InvalidDataException Invalid() =>
        new("Unsupported, corrupt or over-limit extension storage snapshot.");

    private sealed class Reader
    {
        private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _files;
        private readonly byte[] _wanted;
        private readonly CancellationToken _cancellation;
        private readonly Dictionary<ulong, TableInfo> _tables = new();
        private long _work;
        private long _expanded;
        private long _keyBytes;
        private int _entries;
        private ulong _log, _previousLog, _lastSequence, _nextFile;
        private bool _comparator, _hasLog, _hasSequence, _hasNext;
        private ulong? _winningSequence;
        private byte[]? _value;

        public Reader(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, byte[] wanted,
            CancellationToken cancellation)
        {
            _files = files;
            _wanted = wanted;
            _cancellation = cancellation;
        }

        private void Tick(int count = 1)
        {
            _cancellation.ThrowIfCancellationRequested();
            _work += count;
            if (_work > 4_000_000) throw Invalid();
        }

        private ReadOnlyMemory<byte> File(string name)
        {
            if (!_files.TryGetValue(name, out var data)) throw Invalid();
            return data;
        }

        public byte[]? Read()
        {
            Tick();
            if (_files.Count > 4096) throw Invalid();
            long total = 0;
            foreach (var file in _files)
            {
                if (string.IsNullOrEmpty(file.Key) || file.Key.Length > 128 ||
                    file.Key.Any(c => c is '/' or '\\' or '\0') || file.Key is "." or "..") throw Invalid();
                total += file.Value.Length;
                if (total > MaximumSnapshotBytes) throw Invalid();
            }

            var current = File("CURRENT").Span;
            if (current.Length is < 16 or > 40 || current[^1] != '\n') throw Invalid();
            var manifest = Encoding.ASCII.GetString(current[..^1]);
            if (!manifest.StartsWith("MANIFEST-", StringComparison.Ordinal) ||
                !Number(manifest[9..], out _)) throw Invalid();
            foreach (var record in Logs(File(manifest))) Manifest(record.Span);
            if (!_comparator || !_hasLog || !_hasNext || !_hasSequence || _lastSequence > MaximumSequence ||
                _log >= _nextFile || _previousLog >= _nextFile) throw Invalid();
            foreach (var level in _tables.Values.Where(t => t.Level != 0).GroupBy(t => t.Level))
            {
                TableInfo? previous = null;
                foreach (var table in level.OrderBy(t => t.Smallest,
                             Comparer<byte[]>.Create((a, b) => Compare(a, b))))
                {
                    Tick();
                    if (previous != null && Compare(previous.Largest, table.Smallest) >= 0) throw Invalid();
                    previous = table;
                }
            }

            foreach (var table in _tables.Values)
            {
                Tick();
                var candidates = _files.Keys.Where(n =>
                    (n.EndsWith(".ldb", StringComparison.Ordinal) || n.EndsWith(".sst", StringComparison.Ordinal)) &&
                    Number(n[..^4], out var number) && number == table.Number).ToArray();
                if (candidates.Length != 1) throw Invalid();
                Table(File(candidates[0]), table);
            }

            var logs = new SortedDictionary<ulong, ReadOnlyMemory<byte>>();
            foreach (var file in _files)
            {
                if (!file.Key.EndsWith(".log", StringComparison.Ordinal) ||
                    !Number(file.Key[..^4], out var number) || (number < _log && number != _previousLog)) continue;
                if (!logs.TryAdd(number, file.Value)) throw Invalid();
            }

            // A missing current/previous WAL can hide the latest value: fail closed.
            if ((_log != 0 && !logs.ContainsKey(_log)) ||
                (_previousLog != 0 && !logs.ContainsKey(_previousLog))) throw Invalid();
            ulong? previousEnd = null;
            foreach (var log in logs.Values)
            foreach (var record in Logs(log))
                Batch(record.Span, ref previousEnd);
            return _value;
        }

        private static bool Number(string value, out ulong number)
        {
            number = 0;
            return value.Length is >= 6 and <= 20 && value.All(char.IsAsciiDigit) &&
                   ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number != 0;
        }

        private IEnumerable<ReadOnlyMemory<byte>> Logs(ReadOnlyMemory<byte> data)
        {
            using var partial = new MemoryStream();
            var fragmented = false;
            var offset = 0;
            while (offset < data.Length)
            {
                Tick();
                var remaining = Math.Min(32768 - offset % 32768, data.Length - offset);
                if (remaining < 7)
                {
                    if (data.Span.Slice(offset, remaining).ContainsAnyExcept((byte)0)) throw Invalid();
                    offset += remaining;
                    continue;
                }

                var crc = BinaryPrimitives.ReadUInt32LittleEndian(data.Span[offset..]);
                var length = BinaryPrimitives.ReadUInt16LittleEndian(data.Span[(offset + 4)..]);
                var type = data.Span[offset + 6];
                if (type == 0 && length == 0 && crc == 0)
                {
                    if (data.Span.Slice(offset, remaining).ContainsAnyExcept((byte)0)) throw Invalid();
                    offset += remaining;
                    continue;
                }

                if (length > remaining - 7 || Mask(Crc(data.Span.Slice(offset + 6, length + 1))) != crc)
                    throw Invalid();
                var payload = data.Slice(offset + 7, length);
                offset += 7 + length;
                switch (type)
                {
                    case 1:
                        if (fragmented) throw Invalid();
                        yield return payload;
                        break;
                    case 2:
                        if (fragmented) throw Invalid();
                        partial.SetLength(0);
                        fragmented = true;
                        Append(payload);
                        break;
                    case 3:
                    case 4:
                        if (!fragmented) throw Invalid();
                        Append(payload);
                        if (type == 4)
                        {
                            fragmented = false;
                            yield return partial.ToArray();
                        }

                        break;
                    default: throw Invalid();
                }
            }

            if (fragmented) throw Invalid();

            void Append(ReadOnlyMemory<byte> bytes)
            {
                if (partial.Length + bytes.Length > MaximumRecordBytes) throw Invalid();
                partial.Write(bytes.Span);
            }
        }

        private void Manifest(ReadOnlySpan<byte> data)
        {
            var c = new Cursor(data);
            while (!c.End)
            {
                Tick();
                switch (c.Var())
                {
                    case 1:
                        if (!c.Bytes().SequenceEqual("leveldb.BytewiseComparator"u8)) throw Invalid();
                        _comparator = true;
                        break;
                    case 2:
                        _log = c.Var();
                        _hasLog = true;
                        break;
                    case 3:
                        _nextFile = c.Var();
                        _hasNext = true;
                        break;
                    case 4:
                        _lastSequence = c.Var();
                        _hasSequence = true;
                        break;
                    case 5:
                        Level(c.Var());
                        Internal(c.Bytes());
                        break;
                    case 6:
                        var level = Level(c.Var());
                        var removed = c.Var();
                        if (_tables.TryGetValue(removed, out var old) && old.Level != level) throw Invalid();
                        _tables.Remove(removed);
                        break;
                    case 7:
                        var addedLevel = Level(c.Var());
                        var number = c.Var();
                        var size = c.Var();
                        var smallest = c.Bytes().ToArray();
                        var largest = c.Bytes().ToArray();
                        Internal(smallest);
                        Internal(largest);
                        if (number == 0 || size > MaximumSnapshotBytes || Compare(smallest, largest) > 0 ||
                            !_tables.TryAdd(number, new(number, addedLevel, size, smallest, largest)) ||
                            _tables.Count > 4096) throw Invalid();
                        break;
                    case 9: _previousLog = c.Var(); break;
                    default: throw Invalid();
                }
            }
        }

        private static int Level(ulong level) => level < 7 ? (int)level : throw Invalid();

        private void Batch(ReadOnlySpan<byte> data, ref ulong? previousEnd)
        {
            if (data.Length < 12) throw Invalid();
            var seq = BinaryPrimitives.ReadUInt64LittleEndian(data);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            if (count > 1_000_000 || seq > MaximumSequence || count > MaximumSequence - seq + 1 ||
                (count != 0 && previousEnd.HasValue && seq <= previousEnd.Value)) throw Invalid();
            var c = new Cursor(data[12..]);
            for (uint i = 0; i < count; i++)
            {
                Tick();
                var kind = c.Byte();
                if (kind > 1) throw Invalid();
                var key = c.Bytes();
                var value = kind == 1 ? c.Bytes() : ReadOnlySpan<byte>.Empty;
                Consider(key, seq + i, kind, value);
            }

            if (!c.End) throw Invalid();
            if (count != 0) previousEnd = seq + count - 1;
        }

        private void Consider(ReadOnlySpan<byte> key, ulong sequence, byte kind, ReadOnlySpan<byte> value)
        {
            if (!key.SequenceEqual(_wanted)) return;
            if (_winningSequence == sequence)
            {
                if ((kind == 0) != (_value is null) || (kind == 1 && !value.SequenceEqual(_value))) throw Invalid();
                return;
            }

            if (_winningSequence.HasValue && sequence < _winningSequence.Value) return;
            _winningSequence = sequence;
            _value = kind == 1 ? value.ToArray() : null;
        }

        private void Table(ReadOnlyMemory<byte> file, TableInfo info)
        {
            if ((ulong)file.Length != info.Size || file.Length < 48 || info.Number >= _nextFile ||
                BinaryPrimitives.ReadUInt64LittleEndian(file.Span[^8..]) != 0xdb4775248b80fb57) throw Invalid();
            var footer = new Cursor(file.Span.Slice(file.Length - 48, 40));
            var meta = Handle(ref footer);
            var index = Handle(ref footer);
            if (footer.Remaining.ContainsAnyExcept((byte)0)) throw Invalid();
            var occupied = new List<(int Start, int End)>();
            var metadata = Block(file, meta, occupied);
            // Validate metadata entry framing even though optional filter data is not used for lookup.
            Tick(Entries(metadata).Count);
            var indices = Entries(Block(file, index, occupied));
            byte[]? last = null;
            byte[]? first = null;
            byte[]? previousIndex = null;
            foreach (var entry in indices)
            {
                Tick();
                Internal(entry.Key);
                if (previousIndex != null && Compare(previousIndex, entry.Key) >= 0) throw Invalid();
                previousIndex = entry.Key;
                var cursor = new Cursor(entry.Value.Span);
                var handle = Handle(ref cursor);
                if (!cursor.End) throw Invalid();
                var records = Entries(Block(file, handle, occupied));
                if (records.Count == 0) throw Invalid();
                foreach (var record in records)
                {
                    Tick();
                    var tag = Internal(record.Key);
                    if (last != null && Compare(last, record.Key) >= 0 || tag >> 8 > _lastSequence) throw Invalid();
                    first ??= record.Key;
                    last = record.Key;
                    if ((byte)tag == 0 && record.Value.Length != 0) throw Invalid();
                    Consider(record.Key.AsSpan(0, record.Key.Length - 8), tag >> 8, (byte)tag, record.Value.Span);
                }

                if (Compare(last!, entry.Key) > 0) throw Invalid();
            }

            if (first == null || !first.AsSpan().SequenceEqual(info.Smallest) ||
                !last.AsSpan().SequenceEqual(info.Largest)) throw Invalid();
        }

        private static (ulong Offset, ulong Size) Handle(ref Cursor cursor) => (cursor.Var(), cursor.Var());

        private ReadOnlyMemory<byte> Block(ReadOnlyMemory<byte> file, (ulong Offset, ulong Size) handle,
            List<(int Start, int End)> occupied)
        {
            Tick();
            if (handle.Size > MaximumRecordBytes || handle.Offset > (ulong)(file.Length - 48) ||
                handle.Size + 5 > (ulong)(file.Length - 48) - handle.Offset) throw Invalid();
            var start = (int)handle.Offset;
            var size = (int)handle.Size;
            var end = start + size + 5;
            Tick(occupied.Count);
            if (occupied.Any(range => start < range.End && end > range.Start)) throw Invalid();
            occupied.Add((start, end));
            var stored = file.Slice(start, size);
            var type = file.Span[start + size];
            if (Mask(Crc(file.Span.Slice(start, size + 1))) !=
                BinaryPrimitives.ReadUInt32LittleEndian(file.Span[(start + size + 1)..])) throw Invalid();
            ReadOnlyMemory<byte> decoded = type switch
            {
                0 => stored,
                1 => Snappy(stored.Span),
                _ => throw Invalid()
            };
            _expanded += decoded.Length;
            if (_expanded > MaximumSnapshotBytes) throw Invalid();
            return decoded;
        }

        private List<Entry> Entries(ReadOnlyMemory<byte> memory)
        {
            var bytes = memory.Span;
            if (bytes.Length < 8) throw Invalid();
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[^4..]);
            if (count == 0 || count > (bytes.Length - 4) / 4) throw Invalid();
            var end = bytes.Length - 4 - (int)count * 4;
            var restarts = new HashSet<int>();
            var previous = -1;
            for (var i = 0; i < count; i++)
            {
                Tick();
                var at = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(end + i * 4)..]);
                if (at > end || at <= previous || (i == 0 && at != 0)) throw Invalid();
                restarts.Add((int)at);
                previous = (int)at;
            }

            var c = new Cursor(bytes[..end]);
            var result = new List<Entry>();
            byte[] key = [];
            while (!c.End)
            {
                Tick();
                if (++_entries > 250_000) throw Invalid();
                var at = c.Position;
                var shared = c.Var();
                var unshared = c.Var();
                var size = c.Var();
                if (shared > (ulong)key.Length || unshared > 65536 || shared + unshared > 65536 ||
                    (restarts.Remove(at) && shared != 0)) throw Invalid();
                _keyBytes += (long)(shared + unshared);
                if (_keyBytes > MaximumSnapshotBytes) throw Invalid();
                var newKey = new byte[(int)(shared + unshared)];
                key.AsSpan(0, (int)shared).CopyTo(newKey);
                c.Take((int)unshared).CopyTo(newKey.AsSpan((int)shared));
                if (size > MaximumRecordBytes) throw Invalid();
                var valueOffset = c.Position;
                c.Take((int)size);
                result.Add(new(newKey, memory.Slice(valueOffset, (int)size)));
                key = newKey;
            }

            if (end == 0) restarts.Remove(0);
            if (restarts.Count != 0) throw Invalid();
            return result;
        }

        private byte[] Snappy(ReadOnlySpan<byte> bytes)
        {
            var c = new Cursor(bytes);
            var size = c.Var();
            if (size > MaximumRecordBytes || _expanded + (long)size > MaximumSnapshotBytes) throw Invalid();
            var output = new byte[(int)size];
            var position = 0;
            while (!c.End)
            {
                Tick();
                var tag = c.Byte();
                var kind = tag & 3;
                ulong length;
                if (kind == 0)
                {
                    length = (uint)(tag >> 2) + 1;
                    if (length >= 61)
                    {
                        var count = (int)length - 60;
                        length = 0;
                        for (var i = 0; i < count; i++) length |= (ulong)c.Byte() << (8 * i);
                        length++;
                    }

                    if (length > (ulong)(output.Length - position)) throw Invalid();
                    c.Take((int)length).CopyTo(output.AsSpan(position));
                }
                else
                {
                    length = kind == 1 ? (uint)((tag >> 2) & 7) + 4 : (uint)(tag >> 2) + 1;
                    ulong offset = kind == 1 ? (uint)(tag & 0xe0) << 3 : 0;
                    var count = kind == 3 ? 4 : kind;
                    for (var i = 0; i < count; i++) offset |= (ulong)c.Byte() << (8 * i);
                    if (offset == 0 || offset > (ulong)position || length > (ulong)(output.Length - position))
                        throw Invalid();
                    for (var i = 0; i < (int)length; i++) output[position + i] = output[position + i - (int)offset];
                }

                position += (int)length;
            }

            if (position != output.Length) throw Invalid();
            return output;
        }

        private sealed record TableInfo(ulong Number, int Level, ulong Size, byte[] Smallest, byte[] Largest);

        private sealed record Entry(byte[] Key, ReadOnlyMemory<byte> Value);
    }

    private static ulong Internal(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 8 or > 65536) throw Invalid();
        var tag = BinaryPrimitives.ReadUInt64LittleEndian(key[^8..]);
        if ((byte)tag > 1) throw Invalid();
        return tag;
    }

    private static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var a = Internal(left);
        var b = Internal(right);
        var compare = left[..^8].SequenceCompareTo(right[..^8]);
        return compare != 0 ? compare : b.CompareTo(a);
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0x82f63b78U & (0U - (crc & 1)));
        }

        return ~crc;
    }

    private static uint Mask(uint crc) => unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8U);

    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public int Position { get; private set; }
        public bool End => Position == _data.Length;
        public ReadOnlySpan<byte> Remaining => _data[Position..];
        public byte Byte() => Take(1)[0];

        public ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _data.Length - Position) throw Invalid();
            var value = _data.Slice(Position, count);
            Position += count;
            return value;
        }

        public ulong Var()
        {
            ulong result = 0;
            for (var shift = 0; shift <= 63; shift += 7)
            {
                var value = Byte();
                if (shift == 63 && value > 1) throw Invalid();
                result |= (ulong)(value & 127) << shift;
                if (value < 128) return result;
            }

            throw Invalid();
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var size = Var();
            if (size > MaximumRecordBytes) throw Invalid();
            return Take((int)size);
        }
    }
}