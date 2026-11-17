using System.Text;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCsvNoteTests : IDisposable
{
    private readonly List<SensitiveUtf8> _values = [];
    private SensitiveUtf8 Secret(string text) { var value = new SensitiveUtf8(Encoding.UTF8.GetBytes(text)); _values.Add(value); return value; }
    private PortablePasswordLogin Login(params (string Name, string Value)[] notes) => new(1, "https://synthetic.invalid/",
        "https://synthetic.invalid/", "synthetic-user", null, Secret("synthetic-password"),
        notes.Select(note => new PortablePasswordNote(note.Name, 0, false, Secret(note.Value))));

    [Fact]
    public void MatchesIndependentPythonUtf16SetFixture()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords", "CsvNotes.synthetic.json")));
        var groups = fixture.RootElement.GetProperty("groups").EnumerateArray().Select(group =>
            Login(group.GetProperty("notes").EnumerateArray().Select(note =>
                (note.GetProperty("name").GetString()!, note.GetProperty("value").GetString()!)).ToArray())).ToArray();
        using var combined = BravePasswordCsvNotes.Combine(groups);
        Assert.Equal(fixture.RootElement.GetProperty("expectedCombined").GetString(), Encoding.UTF8.GetString(combined.Utf8.Span));
    }

    [Fact]
    public void CanonicalValuesAreDistinctSortedAndJoinedWithoutChangingEmbeddedNewlines()
    {
        var rows = new[] { Login(("", "z\r\nline")), Login(("", "a,\"quoted\"")), Login(("", "z\r\nline")), Login(("", "")), Login() };
        using var combined = BravePasswordCsvNotes.Combine(rows);
        Assert.Equal("a,\"quoted\"\nz\r\nline", Encoding.UTF8.GetString(combined.Utf8.Span));
        Assert.Equal("z\r\nline", Encoding.UTF8.GetString(rows[0].Notes[0].Value.Utf8.Span));
    }

    [Fact]
    public void UsesBrowserUtf16OrderingRatherThanUtf8Ordering()
    {
        using var combined = BravePasswordCsvNotes.Combine(new[] { Login(("", "\ue000")), Login(("", "\U00010000")) });
        Assert.Equal("\U00010000\n\ue000", Encoding.UTF8.GetString(combined.Utf8.Span));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("extra")]
    public void NonemptyNamedNotesAreNeverSilentlyDiscarded(string name)
    {
        var error = Assert.Throws<BackupOperationException>(() => BravePasswordCsvNotes.Combine(new[] { Login(("", "canonical"), (name, "synthetic-secret-note")) }));
        Assert.Equal("passwords-csv-notes-unsupported", error.Code);
        Assert.DoesNotContain("synthetic-secret-note", error.Message); Assert.DoesNotContain(name, error.Message);
    }

    [Fact]
    public void EmptyNamedNoteDoesNotInventCanonicalNote()
    {
        using var combined = BravePasswordCsvNotes.Combine(new[] { Login(("named", "")) });
        Assert.Empty(combined.Utf8.ToArray());
    }

    [Fact]
    public void MissingEmptyAndZeroGroupsRemainEmpty()
    {
        using var absent = BravePasswordCsvNotes.Combine([]);
        using var empty = BravePasswordCsvNotes.Combine(new[] { Login(), Login(("", "")) });
        Assert.Empty(absent.Utf8.ToArray()); Assert.Empty(empty.Utf8.ToArray());
    }

    [Fact]
    public void DuplicateCanonicalIdentityIsInvalidEvenWhenValueIsEmpty()
    {
        Assert.Equal("passwords-csv-note-projection-invalid", Assert.Throws<BackupOperationException>(() =>
            BravePasswordCsvNotes.Combine(new[] { Login(("", ""), ("", "")) })).Code);
    }

    [Fact]
    public void OwnerCopiesAndDisposalAreIndependent()
    {
        var row = Login(("", "synthetic note\0☃"));
        using var combined = BravePasswordCsvNotes.Combine(new[] { row });
        var borrowed = combined.Utf8;
        combined.Dispose(); Assert.All(borrowed.ToArray(), b => Assert.Equal(0, b));
        Assert.Equal("synthetic note\0☃", Encoding.UTF8.GetString(row.Notes[0].Value.Utf8.Span));
    }

    [Fact]
    public void CancellationAndDisposedSourceStopProjection()
    {
        var row = Login(("", "note"));
        Assert.Throws<OperationCanceledException>(() => BravePasswordCsvNotes.Combine(new[] { row }, new CancellationToken(true)));
        row.Notes[0].Value.Dispose();
        Assert.Throws<ObjectDisposedException>(() => BravePasswordCsvNotes.Combine(new[] { row }));
    }

    [Fact]
    public void CombinedFieldLimitCountsSeparatingNewline()
    {
        var first = Login(("", new string('a', SensitiveUtf8.MaximumBytes / 2)));
        var second = Login(("", new string('b', SensitiveUtf8.MaximumBytes / 2)));
        Assert.Equal("passwords-csv-note-projection-invalid", Assert.Throws<BackupOperationException>(() => BravePasswordCsvNotes.Combine(new[] { first, second })).Code);
    }

    [Fact]
    public void GroupLimitIsCheckedBeforeReadingNotes()
    {
        var row = Login();
        Assert.Throws<BackupOperationException>(() => BravePasswordCsvNotes.Combine(Enumerable.Repeat(row, 100001).ToArray()));
    }

    public void Dispose() { foreach (var value in _values) value.Dispose(); }
}
