using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BraveBackup.Core;

public sealed record BraveProfileInfo(
    string ProfileName,
    string ProfileDirectory,
    string BookmarksPath);

public static class BraveProfileFinder
{
    public static IReadOnlyList<BraveProfileInfo> FindProfiles(BraveAppInfo appInfo)
    {
        var userDataDir = appInfo.UserDataDirectory;

        if (!Directory.Exists(userDataDir))
        {
            return Array.Empty<BraveProfileInfo>().AsReadOnly();
        }

        var profiles = new List<BraveProfileInfo>();

        // Check for default profile
        var defaultProfileDir = Path.Combine(userDataDir, "Default");
        if (Directory.Exists(defaultProfileDir))
        {
            var bookmarksPath = Path.Combine(defaultProfileDir, "Bookmarks");
            profiles.Add(new BraveProfileInfo(
                "Default",
                defaultProfileDir,
                bookmarksPath));
        }

        // Check for numbered profiles (Profile 1, Profile 2, etc.)
        var profileDirs = Directory.GetDirectories(userDataDir, "Profile *")
            .OrderBy(d => d)
            .ToList();

        foreach (var profileDir in profileDirs)
        {
            var profileName = Path.GetFileName(profileDir);
            var bookmarksPath = Path.Combine(profileDir, "Bookmarks");
            profiles.Add(new BraveProfileInfo(
                profileName,
                profileDir,
                bookmarksPath));
        }

        // Check for named profiles (e.g., "Person 1", custom names)
        var allDirs = Directory.GetDirectories(userDataDir)
            .Where(d =>
            {
                var name = Path.GetFileName(d);
                return name != "Default" && !name.StartsWith("Profile ") &&
                       !name.StartsWith("GrShaderCache") &&
                       name != "ShaderCache" &&
                       name != "GPUCache" &&
                       name != "DawnGraphiteCache" &&
                       name != "DawnWebGPUCache" &&
                       name != "ArcCache" &&
                       name != "Cache" &&
                       name != "CodeCache" &&
                       name != "GCDATA" &&
                       name != "paks" &&
                       name != "Local Extension Settings" &&
                       name != "Extensions" &&
                       name != "Site List Database" &&
                       name != "SafetyTips" &&
                       name != "Trust Tokens" &&
                       name != "Network" &&
                       name != "Network Persistent State" &&
                       name != "Storage" &&
                       name != "Breadcrumbs" &&
                       name != "BrowserMetrics" &&
                       name != "Crashpad" &&
                       name != "CrashpadMetrics" &&
                       name != "First Run" &&
                       name != "FlagsState" &&
                       name != "Last Version" &&
                       name != "Variations";
            })
            .OrderBy(d => d)
            .ToList();

        foreach (var profileDir in allDirs)
        {
            var profileName = Path.GetFileName(profileDir);
            var bookmarksPath = Path.Combine(profileDir, "Bookmarks");
            profiles.Add(new BraveProfileInfo(
                profileName,
                profileDir,
                bookmarksPath));
        }

        return profiles.AsReadOnly();
    }

    public static bool HasBookmarks(BraveProfileInfo profileInfo)
    {
        return File.Exists(profileInfo.BookmarksPath);
    }
}
