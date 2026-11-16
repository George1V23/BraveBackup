using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Extensions.TabManagerPlus;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class TabManagerPlusArchiveValidationTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "TabManagerPlus",
        "Archive.v5.tabmanagerplus.bravebackup");

    [Fact]
    public void ReadsIndependentlyProducedArchiveAndPayload()
    {
        using var input = File.OpenRead(Fixture);
        var manifest = BraveSettingsBackup.ValidateArchive(input);
        Assert.Equal(5, manifest.FormatVersion);
        var file = Assert.Single(Assert.Single(Assert.Single(Assert.Single(manifest.Installations).Profiles).Categories)
            .Files);
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        using var memory = new MemoryStream();
        using (var source = zip.GetEntry(file.Path)!.Open()) source.CopyTo(memory);
        var parsed = SavedWindowPayload.Parse(memory.ToArray());
        Assert.Equal(1, parsed.Data.TabCount);
        Assert.Equal("Synthetic work", Assert.Single(parsed.Data.Windows).Name);
    }

    [Theory]
    [InlineData("extensionId")]
    [InlineData("extensionVersion")]
    [InlineData("missing-url")]
    [InlineData("bad-index")]
    [InlineData("bad-geometry")]
    [InlineData("bad-date")]
    [InlineData("bad-order")]
    public void RehashedSemanticCorruptionStillFailsBrowserValidation(string kind)
    {
        using var input = Modify((_, _, payload) =>
        {
            var window = payload["windows"]![0]!;
            switch (kind)
            {
                case "extensionId": payload["extensionId"] = new string('a', 32); break;
                case "extensionVersion": payload["extensionVersion"] = "7.0.1"; break;
                case "missing-url": window["tabs"]![0]!.AsObject().Remove("url"); break;
                case "bad-index": window["tabs"]![0]!["index"] = -1; break;
                case "bad-geometry": window["windowsInfo"]!["width"] = -1; break;
                case "bad-date": window["date"] = -1; break;
                case "bad-order":
                    var other = window.DeepClone();
                    other["id"] = "other";
                    other["order"] = -1;
                    payload["windows"]!.AsArray().Add(other);
                    break;
            }
        });
        BackupArchive.Validate(input); // Generic envelope can be valid; extension policy is stricter.
        input.Position = 0;
        Assert.Throws<InvalidDataException>(() => BraveSettingsBackup.ValidateArchive(input));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("capture")]
    [InlineData("source")]
    [InlineData("count")]
    [InlineData("date")]
    [InlineData("windows")]
    [InlineData("duplicate-id")]
    public void RehashedInvalidEnvelopeFailsCoreValidation(string kind)
    {
        using var input = Modify((_, _, payload) =>
        {
            switch (kind)
            {
                case "schema": payload["schemaVersion"] = 2; break;
                case "capture": payload["captureKind"] = "live-browser"; break;
                case "source": payload["source"] = "/private/source"; break;
                case "count": payload["sourceFileCount"] = 0; break;
                case "date": payload["capturedUtc"] = "2026-11-17T00:00:00+02:00"; break;
                case "windows": payload["windows"] = null; break;
                case "duplicate-id": payload["windows"]!.AsArray().Add(payload["windows"]![0]!.DeepClone()); break;
            }
        });
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(input));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public void SavedWindowsRequireExactlyManifestVersionFive(int version)
    {
        using var input = Modify((_, manifest, _) => manifest["formatVersion"] = version);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(input));
    }

    [Theory]
    [InlineData("role")]
    [InlineData("path")]
    [InlineData("length")]
    [InlineData("hash")]
    [InlineData("extra")]
    [InlineData("capability")]
    public void RejectsInvalidDescriptorOrUndeclaredContent(string kind)
    {
        using var input = Modify((zip, manifest, _) =>
        {
            var file = manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![0]!;
            if (kind == "role") file["role"] = "native-extension-database";
            if (kind == "path") file["path"] = "../outside.json";
            if (kind == "length") file["length"] = ExtensionSavedWindowsArchiveFormat.MaximumBytes + 1;
            if (kind == "hash") file["sha256"] = new string('0', 64);
            if (kind == "extra")
                using (var output = zip.CreateEntry("undeclared.json").Open())
                    output.WriteByte(0);
            if (kind == "capability") manifest["requiredCapabilities"] = new JsonArray("sessions-v1");
        }, rewriteMetadata: false);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(input));
    }

    [Fact]
    public void PortableSerializationRoundTripsEmptyAndNonEmptyData()
    {
        foreach (var source in new[] { SavedWindowsTests.Source(), "{}"u8.ToArray() })
        {
            var data = SavedWindows.Parse(source, "7.0.0");
            var bytes = SavedWindowPayload.Serialize(data, DateTimeOffset.UtcNow, 5);
            var parsed = SavedWindowPayload.Parse(bytes);
            Assert.Equal(data.TabCount, parsed.Data.TabCount);
            Assert.Equal(data.Windows.Select(w => w.Id), parsed.Data.Windows.Select(w => w.Id));
            Assert.DoesNotContain("windowsInfo\":null", System.Text.Encoding.UTF8.GetString(bytes));
        }
    }

    private static MemoryStream Modify(Action<ZipArchive, JsonNode, JsonNode> change, bool rewriteMetadata = true)
    {
        var result = new MemoryStream(File.ReadAllBytes(Fixture).Length * 2);
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
            var file = manifest["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![0]!;
            var path = file["path"]!.GetValue<string>();
            var payload = Read(path);
            change(zip, manifest, payload);
            if (rewriteMetadata)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
                file["length"] = bytes.Length;
                file["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
                zip.GetEntry(path)!.Delete();
                using (var output = zip.CreateEntry(path).Open()) output.Write(bytes);
            }

            zip.GetEntry("manifest.json")!.Delete();
            using (var output = zip.CreateEntry("manifest.json").Open()) JsonSerializer.Serialize(output, manifest);
        }

        result.Position = 0;
        return result;
    }
}