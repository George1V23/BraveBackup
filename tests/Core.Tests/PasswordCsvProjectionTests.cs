using System.Text;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvProjectionTests
{
    private static PortablePasswordLogin Login(long id, string url, string? user = "user", string password = "synthetic-password", string note = "", string noteName = "") =>
        new(id, url, url, user, null, new SensitiveUtf8(Encoding.UTF8.GetBytes(password)),
            [new PortablePasswordNote(noteName, 0, false, new SensitiveUtf8(Encoding.UTF8.GetBytes(note)))]);
    private static PortablePasswordDocument Document(params PortablePasswordLogin[] logins) => new([new PortablePasswordStore(PortablePasswordStoreKind.Profile, 0, 0, logins)]);

    [Fact]
    public void MatchesIndependentPythonProjectionFixture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords", "CsvProjection.synthetic.json")));
        var entries = fixture.RootElement.GetProperty("logins").EnumerateArray().ToArray();
        var groups = entries.ToDictionary(row => row.GetProperty("id").GetInt64(), row => row.GetProperty("group").GetString());
        using var document = Document(entries.Select(row => Login(row.GetProperty("id").GetInt64(), row.GetProperty("url").GetString()!,
            row.GetProperty("username").GetString(), row.GetProperty("password").GetString()!, row.GetProperty("note").GetString()!)).ToArray());
        using var projected = BravePasswordCsvProjection.Create(document, login => groups[login.Id]);
        using var csv = PasswordCsvFormat.Encode(projected.Rows, PasswordCsvLineEnding.LinuxLf);
        Assert.Equal(fixture.RootElement.GetProperty("expectedCsv").GetString(), Encoding.UTF8.GetString(csv.Bytes.Span));
    }

    [Fact]
    public void VerifiedGroupCombinesBothStoresAndDistinctCapturedUrls()
    {
        var first = Login(1, "https://a.synthetic.invalid/login", note: "z");
        var duplicate = Login(2, first.Origin, note: "a");
        var other = Login(1, "https://b.synthetic.invalid/login", note: "z");
        using var document = new PortablePasswordDocument([
            new PortablePasswordStore(PortablePasswordStoreKind.Profile, 0, 0, [first, duplicate]),
            new PortablePasswordStore(PortablePasswordStoreKind.Account, 0, 0, [other])]);
        using var result = BravePasswordCsvProjection.Create(document, _ => "verified-synthetic-group");
        Assert.Equal(3, result.SourceLoginCount); Assert.Equal(1, result.CredentialGroupCount); Assert.Equal(2, result.Rows.Count);
        Assert.Equal(new[] { first.Origin, other.Origin }, result.Rows.Select(row => row.Url));
        Assert.All(result.Rows, row => Assert.Equal("a\nz", Encoding.UTF8.GetString(row.Note.Utf8.Span)));
        using var csv = PasswordCsvFormat.Encode(result.Rows, PasswordCsvLineEnding.LinuxLf);
        Assert.Contains("a.synthetic.invalid,https://a.synthetic.invalid/login,user,synthetic-password,\"a\nz\"", Encoding.UTF8.GetString(csv.Bytes.Span));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("username")]
    [InlineData("password")]
    public void DoesNotMergeDifferentGroupUsernameOrPassword(string difference)
    {
        using var document = Document(Login(1, "https://synthetic.invalid/a"), Login(2, "https://synthetic.invalid/b",
            user: difference == "username" ? "different" : "user", password: difference == "password" ? "different" : "synthetic-password"));
        using var result = BravePasswordCsvProjection.Create(document, row => difference == "identity" ? row.Id.ToString() : "same-verified-group");
        Assert.Equal(2, result.CredentialGroupCount); Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void NullAndEmptyUsernameMatchBrowserEmptyValue()
    {
        using var document = Document(Login(1, "https://synthetic.invalid/", user: null), Login(2, "https://synthetic.invalid/", user: ""));
        using var result = BravePasswordCsvProjection.Create(document, _ => "verified-group");
        Assert.Equal(1, result.CredentialGroupCount); Assert.Equal("", Assert.Single(result.Rows).Username);
    }

    [Fact]
    public void MissingResolverOrAnyUnresolvedCredentialFailsWithoutPartialOutput()
    {
        using var document = Document(Login(1, "https://synthetic.invalid/a"), Login(2, "https://synthetic.invalid/b"));
        Assert.Equal("passwords-csv-grouping-unavailable", Assert.Throws<BackupOperationException>(() => BravePasswordCsvProjection.Create(document, null)).Code);
        Assert.Throws<BackupOperationException>(() => BravePasswordCsvProjection.Create(document, row => row.Id == 1 ? "known" : null));
    }

    [Fact]
    public void UnsupportedOriginOrNamedNoteDoesNotSilentlyDropCredential()
    {
        using var origins = Document(Login(1, "https://synthetic.invalid/"), Login(2, "https://xn--bcher-kva.invalid/"));
        Assert.Equal("passwords-csv-origin-unsupported", Assert.Throws<BackupOperationException>(() => BravePasswordCsvProjection.Create(origins, _ => "group")).Code);
        using var notes = Document(Login(1, "https://synthetic.invalid/", note: "synthetic-secret-note", noteName: "named"));
        Assert.Equal("passwords-csv-notes-unsupported", Assert.Throws<BackupOperationException>(() => BravePasswordCsvProjection.Create(notes, _ => "group")).Code);
    }

    [Fact]
    public void ProjectionCopiesSecretsAndClearsEveryBorrowedRowValue()
    {
        using var document = Document(Login(1, "https://a.synthetic.invalid/", note: "note"), Login(2, "https://b.synthetic.invalid/", note: "note"));
        using var result = BravePasswordCsvProjection.Create(document, _ => "verified-group");
        var password = result.Rows[0].Password.Utf8; var note = result.Rows[1].Note.Utf8;
        document.Dispose(); Assert.Equal("synthetic-password", Encoding.UTF8.GetString(password.Span));
        result.Dispose(); Assert.All(password.ToArray(), b => Assert.Equal(0, b)); Assert.All(note.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => result.Rows);
        Assert.DoesNotContain("synthetic", result.ToString());
    }

    [Fact]
    public void EmptyKnownCollectionRequiresNoResolverCalls()
    {
        using var document = Document();
        using var result = BravePasswordCsvProjection.Create(document, _ => throw new Exception("Must not resolve"));
        Assert.Empty(result.Rows); Assert.Equal(0, result.CredentialGroupCount);
    }

    [Fact]
    public void CancellationBeforeOrDuringResolutionStopsProjection()
    {
        using var document = Document(Login(1, "https://synthetic.invalid/"));
        Assert.Throws<OperationCanceledException>(() => BravePasswordCsvProjection.Create(document, _ => "group", new CancellationToken(true)));
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => BravePasswordCsvProjection.Create(document, _ => { cancellation.Cancel(); return "group"; }, cancellation.Token));
    }    [Fact]
    public void PathologicalPasswordBucketAndOversizedScopeAreBounded()
    {
        using var largeBucket = Document(Enumerable.Range(1, 1500).Select(index => Login(index, "https://synthetic.invalid/", password: "synthetic-" + index)).ToArray());
        Assert.Equal("passwords-csv-projection-limit", Assert.Throws<BackupOperationException>(() => BravePasswordCsvProjection.Create(largeBucket, _ => "verified-group")).Code);
        var login = Login(1, "https://synthetic.invalid/");
        using var oversized = Document(Enumerable.Repeat(login, 100001).ToArray());
        Assert.Equal("passwords-csv-projection-limit", Assert.Throws<BackupOperationException>(() => BravePasswordCsvProjection.Create(oversized, _ => throw new Exception("Must not resolve"))).Code);
    }

}
