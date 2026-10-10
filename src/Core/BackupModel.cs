namespace BraveBackup.Core;

/// <summary>
/// Represents the root container for a BraveBackup file export.
/// </summary>
/// <param name="SchemaVersion">The schema version of the backup format.</param>
/// <param name="Bookmarks">The list of exported root bookmark folders or items.</param>
public sealed record BackupModel(
    int SchemaVersion,
    IReadOnlyList<BackupBookmark> Bookmarks);

/// <summary>
/// Specifies the type of bookmark entry in a backup file.
/// </summary>
public enum BackupBookmarkType
{
    /// <summary>
    /// A bookmark folder containing child bookmarks or folders.
    /// </summary>
    Folder,

    /// <summary>
    /// An individual bookmark URL item.
    /// </summary>
    Url
}

/// <summary>
/// Represents a bookmark entry (folder or URL) in a backup file.
/// </summary>
/// <param name="Type">The bookmark entry type (Folder or Url).</param>
/// <param name="Name">The display title or name of the bookmark.</param>
/// <param name="Url">The URL target if the entry is a URL bookmark; otherwise <c>null</c>.</param>
/// <param name="Children">The child bookmarks if the entry is a folder; otherwise empty.</param>
public sealed record BackupBookmark(
    BackupBookmarkType Type,
    string Name,
    string? Url,
    IReadOnlyList<BackupBookmark> Children);
