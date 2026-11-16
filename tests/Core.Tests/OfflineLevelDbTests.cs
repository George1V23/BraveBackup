using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BraveBackup.Extensions.TabManagerPlus;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class OfflineLevelDbTests
{
    private static string Root => Path.Combine(AppContext.BaseDirectory, "Fixtures", "TabManagerPlus");

    private static Dictionary<string, ReadOnlyMemory<byte>> Fixture(string name = "table-and-wal") =>
        Directory.GetFiles(Path.Combine(Root, "storage", name)).ToDictionary(p => Path.GetFileName(p),
            p => (ReadOnlyMemory<byte>)File.ReadAllBytes(p), StringComparer.Ordinal);

    [Theory]
    [InlineData("table-and-wal")]
    [InlineData("deleted")]
    [InlineData("empty")]
    [InlineData("table-only")]
    [InlineData("fragmented")]
    public void ReadsFrozenUpstreamDatabaseWithoutChangingInputs(string name)
    {
        var files = Fixture(name);
        var before = files.ToDictionary(p => p.Key, p => SHA256.HashData(p.Value.Span));
        using var expected =
            JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "storage", name + ".expected.json")));
        var value = OfflineLevelDb.ReadValue(files, "sessions");
        if (expected.RootElement.GetProperty("found").GetBoolean())
            Assert.Equal(expected.RootElement.GetProperty("value").GetRawText(), Encoding.UTF8.GetString(value!));
        else Assert.Null(value);
        foreach (var file in files) Assert.Equal(before[file.Key], SHA256.HashData(file.Value.Span));
        Assert.Null(OfflineLevelDb.ReadValue(files, "key-not-present"));
        Assert.Equal("\"7.0.0\"", Encoding.UTF8.GetString(OfflineLevelDb.ReadValue(files, "version")!));
    }

    [Fact]
    public void FrozenFilesMatchRecordedHashes()
    {
        using var hashes = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "storage.sha256.json")));
        foreach (var entry in hashes.RootElement.EnumerateObject())
            Assert.Equal(entry.Value.GetString(), Convert.ToHexStringLower(SHA256.HashData(
                File.ReadAllBytes(Path.Combine(Root, "storage", entry.Name)))));
    }

    [Theory]
    [InlineData("CURRENT")]
    [InlineData("MANIFEST-000002")]
    [InlineData("000005.ldb")]
    [InlineData("000004.log")]
    public void MissingLiveFilesFailInsteadOfReturningStaleOrEmpty(string file)
    {
        var files = Fixture();
        Assert.True(files.Remove(file));
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Theory]
    [InlineData("MANIFEST-000002")]
    [InlineData("000005.ldb")]
    [InlineData("000004.log")]
    public void ChangedChecksummedBytesFail(string file)
    {
        var files = Fixture();
        var data = files[file].ToArray();
        data[8] ^= 1;
        files[file] = data;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Theory]
    [InlineData("MANIFEST-000002")]
    [InlineData("000005.ldb")]
    [InlineData("000004.log")]
    public void EveryNonemptyTruncationFails(string file)
    {
        var files = Fixture();
        var original = files[file];
        for (var length = 1; length < original.Length; length++)
        {
            files[file] = original[..length];
            Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
        }
    }

    [Theory]
    [InlineData("../MANIFEST-000002\n")]
    [InlineData("MANIFEST-000002\r\n")]
    [InlineData("MANIFEST-000002")]
    [InlineData("MANIFEST-000000\n")]
    public void RejectsUnsafeOrMalformedCurrent(string current)
    {
        var files = Fixture();
        files["CURRENT"] = Encoding.ASCII.GetBytes(current);
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void IgnoresObsoleteUnreferencedTablesAndOldLogs()
    {
        var files = Fixture();
        var baseline = OfflineLevelDb.ReadValue(files, "sessions");
        files["000001.ldb"] = new byte[] { 255 };
        files["000003.log"] = new byte[] { 255 };
        Assert.Equal(baseline, OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void SstSuffixIsSupportedButDuplicateTableIdentityIsRejected()
    {
        var files = Fixture();
        files["000005.sst"] = files["000005.ldb"];
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
        files.Remove("000005.ldb");
        Assert.NotNull(OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void CancellationStopsBeforeParsing()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => OfflineLevelDb.ReadValue(Fixture(), "sessions", cancel.Token));
    }

    [Theory]
    [InlineData(255)] // Unknown write operation
    [InlineData(0)] // Delete with trailing value bytes
    public void ValidChecksumDoesNotHideInvalidBatch(byte operation)
    {
        var files = Fixture();
        var log = files["000004.log"].ToArray();
        log[19] = operation; // Seven-byte log header plus twelve-byte batch header.
        Rehash(log, 0, log.Length - 7);
        files["000004.log"] = log;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void ConflictingOrReplayedWalSequencesAreRejected()
    {
        var files = Fixture();
        files["000006.log"] = files["000004.log"];
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void UnknownManifestTagFailsEvenWithValidChecksum()
    {
        var files = Fixture();
        var manifest = files["MANIFEST-000002"].ToArray();
        manifest[7] = 127;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(manifest.AsSpan(4));
        Rehash(manifest, 0, length);
        files["MANIFEST-000002"] = manifest;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Theory]
    [InlineData(0)] // A zero output length followed by extra compressed data.
    [InlineData(1)] // An output length too small for the first literal.
    [InlineData(255)] // An over-limit/invalid preamble.
    public void RejectsMalformedSnappyWithValidBlockChecksum(byte firstByte)
    {
        var files = Fixture();
        var table = files["000005.ldb"].ToArray();
        var size = FirstBlockSize(table);
        Assert.Equal(1, table[size]);
        table[0] = firstByte;
        RehashBlock(table, 0, size);
        files["000005.ldb"] = table;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Theory]
    [InlineData(2)] // Zstandard is outside the supported slice.
    [InlineData(255)]
    public void RejectsUnsupportedCompressionWithValidChecksum(byte kind)
    {
        var files = Fixture();
        var table = files["000005.ldb"].ToArray();
        var size = FirstBlockSize(table);
        table[size] = kind;
        RehashBlock(table, 0, size);
        files["000005.ldb"] = table;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void FragmentedRecordsRequireFirstMiddleLastOrder()
    {
        var files = Fixture("fragmented");
        var logName = files.Keys.Single(p => p.EndsWith(".log", StringComparison.Ordinal));
        var log = files[logName].ToArray();
        Assert.Equal(2, log[6]);
        log[6] = 3;
        Rehash(log, 0, BinaryPrimitives.ReadUInt16LittleEndian(log.AsSpan(4)));
        files[logName] = log;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void BatchCountMustMatchRecordsWithValidChecksum()
    {
        var files = Fixture();
        var log = files["000004.log"].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(log.AsSpan(15), 2);
        Rehash(log, 0, log.Length - 7);
        files["000004.log"] = log;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void FileCountAndAggregateByteLimitsAreCheckedBeforeDecode()
    {
        var files = Fixture();
        for (var i = 0; i < 4096; i++) files["extra" + i] = ReadOnlyMemory<byte>.Empty;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
        files = Fixture();
        var sharedBuffer = new byte[4 * 1024 * 1024];
        for (var i = 0; i < 65; i++) files["extra" + i] = sharedBuffer;
        Assert.Throws<InvalidDataException>(() => OfflineLevelDb.ReadValue(files, "sessions"));
    }

    [Fact]
    public void ReturnedValueDoesNotAliasInputOrSubsequentResults()
    {
        var files = Fixture();
        var first = OfflineLevelDb.ReadValue(files, "sessions")!;
        var second = OfflineLevelDb.ReadValue(files, "sessions")!;
        first[0] ^= 1;
        Assert.NotEqual(first[0], second[0]);
        Assert.Equal(second, OfflineLevelDb.ReadValue(files, "sessions"));
    }

    private static int FirstBlockSize(byte[] table)
    {
        var position = table.Length - 48;
        // Skip the footer's metaindex handle and read its index handle.
        ReadVar(table, ref position);
        ReadVar(table, ref position);
        var index = ReadVar(table, ref position);
        Assert.Equal(889, index);
        Assert.Equal(36, ReadVar(table, ref position));
        Assert.Equal(1, table[index + 36]);
        // Frozen fixture: independently decoded with native Snappy, the index's
        // first data handle is (offset=0,size=790). Do not use production decoding
        // to locate the bytes whose malformed handling these tests exercise.
        Assert.Equal(978, table.Length);
        return 790;
    }

    private static int ReadVar(byte[] bytes, ref int position)
    {
        var result = 0;
        for (var shift = 0; shift <= 28; shift += 7)
        {
            var value = bytes[position++];
            result |= (value & 127) << shift;
            if (value < 128) return result;
        }

        throw new InvalidOperationException("Invalid test fixture varint");
    }

    private static void RehashBlock(byte[] table, int start, int size)
    {
        // Reuse the independent CRC routine by adapting block payload+type to log layout.
        var envelope = new byte[size + 7];
        table.AsSpan(start, size + 1).CopyTo(envelope.AsSpan(6));
        Rehash(envelope, 0, size);
        envelope.AsSpan(0, 4).CopyTo(table.AsSpan(start + size + 1));
    }

    // Independent test CRC routine, deliberately not calling the production implementation.
    private static void Rehash(byte[] data, int offset, int length)
    {
        uint crc = 0xffffffff;
        foreach (var value in data.AsSpan(offset + 6, length + 1))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0x82f63b78 ^ (crc >> 1) : crc >> 1;
        }

        crc ^= 0xffffffff;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset),
            unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8));
    }
}