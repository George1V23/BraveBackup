using System.IO;
using System.Text.Json;

namespace BraveBackup.Core;

public static class BraveBookmarksExporter
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static BackupModel Export(BraveBookmarksFile bookmarks)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);

        var roots = bookmarks.Roots
            .Select(root => CreateFolder(root.Name, root.Children))
            .ToArray();

        return new BackupModel(CurrentSchemaVersion, Array.AsReadOnly(roots));
    }

    public static BackupModel ExportJson(string json)
    {
        return Export(BraveBookmarksParser.Parse(json));
    }

    public static void ExportToFile(BackupModel backupModel, string filePath)
    {
        ArgumentNullException.ThrowIfNull(backupModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(backupModel, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    public static async Task ExportToFileAsync(BackupModel backupModel, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(backupModel, JsonOptions);
        await File.WriteAllTextAsync(filePath, json, cancellationToken);
    }

    private static BackupBookmark CreateFolder(string name, IReadOnlyList<BraveBookmark> children)
    {
        return new BackupBookmark(
            BackupBookmarkType.Folder,
            name,
            null,
            Array.AsReadOnly(children.Select(CreateBookmark).ToArray()));
    }

    private static BackupBookmark CreateBookmark(BraveBookmark bookmark)
    {
        IReadOnlyList<BackupBookmark> children = bookmark.Type == BraveBookmarkType.Folder
            ? Array.AsReadOnly(bookmark.Children.Select(CreateBookmark).ToArray())
            : Array.Empty<BackupBookmark>();

        return new BackupBookmark(
            bookmark.Type == BraveBookmarkType.Folder ? BackupBookmarkType.Folder : BackupBookmarkType.Url,
            bookmark.Name,
            bookmark.Type == BraveBookmarkType.Url ? bookmark.Url : null,
            children);
    }
}
