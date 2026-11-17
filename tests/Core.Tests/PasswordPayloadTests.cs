using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordPayloadTests
{
    private static byte[] Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PortablePasswords", "Payload.v1.synthetic.bin"));
    private static SensitiveUtf8 Secret(string value) => new(Encoding.UTF8.GetBytes(value));
    private static PortablePasswordDocument Document() => new(new[]
    {
        new PortablePasswordStore(PortablePasswordStoreKind.Profile, 1, 2, new[]
        {
            new PortablePasswordLogin(7, "https://synthetic.invalid/", "https://synthetic.invalid/", null, "", Secret("synthetic, \"secret\"\n☃\0"), new[]
            {
                new PortablePasswordNote("first", 123456789, true, Secret("synthetic note\r\nline two")),
                new PortablePasswordNote("second", 0, false, Secret(""))
            })
        }),
        new PortablePasswordStore(PortablePasswordStoreKind.Account, 0, 0, [])
    });

    [Fact]
    public void WriterMatchesIndependentPythonFixtureAndReaderPreservesFields()
    {
        using var source = Document();
        using var encoded = PasswordPayloadFormat.Encode(source);
        Assert.Equal(Fixture, encoded.Bytes.ToArray());
        using var decoded = PasswordPayloadFormat.Decode(Fixture);
        var store = decoded.Stores[0];
        Assert.Equal(PortablePasswordStoreKind.Profile, store.Kind);
        Assert.Equal(1, store.BlockedEntries); Assert.Equal(2, store.FederatedEntries);
        var login = Assert.Single(store.Logins);
        Assert.Equal(7, login.Id); Assert.Null(login.Username); Assert.Equal("", login.DisplayName);
        Assert.Equal("https://synthetic.invalid/", login.Origin); Assert.Equal(login.Origin, login.Realm);
        Assert.Equal("synthetic, \"secret\"\n☃\0", Encoding.UTF8.GetString(login.Password.Utf8.Span));
        Assert.Equal(123456789, login.Notes[0].Created); Assert.True(login.Notes[0].Confidential);
        Assert.False(login.Notes[1].Confidential); Assert.Empty(login.Notes[1].Value.Utf8.ToArray());
        Assert.Empty(decoded.Stores[1].Logins);
        using var reencoded = PasswordPayloadFormat.Encode(decoded);
        Assert.Equal(encoded.Bytes.ToArray(), reencoded.Bytes.ToArray());
    }

    [Fact]
    public void EveryTruncatedPrefixAndTrailingDataIsRejected()
    {
        var bytes = Fixture;
        for (var length = 0; length < bytes.Length; length++)
            Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(bytes.AsSpan(0, length)));
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 50)]
    [InlineData(8, 0)]
    [InlineData(8, 3)]
    [InlineData(12, 9)]
    public void RejectsUnknownVersionCountOrStoreKind(int offset, byte value)
    {
        var bytes = Fixture; bytes[offset] = value;
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(bytes));
    }

    [Fact]
    public void RejectsImpossibleLengthsAndInvalidMetadataUtf8()
    {
        var bytes = Fixture;
        // Header 12 + store header 16 + login id 8 = first origin length at byte 36.
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(bytes));
        bytes = Fixture; bytes[40] = 255;
        var error = Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(bytes));
        Assert.Null(error.InnerException); Assert.DoesNotContain("synthetic", error.Message);
    }

    [Theory]
    [InlineData("secret-utf8")]
    [InlineData("negative-count")]
    [InlineData("duplicate-store")]
    [InlineData("note-count")]
    [InlineData("note-flag")]
    public void MalformedFieldsFailWithoutPartialDocument(string field)
    {
        var bytes = Fixture;
        var first = bytes.AsSpan().IndexOf("first"u8);
        switch (field)
        {
            case "secret-utf8": bytes[bytes.AsSpan().IndexOf("synthetic, "u8)] = 255; break;
            case "negative-count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), -1); break;
            case "duplicate-store": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 16), 0); break;
            case "note-count": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(first - 8), 100001); break;
            case "note-flag": BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(first + 5 + 8), 2); break;
        }
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(bytes));
    }

    [Fact]
    public void OwnersClearBorrowedBuffersAndRemainIndependent()
    {
        using var source = Document();
        using var encoded = PasswordPayloadFormat.Encode(source);
        using var decoded = PasswordPayloadFormat.Decode(encoded.Bytes.Span);
        var payload = encoded.Bytes;
        var password = decoded.Stores[0].Logins[0].Password.Utf8;
        var note = decoded.Stores[0].Logins[0].Notes[0].Value.Utf8;
        source.Dispose();
        Assert.NotEqual(0, password.Span[0]);
        encoded.Dispose();
        Assert.All(payload.ToArray(), b => Assert.Equal(0, b));
        Assert.NotEqual(0, password.Span[0]);
        decoded.Dispose();
        Assert.All(password.ToArray(), b => Assert.Equal(0, b)); Assert.All(note.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => encoded.Bytes);
        Assert.Throws<ObjectDisposedException>(() => decoded.Stores);
        Assert.DoesNotContain("synthetic", source.ToString());
    }

    [Theory]
    [InlineData("duplicate-store")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-note")]
    [InlineData("negative-count")]
    [InlineData("unknown-kind")]
    [InlineData("invalid-id")]
    [InlineData("too-many-entries")]
    [InlineData("malformed-text")]
    public void InvalidDocumentsFailBeforeEncoding(string problem)
    {
        var id = problem == "invalid-id" ? 0 : 1;
        var note = new PortablePasswordNote("same", 0, false, Secret(""));
        var login = new PortablePasswordLogin(id, problem == "malformed-text" ? "\ud800" : "", "", null, null, Secret(""),
            problem == "duplicate-note" ? new[] { note, note } : new[] { note });
        var kind = problem == "unknown-kind" ? (PortablePasswordStoreKind)9 : PortablePasswordStoreKind.Profile;
        var store = new PortablePasswordStore(kind, problem == "negative-count" ? -1 : problem == "too-many-entries" ? 100001 : 0, 0,
            problem == "duplicate-id" ? new[] { login, login } : new[] { login });
        using var document = new PortablePasswordDocument(problem == "duplicate-store" ? new[] { store, store } : new[] { store });
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Encode(document));
    }

    [Fact]
    public void EmptyStoreIsValidButAbsentStoreIsNot()
    {
        using var empty = new PortablePasswordDocument(new[] { new PortablePasswordStore(PortablePasswordStoreKind.Profile, 0, 0, []) });
        using var encoded = PasswordPayloadFormat.Encode(empty);
        using var decoded = PasswordPayloadFormat.Decode(encoded.Bytes.Span);
        Assert.Empty(Assert.Single(decoded.Stores).Logins);
        using var missing = new PortablePasswordDocument([]);
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Encode(missing));
    }

    [Fact]
    public void CancellationAndBoundsAreEnforced()
    {
        using var document = Document();
        Assert.Throws<OperationCanceledException>(() => PasswordPayloadFormat.Encode(document, new CancellationToken(true)));
        Assert.Throws<OperationCanceledException>(() => PasswordPayloadFormat.Decode(Fixture, new CancellationToken(true)));
        Assert.Throws<InvalidDataException>(() => PasswordPayloadFormat.Decode(new byte[PasswordPayloadFormat.MaximumBytes + 1]));
    }
}
