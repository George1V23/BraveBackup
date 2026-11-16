using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BraveBackup.Extensions.TabManagerPlus;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class SavedWindowsTests
{
    internal static byte[] Source()
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "TabManagerPlus", "storage", "table-and-wal.expected.json")));
        return Encoding.UTF8.GetBytes(expected.RootElement.GetProperty("value").GetRawText());
    }

    private static JsonObject SourceObject() => JsonNode.Parse(Source())!.AsObject();

    private static SavedWindowCollection Parse(JsonNode value) =>
        SavedWindows.Parse(Encoding.UTF8.GetBytes(value.ToJsonString()), "7.0.0");

    [Fact]
    public void ReadsSavedWindowMetadataWithoutInferringAbsentFields()
    {
        var result = SavedWindows.Parse(Source(), "7.0.0");
        var window = Assert.Single(result.Windows);
        Assert.Equal("fixture-window", window.Id);
        Assert.Equal("Synthetic work", window.Name);
        Assert.Equal("blue", window.Color);
        Assert.True(window.CustomName);
        Assert.False(window.Incognito);
        Assert.Equal(1700000000000L, window.Date);
        Assert.Null(window.Updated);
        Assert.Equal(0d, window.Order);
        Assert.Equal(1200, window.Geometry.Width);
        Assert.Null(window.Geometry.Left);
        Assert.Null(window.Geometry.Focused);
        var tab = Assert.Single(window.Tabs);
        Assert.Equal("https://example.invalid/current", tab.Url);
        Assert.Equal("Synthetic tab", tab.Title);
        Assert.True(tab.Pinned);
        Assert.True(tab.Active);
        Assert.Null(tab.Incognito);
        Assert.Equal(1, result.TabCount);
    }

    [Fact]
    public void PreservesPrivateFlagsGeometryOptionalFieldsAndTabArrayOrder()
    {
        var source = SourceObject();
        var window = source["fixture-window"]!;
        window["incognito"] = true;
        window["updated"] = 1700000000020L;
        window["windowsInfo"]!["incognito"] = true;
        window["windowsInfo"]!["left"] = -1200;
        window["windowsInfo"]!["top"] = -25;
        var tabs = window["tabs"]!.AsArray();
        var other = tabs[0]!.DeepClone();
        other["index"] = 9;
        other["url"] = "javascript:fixture-only-never-executed";
        other.AsObject().Remove("title");
        other.AsObject().Remove("active");
        tabs.Insert(0, other);
        var parsed = Assert.Single(Parse(source).Windows);
        Assert.True(parsed.Incognito);
        Assert.True(parsed.Geometry.Incognito);
        Assert.Equal(-1200, parsed.Geometry.Left);
        Assert.Equal(-25, parsed.Geometry.Top);
        Assert.Equal(1700000000020L, parsed.Updated);
        Assert.Equal(new[] { 9, 0 }, parsed.Tabs.Select(tab => tab.Index));
        Assert.Null(parsed.Tabs[0].Title);
        Assert.Null(parsed.Tabs[0].Active);
    }

    [Fact]
    public void ExplicitEmptyMapSucceeds()
    {
        Assert.Empty(SavedWindows.Parse("{}"u8.ToArray(), "7.0.0").Windows);
    }

    [Theory]
    [InlineData("6.0.0")]
    [InlineData("7.0.1")]
    [InlineData("7.0")]
    public void UnsupportedVersionsDoNotBorrowCurrentSchema(string version)
    {
        Assert.Throws<InvalidDataException>(() => SavedWindows.Parse(Source(), version));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("name")]
    [InlineData("color")]
    [InlineData("date")]
    [InlineData("sessionStartTime")]
    [InlineData("incognito")]
    [InlineData("customName")]
    [InlineData("windowsInfo")]
    [InlineData("tabs")]
    public void RequiredFieldsCannotBeSilentlyDefaulted(string field)
    {
        var source = SourceObject();
        source["fixture-window"]!.AsObject().Remove(field);
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Theory]
    [InlineData("url")]
    [InlineData("index")]
    public void MissingNavigationOrTabIndexFailsInsteadOfRepairing(string field)
    {
        var source = SourceObject();
        var tab = source["fixture-window"]!["tabs"]![0]!.AsObject();
        tab.Remove(field);
        tab["pendingUrl"] = "https://example.invalid/not-a-fallback";
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":{\"id\":\"a\",\"id\":\"b\"}}")]
    [InlineData("{\"a\":{},\"a\":{}}")]
    public void InvalidOrDuplicateJsonFailsWithContentFreeMessage(string json)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            SavedWindows.Parse(Encoding.UTF8.GetBytes(json), "7.0.0"));
        Assert.Equal("Unsupported or invalid Tab Manager Plus saved-window data.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void RejectsMismatchedIdentityAndRepeatedTabIndexes()
    {
        var source = SourceObject();
        source["fixture-window"]!["id"] = "other";
        Assert.Throws<InvalidDataException>(() => Parse(source));
        source = SourceObject();
        var tabs = source["fixture-window"]!["tabs"]!.AsArray();
        tabs.Add(tabs[0]!.DeepClone());
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Fact]
    public void RejectsWrongTypesWithoutLeakingValues()
    {
        foreach (var (field, json) in new[]
                 {
                     ("date", "-1"), ("order", "1e999"),
                     ("updated", "null"), ("customName", "\"secret\""), ("name", "true")
                 })
        {
            var source = SourceObject();
            source["fixture-window"]![field] = JsonNode.Parse(json);
            var error = Assert.Throws<InvalidDataException>(() => Parse(source));
            Assert.DoesNotContain("secret", error.Message);
        }
    }

    [Fact]
    public void StableOrderingMatchesSourceRulesIncludingNumericObjectKeys()
    {
        var template = SourceObject()["fixture-window"]!;
        var map = new JsonObject();
        foreach (var (id, order, date) in new (string, double?, long)[]
                     { ("z", null, 30), ("b", 2, 10), ("2", 2, 20), ("1", 2, 40), ("a", -1, 20), ("old", null, 10) })
        {
            var window = template.DeepClone();
            window["id"] = id;
            window["date"] = date;
            window.AsObject().Remove("order");
            if (order.HasValue) window["order"] = order.Value;
            map[id] = window;
        }

        Assert.Equal(new[] { "a", "1", "2", "b", "z", "old" }, Parse(map).Windows.Select(window => window.Id));
    }

    [Fact]
    public void UnknownMetadataIsNotCopiedIntoPortableModelsButDuplicatesAreRejected()
    {
        var source = SourceObject();
        source["fixture-window"]!["unrelated"] = "do-not-copy";
        Assert.DoesNotContain("do-not-copy", JsonSerializer.Serialize(Parse(source)));
        var json = source.ToJsonString().Replace("\"unrelated\":\"do-not-copy\"",
            "\"unrelated\":{\"nested\":1,\"nested\":2}");
        Assert.Throws<InvalidDataException>(() => SavedWindows.Parse(Encoding.UTF8.GetBytes(json), "7.0.0"));
    }

    [Fact]
    public void WindowCountAndTextLengthAreBounded()
    {
        var map = new JsonObject();
        var template = SourceObject()["fixture-window"]!;
        for (var i = 0; i <= SavedWindows.MaximumWindows; i++)
        {
            var window = template.DeepClone();
            window["id"] = "w" + i;
            map["w" + i] = window;
        }

        Assert.Throws<InvalidDataException>(() => Parse(map));
        var source = SourceObject();
        source["fixture-window"]!["name"] = new string('x', 32769);
        Assert.Throws<InvalidDataException>(() => Parse(source));
    }

    [Fact]
    public void HonorsCancellation()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => SavedWindows.Parse(Source(), "7.0.0", cancel.Token));
    }

    [Fact]
    public void MalformedUtf8ProducesTheSanitizedDataError()
    {
        var bytes = Source();
        var at = Array.IndexOf(bytes, (byte)'S');
        Assert.True(at > 0);
        bytes[at] = 0xff;
        Assert.Throws<InvalidDataException>(() => SavedWindows.Parse(bytes, "7.0.0"));
    }

    [Fact]
    public void UnpairedEscapedSurrogateProducesTheSanitizedDataError()
    {
        var json = Encoding.UTF8.GetString(Source()).Replace("Synthetic work", "\\uD800");
        Assert.Throws<InvalidDataException>(() => SavedWindows.Parse(Encoding.UTF8.GetBytes(json), "7.0.0"));
    }
}