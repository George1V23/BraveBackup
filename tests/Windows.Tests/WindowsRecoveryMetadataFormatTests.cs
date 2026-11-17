using System.Buffers.Binary;
using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class WindowsRecoveryMetadataFormatTests
{
    private static byte[] Fixture()
    {
        var bytes = new byte[56]; "BBKWM001"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), 20);
        bytes[36] = 1; bytes[39] = 0x80;
        return bytes;
    }
    [Fact]
    public void AcceptsBoundedSelfRelativeFramingWithoutNativeAccess() => WindowsRecoveryMetadata.Validate(Fixture());
    [Theory]
    [InlineData("magic")] [InlineData("length")] [InlineData("timestamp")]
    [InlineData("revision")] [InlineData("absolute")] [InlineData("offset")]
    public void RejectsMalformedFraming(string mutation)
    {
        var bytes = Fixture();
        switch (mutation)
        {
            case "magic": bytes[0] ^= 1; break;
            case "length": bytes[32]++; break;
            case "timestamp": BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), -1); break;
            case "revision": bytes[36] = 2; break;
            case "absolute": bytes[39] = 0; break;
            case "offset": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), uint.MaxValue); break;
        }
        Assert.Throws<InvalidDataException>(() => WindowsRecoveryMetadata.Validate(bytes));
    }
    [Fact]
    public void SidAndAclStructuresMustFitTheirDeclaredRegions()
    {
        var bytes = new byte[76]; Fixture().CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), 40);
        var descriptor = bytes.AsSpan(36);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], 20);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], 32);
        descriptor[20] = 1; descriptor[21] = 1;
        descriptor[32] = 2; descriptor[34] = 8;
        WindowsRecoveryMetadata.Validate(bytes);
        bytes[57] = 15; Assert.Throws<InvalidDataException>(() => WindowsRecoveryMetadata.Validate(bytes));
        bytes[57] = 1; bytes[72] = 1;
        Assert.Throws<InvalidDataException>(() => WindowsRecoveryMetadata.Validate(bytes));
    }
}
