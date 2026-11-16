using BraveBackup.Core;
using Xunit;

namespace Brave.Tests;

/// <summary>Checks summary counts against isolated profile files using the established parsers.</summary>
public sealed class BraveProfileSummaryReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BraveSummary_" + Guid.NewGuid().ToString("N"));
    public BraveProfileSummaryReaderTests() => Directory.CreateDirectory(_root);
    private BraveProfileInfo Profile => new("Fixture", _root, Path.Combine(_root, "Bookmarks"));

    [Fact]
    public void Read_CountsUrlsAndDiscoveredExtensionsWithoutChangingSourceFiles()
    {
        const string json = """
                            {"version":1,"roots":{"bookmark_bar":{"name":"Bookmarks bar","children":[
                              {"type":"url","name":"One","url":"https://example.test/one"},
                              {"type":"folder","name":"Folder","children":[{"type":"url","name":"Two","url":"https://example.test/two"}]}]}}}
                            """;
        File.WriteAllText(Profile.BookmarksPath, json);
        Directory.CreateDirectory(Path.Combine(_root, "Extensions", "extension-one", "1.0_0"));
        Directory.CreateDirectory(Path.Combine(_root, "Extensions", "extension-one", "2.0_0"));
        Directory.CreateDirectory(Path.Combine(_root, "Extensions", "extension-two", "1.0_0"));
        var summary = BraveProfileSummaryReader.Read(Profile);
        Assert.Equal(2, summary.BookmarkCount);
        Assert.Equal(2, summary.ExtensionCount);
        Assert.Equal(json, File.ReadAllText(Profile.BookmarksPath));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Read_DistinguishesUnavailableBookmarksFromValidEmptyData()
    {
        Assert.Null(BraveProfileSummaryReader.Read(Profile).BookmarkCount);
        Assert.Equal(0, BraveProfileSummaryReader.Read(Profile).ExtensionCount);
        File.WriteAllText(Profile.BookmarksPath, "{ invalid }");
        Assert.Null(BraveProfileSummaryReader.Read(Profile).BookmarkCount);
        File.WriteAllText(Profile.BookmarksPath, "{\"version\":1,\"roots\":{}}");
        Assert.Equal(0, BraveProfileSummaryReader.Read(Profile).BookmarkCount);
        var missing = Profile with
        {
            ProfileDirectory = Path.Combine(_root, "missing"),
            BookmarksPath = Path.Combine(_root, "missing", "Bookmarks")
        };
        Assert.Null(BraveProfileSummaryReader.Read(missing).ExtensionCount);
        Assert.Throws<ArgumentNullException>(() => BraveProfileSummaryReader.Read(null!));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}