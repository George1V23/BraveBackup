using System.Text;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvValidationTests
{
    private const string Header = "name,url,username,password,note";

    [Theory]
    [InlineData(PasswordCsvLineEnding.LinuxLf)]
    [InlineData(PasswordCsvLineEnding.WindowsCrLf)]
    public void EncoderOutputWithUnicodeQuotesAndEmbeddedNewlinesValidates(PasswordCsvLineEnding ending)
    {
        using var password = new SensitiveUtf8(Encoding.UTF8.GetBytes("synthetic,\"é\r\nvalue"));
        using var note = new SensitiveUtf8(Encoding.UTF8.GetBytes("test\nline"));
        using var output = PasswordCsvFormat.Encode([new("name", "https://example.test/", "", password, note)], ending);
        Assert.Equal(1, PasswordCsvValidation.Validate(output.Bytes.Span));
        using var empty = PasswordCsvFormat.Encode([], ending);
        Assert.Equal(0, PasswordCsvValidation.Validate(empty.Bytes.Span));
    }

    [Theory]
    [InlineData("name,url,username,password,note")]
    [InlineData("\uFEFFname,url,username,password,note\n")]
    [InlineData("name,url,username,password,note\r")]
    [InlineData("name,url,username,password,note\n,,,,")]
    [InlineData("name,url,username,password,note\n,,,\n")]
    [InlineData("name,url,username,password,note\n,,,,,\n")]
    [InlineData("name,url,username,password,note\n,,,,\r\n")]
    [InlineData("name,url,username,password,note\r\n,,,,\n")]
    [InlineData("name,url,username,password,note\n,,,ab\"c,\n")]
    [InlineData("name,url,username,password,note\n,,,\"unclosed,\n")]
    [InlineData("name,url,username,password,note\n,,,\"closed\"x,\n")]
    public void MalformedInputIsRejectedWithoutEchoingContents(string input)
    {
        var error = Assert.Throws<InvalidDataException>(() => PasswordCsvValidation.Validate(Encoding.UTF8.GetBytes(input)));
        Assert.Equal("Invalid or unsupported password CSV payload.", error.Message);
    }

    [Fact]
    public void InvalidUtf8LimitsAndCancellationAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => PasswordCsvValidation.Validate(Encoding.UTF8.GetBytes(Header + "\n,,,,").Concat(new byte[] { 0xff, 10 }).ToArray()));
        Assert.Throws<InvalidDataException>(() => PasswordCsvValidation.Validate(Encoding.UTF8.GetBytes(Header + "\n" + new string('a', 1024 * 1024 + 1) + ",,,,\n")));
        Assert.Throws<InvalidDataException>(() => PasswordCsvValidation.Validate(Encoding.UTF8.GetBytes(Header + "\n,,," + new string('a', 4 * 1024 * 1024 + 1) + ",\n")));
        Assert.Throws<InvalidDataException>(() => PasswordCsvValidation.Validate(Encoding.UTF8.GetBytes(Header + "\n" + string.Concat(Enumerable.Repeat(",,,,\n", 100_001)))));
        Assert.Throws<OperationCanceledException>(() => PasswordCsvValidation.Validate(Encoding.UTF8.GetBytes(Header + "\n"), new CancellationToken(true)));
    }
}
