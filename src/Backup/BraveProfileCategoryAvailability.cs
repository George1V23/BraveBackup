using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>
/// Read-only check of which backup categories a profile has any data for. It only inspects file and folder existence
/// (never contents) and treats linked or unreadable locations as absent.
/// </summary>
public static class BraveProfileCategoryAvailability
{
    private static readonly string[] LegacySessionFiles = ["Current Session", "Current Tabs", "Last Session", "Last Tabs"];

    /// <summary>Returns the category IDs (bookmarks, settings, passwords, extensions, sessions, tabmanagerplus) present in the profile.</summary>
    public static IReadOnlySet<string> Detect(string profileDirectory, string? bookmarksPath = null)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        string Path(string name) => System.IO.Path.Combine(profileDirectory, name);
        if (IsFile(bookmarksPath ?? Path("Bookmarks"))) found.Add("bookmarks");
        if (IsFile(Path("Preferences"))) found.Add("settings");
        if (IsFile(Path("Login Data")) || IsFile(Path("Login Data For Account"))) found.Add("passwords");
        if (HasEntries(Path("Extensions")) || HasEntries(Path("Local Extension Settings")) || HasEntries(Path("Sync Extension Settings")))
            found.Add("extensions");
        if (LegacySessionFiles.Any(name => IsFile(Path(name))) || HasSessionFiles(Path("Sessions")) || HasSessionFiles(Path("Sessions_Encrypted")))
            found.Add("sessions");
        if (HasEntries(System.IO.Path.Combine(Path("Local Extension Settings"), SavedWindows.OfficialExtensionId))) found.Add("tabmanagerplus");
        return found;
    }

    private static bool IsFile(string path)
    {
        try { return SnapshotFileAccess.Kind(path) == SnapshotNodeKind.RegularFile; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasEntries(string directory)
    {
        try { return SnapshotFileAccess.Kind(directory) == SnapshotNodeKind.Directory && Directory.EnumerateFileSystemEntries(directory).Any(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool HasSessionFiles(string directory)
    {
        try
        {
            return SnapshotFileAccess.Kind(directory) == SnapshotNodeKind.Directory &&
                   Directory.EnumerateFileSystemEntries(directory).Any(entry =>
                   {
                       var name = System.IO.Path.GetFileName(entry);
                       return name.StartsWith("Session_", StringComparison.Ordinal) || name.StartsWith("Tabs_", StringComparison.Ordinal);
                   });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
