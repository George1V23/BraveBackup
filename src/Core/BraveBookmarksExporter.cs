using System.IO;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>
/// Provides functionality for converting Brave browser bookmark models into standardized backup models and exporting them to JSON or files.
/// </summary>
public static class BraveBookmarksExporter
{
    /// <summary>
    /// Current version of the backup format schema.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// JSON serialization options configured for backup file formatting.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Exports parsed Brave browser bookmarks into a standardized <see cref="BackupModel"/>.
    /// </summary>
    /// <param name="bookmarks">The parsed Brave bookmarks file representation.</param>
    /// <returns>A structured <see cref="BackupModel"/> containing the exported bookmarks.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="bookmarks"/> is null.</exception>
    public static BackupModel Export(BraveBookmarksFile bookmarks)
    {
        ArgumentNullException.ThrowIfNull(bookmarks);

        var roots = bookmarks.Roots
            .Select(root => CreateFolder(root.Name, root.Children))
            .ToArray();

        return new BackupModel(CurrentSchemaVersion, Array.AsReadOnly(roots));
    }

    /// <summary>
    /// Parses a raw Brave bookmarks JSON string and exports it into a standardized <see cref="BackupModel"/>.
    /// </summary>
    /// <param name="json">The raw Chromium / Brave Bookmarks JSON string.</param>
    /// <returns>A structured <see cref="BackupModel"/> containing the exported bookmarks.</returns>
    public static BackupModel ExportJson(string json)
    {
        return Export(BraveBookmarksParser.Parse(json));
    }

    /// <summary>
    /// Serializes and writes a <see cref="BackupModel"/> to the specified file path, creating parent directories if needed.
    /// </summary>
    /// <param name="backupModel">The backup model to export.</param>
    /// <param name="filePath">The target file path where the JSON backup should be saved.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backupModel"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null or whitespace.</exception>
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

    /// <summary>
    /// Asynchronously serializes and writes a <see cref="BackupModel"/> to the specified file path, creating parent directories if needed.
    /// </summary>
    /// <param name="backupModel">The backup model to export.</param>
    /// <param name="filePath">The target file path where the JSON backup should be saved.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task representing the asynchronous export operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backupModel"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="filePath"/> is null or whitespace.</exception>
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
