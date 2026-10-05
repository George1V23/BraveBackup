namespace BraveBackup.Core;

public sealed record BackupModel(
    int SchemaVersion,
    IReadOnlyList<BackupBookmark> Bookmarks);

public enum BackupBookmarkType
{
    Folder,
    Url
}

public sealed record BackupBookmark(
    BackupBookmarkType Type,
    string Name,
    string? Url,
    IReadOnlyList<BackupBookmark> Children);
