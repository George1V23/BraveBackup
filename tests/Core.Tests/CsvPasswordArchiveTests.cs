using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class CsvPasswordArchiveTests
{
    private const string Password = "synthetic-archive-password";
    private static byte[] Payload => System.Text.Encoding.UTF8.GetBytes("name,url,username,password,note\nSynthetic,https://example.test/,user,synthetic-password,\n");
    private static MemoryStream Archive(bool encrypt = true, string mutation = "")
    {
        var bytes = mutation == "binary-payload" ? File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "PortablePasswords", "Payload.v1.synthetic.bin")) : Payload;
        if (mutation == "corrupt-payload") bytes[0] ^= 1;
        var installation = Guid.NewGuid(); var profile = Guid.NewGuid();
        var path = $"installations/{installation:D}/profiles/{profile:D}/passwords/passwords.csv";
        var file = new ArchiveFile(path, "plaintext-password-csv", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        file = mutation switch
        {
            "hash" => file with { Sha256 = new string('0', 64) },
            "role" => file with { Role = "native-source" },
            "protected-role" => file with { Role = "portable-passwords" },
            "path" => file with { Path = path.Replace("passwords.csv", "unknown.bin") },
            "length" => file with { Length = PasswordCsvFormat.MaximumBytes + 1L },
            _ => file
        };
        var category = new ArchiveCategory("passwords", mutation == "category-version" ? 2 : 3, mutation == "missing-descriptor" ? [] : [file]);
        var capabilities = mutation switch
        {
            "no-password-capability" => new[] { "bookmarks-v1" },
            "unknown-capability" => new[] { "passwords-v3", "unknown-v1" },
            _ => new[] { "passwords-v3" }
        };
        var manifest = new ArchiveManifest("BraveBackupArchive", mutation == "old-version" ? 7 : 8, Guid.NewGuid(), DateTimeOffset.UtcNow,
            "synthetic", capabilities, "balanced", [new ArchiveInstallation(installation, "Brave", "Stable", "synthetic", [new ArchiveProfile(profile, "Synthetic", "Default", [category])])]);
        using var plain = new MemoryStream();
        try
        {
            using (var zip = new ZipArchive(plain, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (mutation != "missing-entry")
                {
                    using var entry = zip.CreateEntry(path).Open(); entry.Write(bytes);
                }
                if (mutation == "extra-entry") { using var extra = zip.CreateEntry("unknown.bin").Open(); extra.WriteByte(1); }
                BackupArchive.WriteManifest(zip, manifest);
            }
            var result = new MemoryStream();
            if (encrypt)
            {
                using (var writer = ArchiveEncryption.CreateWriter(result, Password)) writer.Write(plain.GetBuffer().AsSpan(0, (int)plain.Length));
            }
            else plain.WriteTo(result);
            result.Position = 0; return result;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(plain.GetBuffer()); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoreAndBraveValidatorsAcceptEncryptedAndPlainV8(bool encrypt)
    {
        using var archive = Archive(encrypt);
        var manifest = BackupArchive.Validate(archive, encrypt ? Password : null);
        Assert.Equal(8, manifest.FormatVersion);
        Assert.Equal(new[] { "passwords-v3" }, manifest.RequiredCapabilities);
        Assert.Equal(8, BraveSettingsBackup.ValidateArchive(archive, encrypt ? Password : null).FormatVersion);
    }

    [Theory]
    [InlineData("corrupt-payload")]
    [InlineData("binary-payload")]
    [InlineData("hash")]
    [InlineData("role")]
    [InlineData("protected-role")]
    [InlineData("category-version")]
    [InlineData("path")]
    [InlineData("length")]
    [InlineData("missing-descriptor")]
    [InlineData("missing-entry")]
    [InlineData("extra-entry")]
    [InlineData("no-password-capability")]
    [InlineData("unknown-capability")]
    [InlineData("old-version")]
    public void InvalidCategoryOrManifestIsRejected(string mutation)
    {
        using var archive = Archive(mutation: mutation);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(archive, Password));
    }

    [Fact]
    public void WrongPasswordAndCancellationFail()
    {
        using var archive = Archive();
        Assert.ThrowsAny<CryptographicException>(() => BackupArchive.Validate(archive, "wrong-synthetic-password"));
        Assert.Throws<OperationCanceledException>(() => BackupArchive.Validate(archive, Password, new CancellationToken(true)));
    }
}
