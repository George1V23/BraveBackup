using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class SettingsArchiveValidationTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archive.v2.settings.bravebackup");

    [Fact]
    public void IndependentV2FixtureAndExistingV1Fixture_ValidateThroughTheSameService()
    {
        using var second = File.OpenRead(Fixture);
        Assert.Equal(2, BraveSettingsBackup.ValidateArchive(second).FormatVersion);
        using var first = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archive.v1.bravebackup"));
        Assert.Equal(1, BraveSettingsBackup.ValidateArchive(first).FormatVersion);
    }

    [Theory]
    [InlineData("future-version")] [InlineData("v1-settings")] [InlineData("unknown-capability")]
    [InlineData("missing-capability")] [InlineData("unknown-category")] [InlineData("category-version")]
    [InlineData("role")] [InlineData("path")] [InlineData("missing-category")]
    [InlineData("duplicate-category")] [InlineData("payload-length")] [InlineData("payload-hash")]
    public void InvalidV2Manifest_FailsWithoutExtractingAnything(string fault)
    {
        using var result = ChangeManifest(node =>
        {
            var profile = node["installations"]![0]!["profiles"]![0]!;
            var category = profile["categories"]![0]!;
            var file = category["files"]![0]!;
            switch (fault)
            {
                case "future-version": node["formatVersion"] = 4; break;
                case "v1-settings": node["formatVersion"] = 1; break;
                case "unknown-capability": node["requiredCapabilities"]![0] = "future-v1"; break;
                case "missing-capability": node["requiredCapabilities"] = new JsonArray(); break;
                case "unknown-category": category["id"] = "future"; break;
                case "category-version": category["version"] = 2; break;
                case "role": file["role"] = "native-source"; break;
                case "path": file["path"] = "../settings.json"; break;
                case "missing-category": profile["categories"] = new JsonArray(); break;
                case "duplicate-category":
                    node["requiredCapabilities"] = new JsonArray("bookmarks-v1", "settings-v1");
                    profile["categories"]!.AsArray().Add(category.DeepClone()); break;
                case "payload-length": file["length"] = ArchiveLimits.MaxSettingsBytes + 1; break;
                case "payload-hash": file["sha256"] = new string('0', 64); break;
            }
        });
        Assert.Throws<InvalidDataException>(() => BraveSettingsBackup.ValidateArchive(result));
    }

    [Theory]
    [InlineData("unknown-key")] [InlineData("missing-key")] [InlineData("wrong-approved-type")]
    [InlineData("duplicate-presence")] [InlineData("schema")] [InlineData("null-values")]
    public void RehashedSettingsWithInvalidPolicy_AreStillRejected(string fault)
    {
        using var result = ChangePayload(payload =>
        {
            switch (fault)
            {
                case "unknown-key": payload["values"]!["unapproved.preference"] = true; break;
                case "missing-key": payload["values"]!.AsObject().Remove("homepage"); break;
                case "wrong-approved-type": payload["values"]!["homepage"] = true; break;
                case "duplicate-presence": payload["absentKeys"] = new JsonArray("homepage"); break;
                case "schema": payload["schemaVersion"] = 2; break;
                case "null-values": payload["values"] = null; break;
            }
        });
        if (fault == "wrong-approved-type")
            Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.ValidateArchive(result));
        else
            Assert.Throws<InvalidDataException>(() => BraveSettingsBackup.ValidateArchive(result));
    }

    [Fact]
    public void Core_RemainsBrowserIndependentWhileBackupEnforcesTheApprovedKeyPolicy()
    {
        using var result = ChangePayload(payload => payload["values"]!["unapproved.preference"] = true);
        // The generic archive format is sound; only the browser-specific policy rejects it.
        Assert.Equal(2, BackupArchive.Validate(result).FormatVersion);
        Assert.Throws<InvalidDataException>(() => BraveSettingsBackup.ValidateArchive(result));
    }

    private static MemoryStream ChangeManifest(Action<JsonNode> change) => Modify(zip =>
    {
        var manifest = Read(zip.GetEntry("manifest.json")!);
        change(manifest);
        Rewrite(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
    });

    private static MemoryStream ChangePayload(Action<JsonNode> change) => Modify(zip =>
    {
        var manifest = Read(zip.GetEntry("manifest.json")!);
        var descriptor = manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![0]!;
        var path = descriptor["path"]!.GetValue<string>();
        var payload = Read(zip.GetEntry(path)!);
        change(payload);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        descriptor["length"] = bytes.Length;
        descriptor["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Rewrite(zip, path, bytes);
        Rewrite(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest));
    });

    private static JsonNode Read(ZipArchiveEntry entry)
    { using var input = entry.Open(); return JsonNode.Parse(input)!; }

    private static void Rewrite(ZipArchive zip, string path, byte[] bytes)
    {
        zip.GetEntry(path)!.Delete();
        using var output = zip.CreateEntry(path).Open(); output.Write(bytes);
    }

    private static MemoryStream Modify(Action<ZipArchive> change)
    {
        var result = new MemoryStream();
        using (var input = File.OpenRead(Fixture)) input.CopyTo(result);
        result.Position = 0;
        using (var zip = new ZipArchive(result, ZipArchiveMode.Update, true)) change(zip);
        result.Position = 0; return result;
    }
}
