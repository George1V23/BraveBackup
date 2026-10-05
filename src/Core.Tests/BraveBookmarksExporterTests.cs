using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public class BraveBookmarksExporterTests
{
    [Fact]
    public void ExportJson_MapsBookmarkRootsAndNestedItemsToCanonicalModel()
    {
        var model = BraveBookmarksExporter.ExportJson(LoadFixture("Bookmarks.sample.json"));

        Assert.Equal(BraveBookmarksExporter.CurrentSchemaVersion, model.SchemaVersion);
        Assert.Equal(new[] { "Bookmarks bar", "Other bookmarks", "Mobile bookmarks" },
            model.Bookmarks.Select(bookmark => bookmark.Name));

        var bookmarkBar = model.Bookmarks[0];
        Assert.Equal(BackupBookmarkType.Folder, bookmarkBar.Type);
        Assert.Null(bookmarkBar.Url);
        Assert.Equal(2, bookmarkBar.Children.Count);

        var url = bookmarkBar.Children[0];
        Assert.Equal(BackupBookmarkType.Url, url.Type);
        Assert.Equal("Example", url.Name);
        Assert.Equal("https://example.test/", url.Url);
        Assert.Empty(url.Children);

        var folder = bookmarkBar.Children[1];
        Assert.Equal(BackupBookmarkType.Folder, folder.Type);
        Assert.Equal("Research & notes", folder.Name);
        Assert.Null(folder.Url);
        Assert.Equal("https://example.test/path?q=one%20two", folder.Children[0].Url);
    }

    [Fact]
    public void ExportJson_RoundTripsThroughCanonicalModel()
    {
        var originalJson = LoadFixture("Bookmarks.sample.json");
        var model = BraveBookmarksExporter.ExportJson(originalJson);
        var serialized = System.Text.Json.JsonSerializer.Serialize(model);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<BackupModel>(serialized);

        Assert.NotNull(roundTripped);
        Assert.Equal(BraveBookmarksExporter.CurrentSchemaVersion, roundTripped!.SchemaVersion);
        Assert.Equal(new[] { "Bookmarks bar", "Other bookmarks", "Mobile bookmarks" },
            roundTripped.Bookmarks.Select(bookmark => bookmark.Name));
        Assert.Equal("https://example.test/path?q=one%20two", roundTripped.Bookmarks[0].Children[1].Children[0].Url);
        Assert.Equal("Café", roundTripped.Bookmarks[2].Children[0].Name);
    }

    [Fact]
    public void Export_DoesNotIncludeBraveSpecificIdentifiersOrTimestamps()
    {
        var model = BraveBookmarksExporter.Export(BraveBookmarksParser.Parse(LoadFixture("Bookmarks.sample.json")));
        var serialized = System.Text.Json.JsonSerializer.Serialize(model);

        Assert.DoesNotContain("guid-example", serialized);
        Assert.DoesNotContain("13253760000000000", serialized);
        Assert.DoesNotContain("\"id\"", serialized);
    }

    [Fact]
    public void Export_RejectsNullBookmarks()
    {
        Assert.Throws<ArgumentNullException>(() => BraveBookmarksExporter.Export(null!));
    }

    private static string LoadFixture(string fileName)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
        return File.ReadAllText(fixturePath);
    }
}
