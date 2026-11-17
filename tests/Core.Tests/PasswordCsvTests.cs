using System.Text;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvTests : IDisposable
{
    private readonly List<SensitiveUtf8> _secrets = [];
    private SensitiveUtf8 Secret(string value) { var result = new SensitiveUtf8(Encoding.UTF8.GetBytes(value)); _secrets.Add(result); return result; }
    private PasswordCsvRow Row(string name = "site", string url = "https://synthetic.invalid/", string? username = "user",
        string password = "synthetic-secret", string note = "") => new(name, url, username, Secret(password), Secret(note));
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords");

    [Theory]
    [InlineData("linux", PasswordCsvLineEnding.LinuxLf)]
    [InlineData("windows", PasswordCsvLineEnding.WindowsCrLf)]
    public void MatchesIndependentCsvFixtures(string os, PasswordCsvLineEnding ending)
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Crypto.synthetic.json")));
        var row = Row("Synthetic, \"site\"", "https://example.invalid/login", "fixture-user",
            vectors.RootElement.GetProperty("passwordUtf8").GetString()!, vectors.RootElement.GetProperty("noteUtf8").GetString()!);
        using var encoded = PasswordCsvFormat.Encode(new[] { row }, ending);
        Assert.Equal(File.ReadAllBytes(Path.Combine(Fixtures, $"Passwords.synthetic.{os}.csv")), encoded.Bytes.ToArray());
        Assert.NotEqual((byte)0xef, encoded.Bytes.Span[0]);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("comma,value", "\"comma,value\"")]
    [InlineData("quote\"value", "\"quote\"\"value\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData("a\nb", "\"a\nb\"")]
    [InlineData("a\r\nb", "\"a\r\nb\"")]
    [InlineData("☃\0", "☃\0")]
    [InlineData("=unchanged", "=unchanged")]
    public void QuotesOnlyRequiredCharactersWithoutChangingValues(string raw, string expected)
    {
        using var encoded = PasswordCsvFormat.Encode(new[] { Row("", "", "", raw, "") }, PasswordCsvLineEnding.LinuxLf);
        Assert.Equal("name,url,username,password,note\n,,," + expected + ",\n", Encoding.UTF8.GetString(encoded.Bytes.Span));
    }

    [Fact]
    public void SortsAllColumnsUsingUnsignedUtf8BytesAndKeepsDuplicates()
    {
        var rows = new[]
        {
            Row("\U00010000"), Row("\ue000"),
            Row("a", "b", "b", "b", "b"), Row("a", "b", "b", "b", "a"),
            Row("a", "b", "b", "a", "z"), Row("a", "b", "a", "z", "z"),
            Row("a", "a", "z", "z", "z"), Row("a", "a", "z", "z", "z")
        };
        using var encoded = PasswordCsvFormat.Encode(rows, PasswordCsvLineEnding.LinuxLf);
        var lines = Encoding.UTF8.GetString(encoded.Bytes.Span).Split('\n');
        Assert.Equal(new[] { "a,a,z,z,z", "a,a,z,z,z", "a,b,a,z,z", "a,b,b,a,z", "a,b,b,b,a", "a,b,b,b,b" }, lines.Skip(1).Take(6));
        Assert.StartsWith("\ue000,", lines[7]); Assert.StartsWith("\U00010000,", lines[8]);
        Assert.Equal("\U00010000", rows[0].Name);
    }

    [Theory]
    [InlineData(PasswordCsvLineEnding.LinuxLf, "\n")]
    [InlineData(PasswordCsvLineEnding.WindowsCrLf, "\r\n")]
    public void EmptyCollectionHasHeaderAndFinalTerminator(PasswordCsvLineEnding ending, string separator)
    {
        using var encoded = PasswordCsvFormat.Encode([], ending);
        Assert.Equal("name,url,username,password,note" + separator, Encoding.UTF8.GetString(encoded.Bytes.Span));
    }

    [Fact]
    public void NullUsernameBecomesEmptyAndAllFiveFieldsCanBeQuoted()
    {
        using var encoded = PasswordCsvFormat.Encode(new[] { Row("a,b", "url,part", null, "p,q", "n,o") }, PasswordCsvLineEnding.LinuxLf);
        Assert.Equal("name,url,username,password,note\n\"a,b\",\"url,part\",,\"p,q\",\"n,o\"\n", Encoding.UTF8.GetString(encoded.Bytes.Span));
    }

    [Fact]
    public void PayloadOwnsCopyAndDisposalDoesNotClearCallerSecrets()
    {
        var row = Row();
        using var encoded = PasswordCsvFormat.Encode(new[] { row }, PasswordCsvLineEnding.LinuxLf);
        var borrowed = encoded.Bytes;
        encoded.Dispose();
        Assert.All(borrowed.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal("synthetic-secret", Encoding.UTF8.GetString(row.Password.Utf8.Span));
        Assert.Throws<ObjectDisposedException>(() => encoded.Bytes);
        Assert.DoesNotContain("synthetic", encoded.ToString());
        Assert.DoesNotContain("synthetic", row.ToString());
    }

    [Fact]
    public void InvalidInputAndCancellationNeverProducePayload()
    {
        Assert.Throws<InvalidDataException>(() => PasswordCsvFormat.Encode([], (PasswordCsvLineEnding)99));
        Assert.Throws<InvalidDataException>(() => PasswordCsvFormat.Encode(new[] { Row("\ud800") }, PasswordCsvLineEnding.LinuxLf));
        Assert.Throws<InvalidDataException>(() => PasswordCsvFormat.Encode(new[] { Row(new string('x', 1024 * 1024 + 1)) }, PasswordCsvLineEnding.LinuxLf));
        Assert.Throws<InvalidDataException>(() => PasswordCsvFormat.Encode(new PasswordCsvRow[] { null! }, PasswordCsvLineEnding.LinuxLf));
        Assert.Throws<OperationCanceledException>(() => PasswordCsvFormat.Encode(new[] { Row() }, PasswordCsvLineEnding.LinuxLf, new CancellationToken(true)));
        var disposed = Row(); disposed.Password.Dispose();
        Assert.Throws<ObjectDisposedException>(() => PasswordCsvFormat.Encode(new[] { disposed }, PasswordCsvLineEnding.LinuxLf));
    }

    [Fact]
    public void RowAndEscapedOutputLimitsAreEnforcedBeforeAllocatingPayload()
    {
        var row = Row(password: new string('"', 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => PasswordCsvFormat.Encode(Enumerable.Repeat(row, 33).ToArray(), PasswordCsvLineEnding.LinuxLf));
        Assert.Throws<InvalidDataException>(() => PasswordCsvFormat.Encode(Enumerable.Repeat(Row(), 100001).ToArray(), PasswordCsvLineEnding.LinuxLf));
    }

    public void Dispose() { foreach (var secret in _secrets) secret.Dispose(); }
}
