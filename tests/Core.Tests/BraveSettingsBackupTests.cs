using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BraveSettingsBackupTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BraveSettingsTests_" + Guid.NewGuid().ToString("N"));

    private static byte[] Fixture =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Preferences.settings.json"));

    public BraveSettingsBackupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Extract_PreservesApprovedValuesAndExcludesUnrelatedData()
    {
        var input = Fixture;
        var original = input.ToArray();
        var output = BraveSettingsBackup.Extract(input);
        Assert.Equal(7, BraveSettingsBackup.ValidatePayload(output));
        using var json = JsonDocument.Parse(output);
        var model = PortableSettings.Parse(json.RootElement);
        Assert.Empty(model.AbsentKeys);
        Assert.False(model.Values["bookmark_bar.show_on_all_tabs"].GetBoolean());
        Assert.Equal(1, model.Values["bookmark_bar.visibility_state"].GetInt32());
        Assert.True(model.Values["browser.show_home_button"].GetBoolean());
        Assert.False(model.Values["homepage_is_newtabpage"].GetBoolean());
        Assert.Equal("https://home.example.test/", model.Values["homepage"].GetString());
        Assert.Equal(4, model.Values["session.restore_on_startup"].GetInt32());
        Assert.Equal(new[] { "https://first.example.test/", "https://second.example.test/" },
            model.Values["session.startup_urls"].EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.DoesNotContain("unrelated", Encoding.UTF8.GetString(output));
        Assert.DoesNotContain("must_not_be_archived", Encoding.UTF8.GetString(output));
        Assert.Equal(original, input);
    }

    [Theory]
    [InlineData("{}", 0)]
    [InlineData("{\"homepage\":\"\"}", 1)]
    [InlineData("{\"session\":{\"startup_urls\":[]}}", 1)]
    [InlineData("{\"session\":{\"restore_on_startup\":999}}", 1)]
    [InlineData("{\"bookmark_bar\":{\"visibility_state\":-9}}", 1)]
    public void AbsentEmptyAndUnknownIntegerValues_ArePreservedWithoutDefaultGuessing(string json, int count)
    {
        var bytes = BraveSettingsBackup.Extract(Encoding.UTF8.GetBytes(json));
        Assert.Equal(count, BraveSettingsBackup.ValidatePayload(bytes));
        using var document = JsonDocument.Parse(bytes);
        var payload = PortableSettings.Parse(document.RootElement);
        Assert.Equal(7 - count, payload.AbsentKeys.Length);
        if (json.Contains("999")) Assert.Equal(999, payload.Values["session.restore_on_startup"].GetInt32());
        if (json.Contains("-9")) Assert.Equal(-9, payload.Values["bookmark_bar.visibility_state"].GetInt32());
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"homepage\":null}")]
    [InlineData("{\"homepage\":false}")]
    [InlineData("{\"browser\":true}")]
    [InlineData("{\"browser\":{\"show_home_button\":\"true\"}}")]
    [InlineData("{\"bookmark_bar\":{\"visibility_state\":1.5}}")]
    [InlineData("{\"session\":{\"restore_on_startup\":2147483648}}")]
    [InlineData("{\"session\":{\"startup_urls\":[false]}}")]
    [InlineData("{\"session\":{\"startup_urls\":\"https://example.test\"}}")]
    [InlineData("{\"homepage\":\"first\",\"homepage\":\"second\"}")]
    [InlineData("{\"unknown\":{\"value\":1,\"value\":2}}")]
    public void AmbiguousOrInvalidTypes_FailTheCategory(string json)
    {
        Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Extract(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void ValueAndPayloadLimits_RejectOversizedSettings()
    {
        var longString = JsonSerializer.SerializeToUtf8Bytes(new { homepage = new string('a', 32769) });
        Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Extract(longString));
        var largeList = JsonSerializer.SerializeToUtf8Bytes(new
            { session = new { startup_urls = Enumerable.Repeat("x", 1025).ToArray() } });
        Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Extract(largeList));
        var largePayload = JsonSerializer.SerializeToUtf8Bytes(new
            { session = new { startup_urls = Enumerable.Repeat(new string('x', 2000), 600).ToArray() } });
        var error = Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Extract(largePayload));
        Assert.Equal("settings-payload-size", error.Code);
    }

    [Fact]
    public void PortablePolicy_RejectsUnknownMissingDuplicateAndWronglyTypedKeys()
    {
        var valid = BraveSettingsBackup.Extract(Fixture);
        var unknown = JsonNode.Parse(valid)!;
        unknown["values"]!["unknown.preference"] = true;
        Assert.Throws<InvalidDataException>(() =>
            BraveSettingsBackup.ValidatePayload(JsonSerializer.SerializeToUtf8Bytes(unknown)));
        var missing = JsonNode.Parse(valid)!;
        missing["values"]!.AsObject().Remove("homepage");
        Assert.Throws<InvalidDataException>(() =>
            BraveSettingsBackup.ValidatePayload(JsonSerializer.SerializeToUtf8Bytes(missing)));
        var duplicate = JsonNode.Parse(valid)!;
        duplicate["absentKeys"] = new JsonArray("homepage");
        Assert.Throws<InvalidDataException>(() =>
            BraveSettingsBackup.ValidatePayload(JsonSerializer.SerializeToUtf8Bytes(duplicate)));
        var wrong = JsonNode.Parse(valid)!;
        wrong["values"]!["homepage"] = 15;
        Assert.Throws<BackupOperationException>(() =>
            BraveSettingsBackup.ValidatePayload(JsonSerializer.SerializeToUtf8Bytes(wrong)));
    }

    [Fact]
    public void Capture_IsReadOnlyAndHandlesBomMissingCorruptOversizedAndCancellation()
    {
        var path = Path.Combine(_root, "Preferences");
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Fixture).ToArray();
        File.WriteAllBytes(path, bytes);
        var result = BraveSettingsBackup.Capture(_root);
        Assert.Equal(7, BraveSettingsBackup.ValidatePayload(result));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        File.Delete(path);
        Assert.Equal("settings-source-unreadable",
            Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Capture(_root)).Code);
        File.WriteAllBytes(path, new byte[] { 0xFF, 0xFF });
        Assert.Equal("settings-source-unreadable",
            Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Capture(_root)).Code);
        using (var source = File.OpenWrite(path)) source.SetLength(ArchiveLimits.MaxPreferencesBytes + 1L);
        Assert.Equal("settings-source-size",
            Assert.Throws<BackupOperationException>(() => BraveSettingsBackup.Capture(_root)).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BraveSettingsBackup.Capture(_root, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => BraveSettingsBackup.Extract(Fixture, cancellation.Token));
    }

    public void Dispose() => Directory.Delete(_root, true);
}