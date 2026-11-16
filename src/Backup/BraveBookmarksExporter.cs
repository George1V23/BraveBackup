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
    /// <exception cref="InvalidOperationException">The destination is inside browser data or traverses a filesystem link.</exception>
    /// <remarks>Writes through a new temporary sibling and replaces the destination only after a successful write.</remarks>
    public static void ExportToFile(BackupModel backupModel, string filePath)
    {
        ArgumentNullException.ThrowIfNull(backupModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var json = JsonSerializer.Serialize(backupModel, JsonOptions);
        var destination = ExportDestinationGuard.Validate(filePath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".bravebackup-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
                writer.Write(json);
            ExportDestinationGuard.Validate(destination);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
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
    /// <exception cref="InvalidOperationException">The destination is inside browser data or traverses a filesystem link.</exception>
    /// <remarks>Failed or cancelled writes preserve an existing backup and remove their temporary file.</remarks>
    public static async Task ExportToFileAsync(BackupModel backupModel, string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backupModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        cancellationToken.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(backupModel, JsonOptions);
        var destination = ExportDestinationGuard.Validate(filePath);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".bravebackup-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None,
                             4096, FileOptions.Asynchronous))
            await using (var writer = new StreamWriter(stream))
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ExportDestinationGuard.Validate(destination);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
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