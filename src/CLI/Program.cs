using System.Text;
using BraveBackup.Core;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== Brave Browser Bookmark Exporter ===");
Console.WriteLine();

// Step 1: Detect installed Brave applications
Console.WriteLine("Detecting installed Brave Browser applications...");
Console.WriteLine();

IReadOnlyList<BraveAppInfo> installedApps;
try
{
    installedApps = BraveAppDetector.DetectInstalledApps();
}
catch (PlatformNotSupportedException ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    return;
}

if (installedApps.Count == 0)
{
    Console.WriteLine("No Brave Browser installations found.");
    Console.WriteLine();
    Console.WriteLine("Supported channels: Stable, Beta, Nightly");
    return;
}

// Display detected applications
Console.WriteLine($"Found {installedApps.Count} Brave Browser installation(s):");
Console.WriteLine();

for (int i = 0; i < installedApps.Count; i++)
{
    var app = installedApps[i];
    var channelName = app.Channel switch
    {
        BraveAppChannel.Stable => "Stable",
        BraveAppChannel.Beta => "Beta",
        BraveAppChannel.Nightly => "Nightly",
        _ => app.Channel.ToString()
    };

    Console.WriteLine($"  [{i + 1}] {channelName} (v{app.Version})");
    Console.WriteLine($"      Program: {app.ProgramPath}");
    Console.WriteLine($"      Data:    {app.UserDataDirectory}");
    Console.WriteLine();
}

// Step 2: Let user choose an application
Console.Write("Select an application (number): ");
var input = Console.ReadLine()?.Trim();

if (string.IsNullOrEmpty(input))
{
    Console.WriteLine("No selection made. Exiting.");
    return;
}

if (!int.TryParse(input, out var selectedIndex) || selectedIndex < 1 || selectedIndex > installedApps.Count)
{
    Console.WriteLine($"Invalid selection. Please enter a number between 1 and {installedApps.Count}.");
    return;
}

var selectedApp = installedApps[selectedIndex - 1];
var selectedChannelName = selectedApp.Channel switch
{
    BraveAppChannel.Stable => "Stable",
    BraveAppChannel.Beta => "Beta",
    BraveAppChannel.Nightly => "Nightly",
    _ => selectedApp.Channel.ToString()
};

Console.WriteLine();
Console.WriteLine($"Selected: {selectedChannelName} (v{selectedApp.Version})");
Console.WriteLine();

// Step 3: Find profiles for the selected application
Console.WriteLine("Searching for profiles...");
Console.WriteLine();

var profiles = BraveProfileFinder.FindProfiles(selectedApp);

if (profiles.Count == 0)
{
    Console.WriteLine("No profiles found for this application.");
    Console.WriteLine($"User data directory: {selectedApp.UserDataDirectory}");
    return;
}

// Display found profiles
Console.WriteLine($"Found {profiles.Count} profile(s):");
Console.WriteLine();

for (int i = 0; i < profiles.Count; i++)
{
    var profile = profiles[i];
    var hasBookmarks = BraveProfileFinder.HasBookmarks(profile);
    var bookmarkStatus = hasBookmarks ? "Bookmarks available" : "No bookmarks file found";

    Console.WriteLine($"  [{i + 1}] {profile.ProfileName}");
    Console.WriteLine($"      Directory: {profile.ProfileDirectory}");
    Console.WriteLine($"      Bookmarks: {bookmarkStatus}");
    Console.WriteLine();
}

// Step 4: Let user choose a profile
Console.Write("Select a profile (number): ");
var profileInput = Console.ReadLine()?.Trim();

if (string.IsNullOrEmpty(profileInput))
{
    Console.WriteLine("No selection made. Exiting.");
    return;
}

if (!int.TryParse(profileInput, out var selectedProfileIndex) || selectedProfileIndex < 1 || selectedProfileIndex > profiles.Count)
{
    Console.WriteLine($"Invalid selection. Please enter a number between 1 and {profiles.Count}.");
    return;
}

var selectedProfile = profiles[selectedProfileIndex - 1];

if (!BraveProfileFinder.HasBookmarks(selectedProfile))
{
    Console.WriteLine($"No bookmarks file found at: {selectedProfile.BookmarksPath}");
    return;
}

// Step 5: Export bookmarks
Console.WriteLine();
Console.WriteLine($"Exporting bookmarks from profile '{selectedProfile.ProfileName}'...");
Console.WriteLine();

try
{
    var bookmarksJson = File.ReadAllText(selectedProfile.BookmarksPath);
    var backupModel = BraveBookmarksExporter.ExportJson(bookmarksJson);

    Console.WriteLine("Export successful!");
    Console.WriteLine();
    Console.WriteLine($"Schema version: {backupModel.SchemaVersion}");
    Console.WriteLine($"Top-level folders: {backupModel.Bookmarks.Count}");

    // Print bookmark summary
    Console.WriteLine();
    PrintBookmarkSummary(backupModel.Bookmarks, 0);

    Console.WriteLine();
    Console.WriteLine("Done.");
}
catch (Exception ex)
{
    Console.WriteLine($"Error exporting bookmarks: {ex.Message}");
}

static void PrintBookmarkSummary(IReadOnlyList<BackupBookmark> bookmarks, int depth)
{
    var indent = new string(' ', depth * 2);

    foreach (var bookmark in bookmarks)
    {
        if (bookmark.Type == BackupBookmarkType.Folder)
        {
            Console.WriteLine($"{indent}[Folder] {bookmark.Name} ({bookmark.Children.Count} items)");
            if (bookmark.Children.Count > 0)
            {
                PrintBookmarkSummary(bookmark.Children, depth + 1);
            }
        }
        else
        {
            var urlDisplay = bookmark.Url?.Length > 60 ? bookmark.Url[..60] + "..." : bookmark.Url;
            Console.WriteLine($"{indent}  [URL] {bookmark.Name}");
            Console.WriteLine($"{indent}        {urlDisplay}");
        }
    }
}
