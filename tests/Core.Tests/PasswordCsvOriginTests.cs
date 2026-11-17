using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvOriginTests
{
    [Fact]
    public void MatchesSourceDerivedAsciiFixtureWithoutChangingUrl()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords", "CsvOrigins.synthetic.json")));
        foreach (var row in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var url = row.GetProperty("url").GetString()!;
            var result = BravePasswordCsvOrigin.Project(url);
            Assert.Equal(url, result.Url);
            Assert.Equal(row.GetProperty("name").GetString(), result.Name);
            Assert.Equal(row.GetProperty("groupOrigin").GetString(), result.GroupOrigin);
            Assert.Equal(row.GetProperty("scheme").GetString(), result.Scheme);
            Assert.DoesNotContain("synthetic", result.ToString());
        }
    }

    [Theory]
    [InlineData("https://xn--bcher-kva.invalid/")]
    [InlineData("https://bücher.invalid/")]
    [InlineData("https://[::1]/")]
    [InlineData("android://certificate@package/")]
    [InlineData("file:///synthetic")]
    [InlineData("https://user:secret@synthetic.invalid/")]
    [InlineData("https://Synthetic.invalid/")]
    [InlineData("HTTPS://synthetic.invalid/")]
    [InlineData("https://synthetic.invalid")]
    [InlineData("https://synthetic.invalid:443/")]
    [InlineData("http://synthetic.invalid:80/")]
    [InlineData("https://synthetic.invalid:0/")]
    [InlineData("https://synthetic.invalid:00444/")]
    [InlineData("https://synthetic.invalid:65536/")]
    [InlineData("https://synthetic.invalid:abc/")]
    [InlineData("https://synthetic.invalid./")]
    [InlineData("https://synthetic..invalid/")]
    [InlineData("https://-synthetic.invalid/")]
    [InlineData("https://synthetic-.invalid/")]
    [InlineData("https://127.1/")]
    [InlineData("https://2130706433/")]
    [InlineData("https://0x7f000001/")]
    [InlineData("https://synthetic.0xff/")]
    [InlineData("https://0177.0.0.1/")]
    [InlineData("https://999.0.0.1/")]
    [InlineData("https://synthetic.123/")]
    [InlineData("https://synthetic.invalid/back\\slash")]
    [InlineData("https://synthetic.invalid/space here")]
    [InlineData("https://synthetic.invalid/%")]
    [InlineData("https://synthetic.invalid/%GG")]
    [InlineData("https://synthetic.invalid/./")]
    [InlineData("https://synthetic.invalid/a/../b")]
    [InlineData("https://synthetic.invalid/%2e%2e/b")]
    [InlineData("https://synthetic.invalid/☃")]
    [InlineData("https://synthetic.invalid/\0")]
    public void UnsupportedInputsFailExplicitlyWithoutUrlInError(string url)
    {
        var error = Assert.Throws<BackupOperationException>(() => BravePasswordCsvOrigin.Project(url));
        Assert.Equal("passwords-csv-origin-unsupported", error.Code);
        Assert.DoesNotContain(url, error.Message); Assert.Null(error.InnerException);
    }

    [Fact]
    public void CancellationSizeAndNullInputsAreHandledBeforeProjection()
    {
        Assert.Throws<OperationCanceledException>(() => BravePasswordCsvOrigin.Project("https://synthetic.invalid/", new CancellationToken(true)));
        Assert.Throws<BackupOperationException>(() => BravePasswordCsvOrigin.Project("https://synthetic.invalid/" + new string('a', 1024 * 1024)));
        Assert.Throws<ArgumentNullException>(() => BravePasswordCsvOrigin.Project(null!));
    }
}
