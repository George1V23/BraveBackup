using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordCryptoTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords");
    private static JsonElement Vectors => JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Crypto.synthetic.json"))).RootElement.Clone();
    private static byte[] Vector(string name) => Convert.FromHexString(Vectors.GetProperty("valuesHex").GetProperty(name).GetString()!);
    private static PasswordKeyMaterial Key(string os) => new(os == "windows" ? PasswordEncryptionProvider.WindowsDpapiV10 : PasswordEncryptionProvider.LinuxKeyringV11,
        Convert.FromHexString(Vectors.GetProperty(os + "KeyHex").GetString()!));

    [Theory]
    [InlineData("windows", "v10", "password")]
    [InlineData("windows", "v10", "note")]
    [InlineData("linux", "v11", "password")]
    [InlineData("linux", "v11", "note")]
    public void DecodesIndependentOpenSslVectors(string os, string prefix, string kind)
    {
        using var key = Key(os);
        var input = Vector($"{os}-{prefix}-{kind}");
        var before = input.ToArray();
        using var value = BravePasswordCrypto.Decode(input, key);
        Assert.Equal(Encoding.UTF8.GetBytes(Vectors.GetProperty(kind + "Utf8").GetString()!), value.Utf8.ToArray());
        Assert.Equal(before, input);
        Assert.DoesNotContain("fixture", value.ToString());
    }

    [Fact]
    public void DerivesLinuxKeyFromExplicitSecret()
    {
        using var key = PasswordKeyMaterial.FromLinuxKeyringSecret(Encoding.UTF8.GetBytes(Vectors.GetProperty("syntheticLinuxSecret").GetString()!));
        using var value = BravePasswordCrypto.Decode(Vector("linux-v11-password"), key);
        Assert.Equal(Vectors.GetProperty("passwordUtf8").GetString(), Encoding.UTF8.GetString(value.Utf8.Span));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("linux")]
    public void DatabaseResultOwnsSecretsAndPreservesSource(string os)
    {
        using var source = BravePasswordDatabase.Read(File.ReadAllBytes(Path.Combine(Fixtures, $"LoginData.v43.{os}.sqlite")));
        using var key = Key(os);
        var encrypted = source.Rows[0].EncryptedValue.ToArray();
        using var decoded = BravePasswordCrypto.DecodeDatabase(source, key);
        var row = Assert.Single(decoded.Rows);
        Assert.Equal(source.Rows[0].Username, row.Username);
        Assert.Equal(1, decoded.BlockedEntries);
        Assert.Equal(1, decoded.FederatedEntries);
        Assert.Equal(Vectors.GetProperty("passwordUtf8").GetString(), Encoding.UTF8.GetString(row.Password.Utf8.Span));
        Assert.Equal(Vectors.GetProperty("noteUtf8").GetString(), Encoding.UTF8.GetString(Assert.Single(row.Notes).Value.Utf8.Span));
        Assert.Equal(encrypted, source.Rows[0].EncryptedValue);
        var password = row.Password.Utf8;
        var note = row.Notes[0].Value.Utf8;
        source.Dispose();
        Assert.NotEqual(0, password.Span[0]);
        decoded.Dispose();
        Assert.All(password.ToArray(), b => Assert.Equal(0, b));
        Assert.All(note.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => decoded.Rows);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(16)]
    [InlineData(60)]
    public void GcmRejectsModifiedNonceCiphertextOrTag(int offset)
    {
        using var key = Key("windows");
        var input = Vector("windows-v10-password");
        input[offset] ^= 1;
        var error = Assert.Throws<BackupOperationException>(() => BravePasswordCrypto.Decode(input, key));
        Assert.Equal("passwords-decode-failed", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("fixture", error.Message);
    }

    [Theory]
    [InlineData("v10")]
    [InlineData("v12")]
    [InlineData("v20")]
    [InlineData("xyz")]
    public void NeverGuessesAnotherProvider(string prefix)
    {
        using var key = Key("linux");
        var error = Assert.Throws<BackupOperationException>(() => BravePasswordCrypto.Decode(Encoding.ASCII.GetBytes(prefix), key));
        Assert.Equal("passwords-encryption-unsupported", error.Code);
    }

    [Theory]
    [InlineData("windows", "v10")]
    [InlineData("linux", "v11")]
    public void EmptyValueIsDistinctFromTruncatedFrame(string os, string prefix)
    {
        using var key = Key(os);
        using var value = BravePasswordCrypto.Decode([], key);
        Assert.Empty(value.Utf8.ToArray());
        Assert.Equal("passwords-decode-failed", Assert.Throws<BackupOperationException>(() => BravePasswordCrypto.Decode(Encoding.ASCII.GetBytes(prefix), key)).Code);
        var input = Vector($"{os}-{prefix}-password");
        Assert.Throws<BackupOperationException>(() => BravePasswordCrypto.Decode(input[..^1], key));
    }

    [Fact]
    public void AuthenticatedInvalidUtf8IsRejected()
    {
        var rawKey = new byte[32];
        using var key = new PasswordKeyMaterial(PasswordEncryptionProvider.WindowsDpapiV10, rawKey);
        var frame = new byte[32];
        "v10"u8.CopyTo(frame);
        using (var aes = new AesGcm(rawKey, 16)) aes.Encrypt(frame.AsSpan(3, 12), new byte[] { 255 }, frame.AsSpan(15, 1), frame.AsSpan(16, 16));
        Assert.Equal("passwords-decode-failed", Assert.Throws<BackupOperationException>(() => BravePasswordCrypto.Decode(frame, key)).Code);
    }

    [Fact]
    public void BadNoteFailsWholeDatabase()
    {
        using var source = BravePasswordDatabase.Read(File.ReadAllBytes(Path.Combine(Fixtures, "LoginData.v43.windows.sqlite")));
        using var key = Key("windows");
        source.Rows[0].Notes[0].EncryptedValue[^1] ^= 1;
        Assert.Throws<BackupOperationException>(() => BravePasswordCrypto.DecodeDatabase(source, key));
    }

    [Fact]
    public void SensitiveOwnerCopiesValidatesAndClearsBytes()
    {
        var input = Encoding.UTF8.GetBytes("synthetic\0☃\r\n");
        using var value = new SensitiveUtf8(input);
        var borrowed = value.Utf8;
        input[0] = 0;
        Assert.Equal((byte)'s', borrowed.Span[0]);
        value.Dispose();
        Assert.All(borrowed.ToArray(), b => Assert.Equal(0, b));
        Assert.Throws<ObjectDisposedException>(() => value.Utf8);
        Assert.Throws<InvalidDataException>(() => new SensitiveUtf8(new byte[] { 255 }));
        Assert.Throws<InvalidDataException>(() => new SensitiveUtf8(new byte[SensitiveUtf8.MaximumBytes + 1]));
    }

    [Fact]
    public void KeyOwnershipValidationAndCancellation()
    {
        var input = Convert.FromHexString(Vectors.GetProperty("windowsKeyHex").GetString()!);
        using var key = new PasswordKeyMaterial(PasswordEncryptionProvider.WindowsDpapiV10, input);
        CryptographicOperations.ZeroMemory(input);
        using var value = BravePasswordCrypto.Decode(Vector("windows-v10-password"), key);
        Assert.Throws<OperationCanceledException>(() => BravePasswordCrypto.Decode([], key, new CancellationToken(true)));
        key.Dispose();
        Assert.Throws<ObjectDisposedException>(() => BravePasswordCrypto.Decode([], key));
        Assert.Throws<ArgumentException>(() => new PasswordKeyMaterial(PasswordEncryptionProvider.WindowsDpapiV10, new byte[16]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PasswordKeyMaterial((PasswordEncryptionProvider)99, []));
    }
}
