using System.Text;
using BraveBackup.Core;
using Xunit;

namespace Brave.Tests;

/// <summary>Checks browser HTML against a synthetic fixture based on Chromium's published writer.</summary>
public sealed class BraveBookmarksHtmlExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BraveHtmlExport_" + Guid.NewGuid().ToString("N"));
    public BraveBookmarksHtmlExporterTests() => Directory.CreateDirectory(_root);

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static BraveBookmarksFile ParsedFixture =>
        BraveBookmarksParser.Parse(Fixture("Bookmarks.html-export.json"));

    [Fact]
    public void Export_MatchesBrowserStructureEscapingRootOrderAndDates()
    {
        var expected = Fixture("Bookmarks.html-export.expected.html").Replace("\r\n", "\n").Replace("\n", "\r\n");
        var actual = BraveBookmarksHtmlExporter.ExportJson(Fixture("Bookmarks.html-export.json"));
        Assert.Equal(expected, actual);
        Assert.DoesNotContain("Other bookmarks</H3>", actual);
        Assert.DoesNotContain("Mobile bookmarks</H3>", actual);
        Assert.DoesNotContain("\n", actual.Replace("\r\n", ""));
    }

    [Fact]
    public void Export_EscapesQuotedHrefAndMarkupTitlesWithoutChangingUnicode()
    {
        var node = new BraveBookmark(BraveBookmarkType.Url, "<script>\"Café\" & '😀'</script>", null, null, "0", null,
            "https://example.test/?q=\"test\"&x=1", Array.Empty<BraveBookmark>());
        var root = new BraveBookmarkRoot("bookmark_bar", "Toolbar", null, null, new[] { node });
        var html = BraveBookmarksHtmlExporter.Export(new BraveBookmarksFile(1, new[] { root }));
        Assert.Contains("HREF=\"https://example.test/?q=&quot;test&quot;&x=1\"", html);
        Assert.Contains("&lt;script&gt;&quot;Café&quot; &amp; &#39;😀&#39;&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
    }

    [Theory]
    [InlineData(null, "0")]
    [InlineData("invalid", "0")]
    [InlineData("0", "0")]
    [InlineData("11644473600000000", "0")]
    [InlineData("11644473601000000", "1")]
    [InlineData("11644473599999999", "-1")]
    [InlineData("13253760001999999", "1609286401")]
    [InlineData("9223372036854775807", "9223372036854775807")]
    [InlineData("-9223372036854775808", "-9223372036854775808")]
    public void Export_ConvertsChromiumTimesToUnixSeconds(string? chromiumTime, string expected)
    {
        var node = new BraveBookmark(BraveBookmarkType.Url, "Example", null, null, chromiumTime, null,
            "https://example.test/", Array.Empty<BraveBookmark>());
        var root = new BraveBookmarkRoot("bookmark_bar", "Toolbar", null, null, new[] { node })
        {
            DateAdded = chromiumTime, DateModified = chromiumTime
        };
        var html = BraveBookmarksHtmlExporter.Export(new BraveBookmarksFile(1, new[] { root }));
        Assert.Contains($"<DT><H3 ADD_DATE=\"{expected}\" LAST_MODIFIED=\"{expected}\"", html);
        Assert.Contains($"HREF=\"https://example.test/\" ADD_DATE=\"{expected}\"", html);
    }

    [Fact]
    public void Export_HandlesEmptyAndAdditionalRootsWithoutSilentlyDroppingData()
    {
        var empty = BraveBookmarksHtmlExporter.ExportJson("{\"version\":1,\"roots\":{}}");
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>", empty);
        Assert.EndsWith("</DL><p>\r\n", empty);
        var extra = new BraveBookmarkRoot("extra", "Custom root", null, null, Array.Empty<BraveBookmark>());
        Assert.Contains(">Custom root</H3>",
            BraveBookmarksHtmlExporter.Export(new BraveBookmarksFile(1, new[] { extra })));
        Assert.Throws<InvalidDataException>(() =>
            BraveBookmarksHtmlExporter.Export(new BraveBookmarksFile(1, new[] { extra, extra })));
        Assert.Throws<ArgumentNullException>(() => BraveBookmarksHtmlExporter.Export(null!));
    }

    [Fact]
    public void Parser_RetainsRootTimesWithoutBreakingLegacyJsonForMalformedOptionalRootMetadata()
    {
        var parsed = ParsedFixture;
        Assert.Equal("13253760000000000", parsed.Roots.Single(root => root.Key == "bookmark_bar").DateAdded);
        var malformed = Fixture("Bookmarks.html-export.json")
            .Replace("\"date_modified\": \"13253760002000000\"", "\"date_modified\": false");
        var read = BraveBookmarksParser.Parse(malformed);
        Assert.Null(read.Roots.Single(root => root.Key == "bookmark_bar").DateModified);
        Assert.Equal(1, BraveBookmarksExporter.Export(read).SchemaVersion);
        Assert.Contains("LAST_MODIFIED=\"0\"", BraveBookmarksHtmlExporter.Export(read));
    }

    [Fact]
    public async Task FileExport_WritesUtf8WithoutBomAndCleansUpItsTemporaryFile()
    {
        var target = Path.Combine(_root, "nested", "bookmarks.html");
        await BraveBookmarksHtmlExporter.ExportToFileAsync(ParsedFixture, target);
        var bytes = File.ReadAllBytes(target);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(BraveBookmarksHtmlExporter.Export(ParsedFixture), Encoding.UTF8.GetString(bytes));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(target)!));
    }

    [Fact]
    public async Task FileExport_RejectsProfileWritesAndPreservesExistingOutputOnCancellationOrFailure()
    {
        var profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        var source = Path.Combine(profile, "Bookmarks");
        File.WriteAllText(source, "original source");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BraveBookmarksHtmlExporter.ExportToFileAsync(ParsedFixture, source));
        Assert.Equal("original source", File.ReadAllText(source));
        var target = Path.Combine(_root, "bookmarks.html");
        File.WriteAllText(target, "previous export");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BraveBookmarksHtmlExporter.ExportToFileAsync(ParsedFixture, target, cancel.Token));
        Assert.Equal("previous export", File.ReadAllText(target));
        var folderTarget = Path.Combine(_root, "folder");
        Directory.CreateDirectory(folderTarget);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            BraveBookmarksHtmlExporter.ExportToFileAsync(ParsedFixture, folderTarget));
        Assert.Empty(Directory.GetFiles(_root, ".bravebackup-*.tmp"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}