using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Contains counts read from profile files; null means the count could not be established.</summary>
/// <param name="BookmarkCount">The number of URL bookmarks, excluding folders.</param>
/// <param name="ExtensionCount">The number of discovered extension directories with version subdirectories.</param>
public sealed record BraveProfileSummary(int? BookmarkCount, int? ExtensionCount);

/// <summary>Reads profile counts through the existing bookmark and extension readers without modifying browser files.</summary>
public static class BraveProfileSummaryReader
{
    /// <summary>Reads independent counts so one unreadable category does not hide the profile or the other count.</summary>
    /// <param name="profile">The discovered profile to inspect.</param>
    /// <returns>Counts of on-disk data, with unavailable counts represented by null.</returns>
    public static BraveProfileSummary Read(BraveProfileInfo profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        int? bookmarks = null;
        int? extensions = null;
        try
        {
            var parsed = BraveBookmarksParser.Parse(File.ReadAllText(profile.BookmarksPath));
            bookmarks = parsed.Roots.Sum(root => CountUrls(root.Children));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        try
        {
            if (Directory.Exists(profile.ProfileDirectory))
                extensions = BraveProfileDetailsReader.GetExtensions(profile).Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        return new BraveProfileSummary(bookmarks, extensions);
    }

    /// <summary>Counts URL entries recursively, excluding root and child folders.</summary>
    private static int CountUrls(IReadOnlyList<BraveBookmark> bookmarks) =>
        bookmarks.Sum(bookmark => bookmark.Type == BraveBookmarkType.Url ? 1 : CountUrls(bookmark.Children));
}