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
}
