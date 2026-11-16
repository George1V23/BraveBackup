using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BackupArchiveTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Archive.v1.bravebackup");

    [Fact]
    public void IndependentPythonZipFixture_ValidatesAndKeepsLegacySchema()
    {
        using var file = File.OpenRead(Fixture);
        var manifest = BackupArchive.Validate(file);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Equal("Fixture", manifest.Installations[0].Profiles[0].DisplayName);
        using var zip = ZipFile.OpenRead(Fixture);
        var portable = zip.Entries.Single(item => item.FullName.EndsWith("portable-v1.json"));
        using var content = portable.Open();
        var model = JsonSerializer.Deserialize<BackupModel>(content)!;
        Assert.Equal(1, model.SchemaVersion);
    }

    [Theory]
    [InlineData("../payload")]
    [InlineData("/rooted")]
    [InlineData("C:\\rooted")]
    [InlineData("installations\\escape")]
    [InlineData("undeclared.json")]
    [InlineData("a//b")]
    public void UnexpectedOrUnsafeEntry_IsRejected(string name)
    {
        using var archive = ModifyZip(zip =>
        {
            using var entry = zip.CreateEntry(name).Open(); entry.WriteByte(1);
        });
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(archive));
    }

    [Fact]
    public void DuplicateEntry_IsRejected()
    {
        using var archive = ModifyZip(zip => { using var entry = zip.CreateEntry("manifest.json").Open(); entry.Write("{}"u8); });
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(archive));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("capability")]
    [InlineData("null")]
    [InlineData("missing")]
    [InlineData("hash")]
    [InlineData("length")]
    [InlineData("id")]
    [InlineData("role")]
    [InlineData("traversal")]
    [InlineData("profile-null")]
    public void InvalidRequiredMetadata_IsRejected(string fault)
    {
        using var archive = ModifyZip(zip =>
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonNode node;
            using (var input = entry.Open()) node = JsonNode.Parse(input)!;
            var installation = node["installations"]![0]!;
            var profile = installation["profiles"]![0]!;
            var files = profile["categories"]![0]!["files"]!;
            switch (fault)
            {
                case "version": node["formatVersion"] = 99; break;
                case "capability": node["requiredCapabilities"]![0] = "unknown"; break;
                case "null": node["installations"] = null; break;
                case "missing": node.AsObject().Remove("archiveId"); break;
                case "hash": files[0]!["sha256"] = new string('0', 64); break;
                case "length": files[0]!["length"] = long.MaxValue; break;
                case "id": profile["id"] = installation["id"]!.GetValue<string>(); break;
                case "role": files[1]!["role"] = "native-source"; break;
                case "traversal": files[0]!["path"] = "../source.json"; break;
                case "profile-null": installation["profiles"]![0] = null; break;
            }
            entry.Delete();
            using var output = zip.CreateEntry("manifest.json").Open();
            JsonSerializer.Serialize(output, node);
        });
        Assert.ThrowsAny<Exception>(() => BackupArchive.Validate(archive));
    }

    [Fact]
    public void MissingPayload_DuplicateJsonProperties_AndInvalidPayloadJsonAreRejected()
    {
        using var missing = ModifyZip(zip => zip.Entries.First(item => item.FullName != "manifest.json").Delete());
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(missing));
        using var duplicateProperty = ModifyZip(zip =>
        {
            var old = zip.GetEntry("manifest.json")!;
            string text;
            using (var reader = new StreamReader(old.Open())) text = reader.ReadToEnd();
            old.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(text.Replace("\"formatVersion\": 1", "\"formatVersion\": 1, \"FormatVersion\": 1"));
        });
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(duplicateProperty));
        using var invalidJson = ModifyZip(zip =>
        {
            var payload = zip.Entries.First(item => item.FullName.EndsWith("source.json"));
            var name = payload.FullName; payload.Delete();
            var bytes = Encoding.UTF8.GetBytes("not-json");
            using (var output = zip.CreateEntry(name).Open()) output.Write(bytes);
            var manifest = zip.GetEntry("manifest.json")!;
            JsonNode node;
            using (var input = manifest.Open()) node = JsonNode.Parse(input)!;
            var descriptor = node["installations"]![0]!["profiles"]![0]!["categories"]![0]!["files"]![0]!;
            descriptor["length"] = bytes.Length;
            descriptor["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            manifest.Delete();
            using var result = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(result, node);
        });
        Assert.ThrowsAny<Exception>(() => BackupArchive.Validate(invalidJson));
    }

    [Fact]
    public void OversizedManifest_IsRejectedBeforeDecompression()
    {
        using var archive = ModifyZip(zip =>
        {
            zip.GetEntry("manifest.json")!.Delete();
            using var output = zip.CreateEntry("manifest.json", CompressionLevel.SmallestSize).Open();
            var bytes = new byte[1024 * 1024];
            for (var i = 0; i < 17; i++) output.Write(bytes);
        });
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(archive));
    }

    [Fact]
    public void Zip64Directory_IsBoundedBeforeEntryMaterialization()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, true))
            for (var index = 0; index < 65536; index++) zip.CreateEntry($"entry-{index}");
        archive.Position = 0;
        ArchiveZipGuard.Validate(archive, CancellationToken.None);
        Assert.Equal(0, archive.Position);
        // A false ZIP64 count must be rejected before ZipArchive allocates an entry list.
        var bytes = archive.ToArray();
        var zip64 = bytes.Length - 22 - 20 - 56;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(zip64 + 24), 100001);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(zip64 + 32), 100001);
        Assert.Throws<InvalidDataException>(() => BackupArchive.Validate(new MemoryStream(bytes)));
    }

    private static MemoryStream ModifyZip(Action<ZipArchive> change)
    {
        var result = new MemoryStream();
        using (var file = File.OpenRead(Fixture)) file.CopyTo(result);
        result.Position = 0;
        using (var zip = new ZipArchive(result, ZipArchiveMode.Update, leaveOpen: true)) change(zip);
        result.Position = 0;
        return result;
    }
}
