using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ExtensionArchiveValidationTests
{
    private static string Fixture =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archive.v3.extensions.bravebackup");

    [Fact]
    public void IndependentV3FixtureAndPreviousArchiveVersions_AllValidate()
    {
        foreach (var name in new[]
                 {
                     "Archive.v1.bravebackup", "Archive.v2.settings.bravebackup", "Archive.v3.extensions.bravebackup"
                 })
        {
            using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
            BraveSettingsBackup.ValidateArchive(source);
        }
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-version")]
    [InlineData("bad-directory")]
    [InlineData("missing-parent")]
    [InlineData("state")]
    [InlineData("duplicate-file")]
    [InlineData("path")]
    [InlineData("role")]
    [InlineData("length")]
    [InlineData("missing-file")]
    [InlineData("missing-version")]
    [InlineData("metadata-mismatch")]
    public void RehashedInvalidInventory_IsRejected(string fault)
    {
        using var source = Modify((zip, manifest, inventory) =>
        {
            var item = inventory["extensions"]![0]!;
            var file = item["files"]![0]!;
            switch (fault)
            {
                case "schema": inventory["schemaVersion"] = 2; break;
                case "duplicate-id": inventory["extensions"]!.AsArray().Add(item.DeepClone()); break;
                case "duplicate-version": item["versions"]!.AsArray().Add(item["versions"]![0]!.DeepClone()); break;
                case "bad-directory": item["directories"]!.AsArray().Add("local/../escape"); break;
                case "missing-parent":
                    item["directories"] = new JsonArray("package/10.0_0", "package/9.0_0", "sync"); break;
                case "state": item["localStorage"] = "absent"; break;
                case "duplicate-file": item["files"]!.AsArray().Add(file.DeepClone()); break;
                case "path": file["relativePath"] = "../escape"; break;
                case "role":
                    manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![1]!["role"] =
                        "portable-settings"; break;
                case "length": file["length"] = -1; break;
                case "missing-file": zip.GetEntry(file["archivePath"]!.GetValue<string>())!.Delete(); break;
                case "missing-version": item["versions"] = new JsonArray(); break;
                case "metadata-mismatch": item["versions"]![0]!["name"] = "invented different name"; break;
            }
        });
        var failure = Record.Exception(() => BraveSettingsBackup.ValidateArchive(source));
        Assert.NotNull(failure);
        Assert.True(failure is InvalidDataException or BackupOperationException, failure.ToString());
    }

    [Fact]
    public void BinaryTampering_IsRejectedWithoutTreatingStorageAsJson()
    {
        using var source = Modify((zip, _, inventory) =>
        {
            var reference = inventory["extensions"]![0]!["files"]!.AsArray()
                .Single(item => item!["area"]!.GetValue<string>() == "local")!;
            var path = reference["archivePath"]!.GetValue<string>();
            zip.GetEntry(path)!.Delete();
            using var output = zip.CreateEntry(path).Open();
            output.Write(new byte[7]);
        });
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(source));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void WrongRequiredManifestVersion_CannotSilentlySkipExtensions(int version)
    {
        using var source = Modify((_, manifest, _) => manifest["formatVersion"] = version);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(source));
    }

    private static MemoryStream Modify(Action<ZipArchive, JsonNode, JsonNode> action)
    {
        var result = new MemoryStream();
        using (var source = File.OpenRead(Fixture)) source.CopyTo(result);
        result.Position = 0;
        using (var zip = new ZipArchive(result, ZipArchiveMode.Update, true))
        {
            JsonNode Read(string path)
            {
                using var input = zip.GetEntry(path)!.Open();
                return JsonNode.Parse(input)!;
            }

            var manifest = Read("manifest.json");
            var descriptor = manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![0]!;
            var inventoryPath = descriptor["path"]!.GetValue<string>();
            var inventory = Read(inventoryPath);
            action(zip, manifest, inventory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(inventory);
            descriptor["length"] = bytes.Length;
            descriptor["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            zip.GetEntry(inventoryPath)!.Delete();
            using (var output = zip.CreateEntry(inventoryPath).Open()) output.Write(bytes);
            zip.GetEntry("manifest.json")!.Delete();
            using (var output = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(output, manifest);
        }

        result.Position = 0;
        return result;
    }
}