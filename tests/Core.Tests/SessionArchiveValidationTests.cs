using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class SessionArchiveValidationTests
{
    private static string Fixture =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sessions", "Archive.v4.sessions.bravebackup");

    [Fact]
    public void IndependentCppAndPythonArchive_ValidatesAlongsideAllOlderVersions()
    {
        using var newest = File.OpenRead(Fixture);
        Assert.Equal(4, BraveSettingsBackup.ValidateArchive(newest).FormatVersion);
        foreach (var file in new[]
                 {
                     "Archive.v1.bravebackup", "Archive.v2.settings.bravebackup", "Archive.v3.extensions.bravebackup"
                 })
        {
            using var source = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
            BraveSettingsBackup.ValidateArchive(source);
        }
    }

    [Theory]
    [InlineData("duplicate-tab")]
    [InlineData("group-reference")]
    [InlineData("selection")]
    [InlineData("live-claim")]
    [InlineData("source-path")]
    [InlineData("source-hash")]
    [InlineData("source-version")]
    [InlineData("native-toggle")]
    [InlineData("url-replay-mismatch")]
    [InlineData("group-replay-mismatch")]
    public void RehashedPortableTampering_CannotPassStructureOrNativeReplay(string fault)
    {
        using var source = Modify((zip, manifest, payload) =>
        {
            switch (fault)
            {
                case "duplicate-tab": payload["windows"]![1]!["tabs"]![0]!["id"] = 11; break;
                case "group-reference": payload["windows"]![0]!["tabs"]![0]!["groupId"] = new string('1', 32); break;
                case "selection": payload["windows"]![0]!["selectedTabIndex"] = 5; break;
                case "live-claim": payload["captureKind"] = "real-time"; break;
                case "source-path": payload["source"]!["path"] = "../escape"; break;
                case "source-hash": payload["source"]!["sha256"] = new string('0', 64); break;
                case "source-version": payload["source"]!["formatVersion"] = 5; break;
                case "native-toggle": payload["includeNativeFiles"] = false; break;
                case "url-replay-mismatch":
                    payload["windows"]![0]!["tabs"]![0]!["url"] = "https://invented.example.test/"; break;
                case "group-replay-mismatch": payload["windows"]![0]!["groups"]![0]!["title"] = "invented"; break;
            }
        });
        Assert.Throws<InvalidDataException>(() => BraveSettingsBackup.ValidateArchive(source));
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("old-version")]
    [InlineData("role")]
    [InlineData("missing-native")]
    [InlineData("undeclared")]
    public void MalformedSessionManifest_IsRejected(string fault)
    {
        using var source = Modify((zip, manifest, _) =>
        {
            var category = manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!;
            switch (fault)
            {
                case "capability": manifest["requiredCapabilities"] = new JsonArray("bookmarks-v1"); break;
                case "old-version": manifest["formatVersion"] = 3; break;
                case "role": category["files"]![1]!["role"] = "extension-file"; break;
                case "missing-native":
                    zip.GetEntry(category["files"]![1]!["path"]!.GetValue<string>())!.Delete(); break;
                case "undeclared":
                    using (var stream = zip.CreateEntry("undeclared.bin").Open()) stream.WriteByte(0);
                    break;
            }
        });
        Assert.Throws<InvalidDataException>(() => BraveSettingsBackup.ValidateArchive(source));
    }

    [Fact]
    public void BoundedSerializationAndPortableResourceValidation_FailExplicitly()
    {
        Assert.Throws<InvalidDataException>(() => PortableSessionFormat.SerializeBounded(new string('x', 10000), 100));
        var model = BraveSessionDecoder.Decode(BraveSessionDecoderTests.Fixture(), "Sessions/Session_10",
            DateTimeOffset.UnixEpoch);
        var node = JsonSerializer.SerializeToNode(model, ArchiveManifest.JsonOptions)!;
        node["windows"]![0]!["tabs"]![0]!["title"] = new string('x', 32769);
        using var document = JsonDocument.Parse(node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => PortableSessionFormat.Parse(document.RootElement));
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
                using var source = zip.GetEntry(path)!.Open();
                return JsonNode.Parse(source)!;
            }

            var manifest = Read("manifest.json");
            var descriptor = manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![0]!;
            var path = descriptor["path"]!.GetValue<string>();
            var portable = Read(path);
            action(zip, manifest, portable);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(portable);
            descriptor["length"] = bytes.Length;
            descriptor["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            zip.GetEntry(path)!.Delete();
            using (var output = zip.CreateEntry(path).Open()) output.Write(bytes);
            zip.GetEntry("manifest.json")!.Delete();
            using (var output = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(output, manifest);
        }

        result.Position = 0;
        return result;
    }
}