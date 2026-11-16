using System.Buffers.Binary;
using System.Security.Cryptography;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ArchiveEncryptionTests
{
    private static byte[] Vector => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Envelope.v1.bin"));

    [Fact]
    public void IndependentOpenSslVector_DecryptsAndMatchesWriterBytes()
    {
        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Envelope.v1.txt"));
        var vector = Vector;
        using var reader = ArchiveEncryption.OpenReader(new MemoryStream(vector), "vector-only-Îñ☃");
        using var result = new MemoryStream();
        reader.CopyTo(result);
        Assert.Equal(expected, result.ToArray());
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateVectorWriter(output, "vector-only-Îñ☃", vector[..48])) writer.Write(expected);
        Assert.Equal(vector, output.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1048576)]
    [InlineData(1048577)]
    [InlineData(2097169)]
    public void ChunkBoundaries_RoundTripAndSupportSeeking(int length)
    {
        var data = new byte[length];
        RandomNumberGenerator.Fill(data);
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, "fixture-password"))
        {
            for (var offset = 0; offset < data.Length; offset += 7937)
                writer.Write(data.AsSpan(offset, Math.Min(7937, data.Length - offset)));
        }
        output.Position = 0;
        using var reader = ArchiveEncryption.OpenReader(output, "fixture-password");
        Assert.Equal(length, reader.Length);
        using var restored = new MemoryStream();
        reader.CopyTo(restored);
        Assert.Equal(data, restored.ToArray());
        if (length > 4)
        {
            reader.Seek(-4, SeekOrigin.End);
            var tail = new byte[4]; reader.ReadExactly(tail);
            Assert.Equal(data[^4..], tail);
            reader.Position = 1;
            Assert.Equal(data[1], reader.ReadByte());
        }
    }

    [Fact]
    public void ProductionHeaders_AreFreshAndPasswordIsExact()
    {
        using var first = new MemoryStream();
        using var second = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(first, " space ")) writer.Write("payload"u8);
        using (var writer = ArchiveEncryption.CreateWriter(second, " space ")) writer.Write("payload"u8);
        Assert.NotEqual(first.ToArray()[20..44], second.ToArray()[20..44]);
        first.Position = 0;
        Assert.ThrowsAny<CryptographicException>(() => ArchiveEncryption.OpenReader(first, "space"));
    }

    [Theory]
    [InlineData(0)] [InlineData(8)] [InlineData(10)] [InlineData(11)]
    [InlineData(12)] [InlineData(16)] [InlineData(20)] [InlineData(36)] [InlineData(44)]
    [InlineData(48)] [InlineData(52)] [InlineData(68)]
    public void TamperedHeaderLengthCiphertextOrTag_IsRejected(int offset)
    {
        var bytes = Vector;
        bytes[offset] ^= 0x80;
        Assert.ThrowsAny<Exception>(() => ArchiveEncryption.OpenReader(new MemoryStream(bytes), "vector-only-Îñ☃"));
    }

    [Theory]
    [InlineData(1)] [InlineData(16)] [InlineData(20)] [InlineData(40)] [InlineData(48)]
    public void TruncatedEnvelope_IsRejected(int remove)
    {
        var bytes = Vector;
        Assert.ThrowsAny<Exception>(() => ArchiveEncryption.OpenReader(new MemoryStream(bytes[..^remove]), "vector-only-Îñ☃"));
    }

    [Fact]
    public void MissingTerminalTrailingBytesAndReorderedRecords_AreRejected()
    {
        var bytes = Vector;
        Assert.ThrowsAny<Exception>(() => ArchiveEncryption.OpenReader(new MemoryStream(bytes[..^20]), "vector-only-Îñ☃"));
        Assert.ThrowsAny<Exception>(() => ArchiveEncryption.OpenReader(new MemoryStream(bytes.Concat(new byte[] { 0 }).ToArray()), "vector-only-Îñ☃"));
        var reordered = bytes[..48].Concat(bytes[^20..]).Concat(bytes[48..^20]).ToArray();
        Assert.ThrowsAny<Exception>(() => ArchiveEncryption.OpenReader(new MemoryStream(reordered), "vector-only-Îñ☃"));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(48), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => ArchiveEncryption.OpenReader(new MemoryStream(bytes), "vector-only-Îñ☃"));
    }

    [Fact]
    public void Cancellation_StopsWritingAndReadingWithoutFinalizingASuccessfulEnvelope()
    {
        using var cancellation = new CancellationTokenSource();
        using var output = new MemoryStream();
        var writer = ArchiveEncryption.CreateWriter(output, "fixture", cancellation.Token);
        writer.Write("incomplete"u8);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => writer.Write("more"u8));
        writer.Dispose();
        Assert.Equal(48, output.Length);
        Assert.Throws<OperationCanceledException>(() => ArchiveEncryption.OpenReader(new MemoryStream(Vector), "fixture", cancellation.Token));
    }
}
