using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RawPasswordArchiveTests
{
    private static MemoryStream Archive(string mutation = "", bool encrypted = true)
    {
        var installation = Guid.NewGuid(); var profile = Guid.NewGuid();
        var prefix = $"installations/{installation:D}/profiles/{profile:D}/passwords/";
        var name = mutation == "orphan" || mutation == "wrong-missing" ? "Login Data-wal" : mutation == "unknown-file" ? "Local State" : "Login Data";
        var metadata = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1, ["captureKind"] = "raw-native-password-files", ["portableCaptureCompleted"] = false,
            ["reasonCode"] = "passwords-database-invalid", ["missingMainDatabases"] = mutation == "orphan" ? new[] { "Login Data" } : Array.Empty<string>()
        };
        if (mutation == "portable-claim") metadata["portableCaptureCompleted"] = true;
        if (mutation == "reason") metadata["reasonCode"] = "passwords-source-changed";
        if (mutation == "schema") metadata["schemaVersion"] = 999;
        if (mutation == "extra-metadata") metadata["credentials"] = "must not be stored";
        var version = mutation == "v6" ? 6 : 7;
        using var plain = new MemoryStream();
        using (var zip = new ZipArchive(plain, ZipArchiveMode.Create, leaveOpen: true))
        {
            var files = new List<ArchiveFile>();
            var indexBytes = mutation == "duplicate-metadata" ? "{\"schemaVersion\":1,\"schemaVersion\":1}"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(metadata);
            files.Add(BackupArchive.WriteFile(zip, prefix + "raw-index-v1.json", "raw-password-index", indexBytes, "balanced"));
            var native = BackupArchive.WriteFile(zip, prefix + "raw/" + name, "native-password-file", "synthetic raw bytes"u8.ToArray(), "balanced");
            if (mutation == "hash") native = native with { Sha256 = new string('0', 64) };
            if (mutation == "role") native = native with { Role = "portable-passwords" };
            if (mutation == "length") native = native with { Length = 64L * 1024 * 1024 + 1 };
            if (mutation == "null-path") native = native with { Path = null! };
            files.Add(native);
            if (mutation == "duplicate-file") files.Add(native);
            var category = new ArchiveCategory("passwords", mutation == "category-version" || mutation == "v6" ? 1 : 2, files.ToArray());
            BackupArchive.WriteManifest(zip, new ArchiveManifest("BraveBackupArchive", version, Guid.NewGuid(), DateTimeOffset.UtcNow,
                "synthetic", [version == 6 ? "passwords-v1" : mutation == "capability" ? "passwords-v1" : "passwords-v2"], "balanced",
                [new ArchiveInstallation(installation, "Brave", "Stable", "synthetic", [new ArchiveProfile(profile, "Synthetic", "Default", [category])])]));
        }
        var result = new MemoryStream();
        if (encrypted) { using var writer = ArchiveEncryption.CreateWriter(result, "synthetic-archive-key"); writer.Write(plain.GetBuffer().AsSpan(0, (int)plain.Length)); }
        else plain.WriteTo(result);
        result.Position = 0; return result;
    }

    [Theory]
    [InlineData("")]
    [InlineData("orphan")]
    public void AcceptsExplicitlyClassifiedRawFilesWithoutInterpretingThem(string variant)
    {
        using var archive = Archive(variant);
        Assert.Equal(7, BackupArchive.Validate(archive, "synthetic-archive-key").FormatVersion);
        Assert.Equal(7, BraveSettingsBackup.ValidateArchive(archive, "synthetic-archive-key").FormatVersion);
    }

    [Fact]
    public void RejectsUnencryptedRawPasswordArchive()
    {
        using var archive = Archive(encrypted: false);
        Assert.Contains("encrypted archive envelope", Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(archive)).Message);
    }

    [Theory]
    [InlineData("unknown-file")]
    [InlineData("portable-claim")]
    [InlineData("reason")]
    [InlineData("schema")]
    [InlineData("extra-metadata")]
    [InlineData("duplicate-metadata")]
    [InlineData("wrong-missing")]
    [InlineData("hash")]
    [InlineData("role")]
    [InlineData("length")]
    [InlineData("null-path")]
    [InlineData("duplicate-file")]
    [InlineData("v6")]
    [InlineData("category-version")]
    [InlineData("capability")]
    public void RejectsInvalidInventoryMetadataOrVersion(string mutation)
    {
        using var archive = Archive(mutation);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(archive, "synthetic-archive-key"));
    }
}
