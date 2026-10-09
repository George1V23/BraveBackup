using System.Text.Json;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public class BraveBookmarksParserTests
{
    [Fact]
    public void Parse_ParsesBookmarkRootsAndNestedEntries()
    {
        var bookmarks = BraveBookmarksParser.Parse(LoadFixture("Bookmarks.sample.json"));

        Assert.Equal(1, bookmarks.Version);
        Assert.Equal(new[] { "bookmark_bar", "other", "synced" }, bookmarks.Roots.Select(root => root.Key));

        var bookmarkBar = bookmarks.Roots[0];
        Assert.Equal("Bookmarks bar", bookmarkBar.Name);
        Assert.Equal("1", bookmarkBar.Id);
        Assert.Equal("bar-guid", bookmarkBar.Guid);
        Assert.Equal(2, bookmarkBar.Children.Count);

        var url = bookmarkBar.Children[0];
        Assert.Equal(BraveBookmarkType.Url, url.Type);
        Assert.Equal("Example", url.Name);
        Assert.Equal("https://example.test/", url.Url);
        Assert.Equal("13253760000000000", url.DateAdded);
        Assert.Empty(url.Children);

        var folder = bookmarkBar.Children[1];
        Assert.Equal(BraveBookmarkType.Folder, folder.Type);
        Assert.Equal("Research & notes", folder.Name);
        Assert.Equal("13253760000000004", folder.DateModified);
        Assert.Single(folder.Children);
        Assert.Equal("https://example.test/path?q=one%20two", folder.Children[0].Url);
        Assert.Equal("Café", bookmarks.Roots[2].Children[0].Name);
    }

    [Fact]
    public void Parse_AllowsAnEmptyRootsObject()
    {
        var bookmarks = BraveBookmarksParser.Parse(LoadFixture("Bookmarks.empty-roots.json"));

        Assert.Equal(1, bookmarks.Version);
        Assert.Empty(bookmarks.Roots);
    }

    [Fact]
    public void Parse_RejectsBookmarkEntriesWithoutRequiredUrl()
    {
        const string json = """
            {
              "version": 1,
              "roots": {
                "bookmark_bar": {
                  "name": "Bookmarks bar",
                  "children": [
                    { "type": "url", "name": "Missing URL" }
                  ]
                }
              }
            }
            """;

        var exception = Assert.Throws<JsonException>(() => BraveBookmarksParser.Parse(json));

        Assert.Contains("url", exception.Message);
    }

    [Fact]
    public void Parse_RejectsUnsupportedBookmarkEntryTypes()
    {
        const string json = """
            {
              "version": 1,
              "roots": {
                "bookmark_bar": {
                  "name": "Bookmarks bar",
                  "children": [
                    { "type": "unknown", "name": "Unsupported" }
                  ]
                }
              }
            }
            """;

        var exception = Assert.Throws<JsonException>(() => BraveBookmarksParser.Parse(json));

        Assert.Contains("Unsupported bookmark type", exception.Message);
    }

    [Fact]
    public void Parse_RejectsMalformedJson()
    {
        Assert.ThrowsAny<JsonException>(() => BraveBookmarksParser.Parse("{"));
    }

    private static string LoadFixture(string fileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(fixturePath);
    }
}
