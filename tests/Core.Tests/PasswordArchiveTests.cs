using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class PasswordArchiveTests
{
    private const string Password = "synthetic-archive-password";
    private static byte[] Payload => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "PortablePasswords", "Payload.v1.synthetic.bin"));
    private static MemoryStream Archive(bool encrypt = true, string mutation = "")
    {
        var bytes = Payload;
        if (mutation == "corrupt-payload") bytes[0] ^= 1;
        var installation = Guid.NewGuid(); var profile = Guid.NewGuid();
        var path = $"installations/{installation:D}/profiles/{profile:D}/passwords/portable-v1.bin";
        var file = new ArchiveFile(path, "portable-passwords", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        file = mutation switch
        {
            "hash" => file with { Sha256 = new string('0', 64) },
            "role" => file with { Role = "native-source" },
            "path" => file with { Path = path.Replace("portable-v1.bin", "unknown.bin") },
            "length" => file with { Length = PasswordPayloadFormat.MaximumBytes + 1L },
            _ => file
        };
        var category = new ArchiveCategory("passwords", 1, mutation == "missing-descriptor" ? [] : [file]);
        var capabilities = mutation switch
        {
            "no-password-capability" => new[] { "bookmarks-v1" },
            "unknown-capability" => new[] { "passwords-v1", "unknown-v1" },
            _ => new[] { "passwords-v1" }
        };
        var manifest = new ArchiveManifest("BraveBackupArchive", mutation == "old-version" ? 5 : 6, Guid.NewGuid(), DateTimeOffset.UtcNow,
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

    [Fact]
    public void CoreAndBraveValidatorsAcceptEncryptedV6()
    {
        using var archive = Archive();
        var manifest = BackupArchive.Validate(archive, Password);
        Assert.Equal(6, manifest.FormatVersion); Assert.Equal(new[] { "passwords-v1" }, manifest.RequiredCapabilities);
        Assert.Equal(6, BraveSettingsBackup.ValidateArchive(archive, Password).FormatVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainZipCannotClaimProtectedPasswordPayload(bool braveValidator)
    {
        using var archive = Archive(encrypt: false);
        var error = Assert.Throws<InvalidDataException>(() => braveValidator ? BraveSettingsBackup.ValidateArchive(archive) : BackupArchive.Validate(archive));
        Assert.Contains("encrypted archive envelope", error.Message);
    }

    [Theory]
    [InlineData("corrupt-payload")]
    [InlineData("hash")]
    [InlineData("role")]
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
