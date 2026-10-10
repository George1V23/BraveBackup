using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>
/// Contains information about a discovered Brave profile.
/// </summary>
/// <param name="ProfileName">The display or configured name of the profile.</param>
/// <param name="ProfileDirectory">The absolute path to the profile directory on disk.</param>
/// <param name="BookmarksPath">The absolute path to the profile's Bookmarks file.</param>
public sealed record BraveProfileInfo(
    string ProfileName,
    string ProfileDirectory,
    string BookmarksPath);

/// <summary>
/// Provides methods to discover Brave Browser profiles from user data directories and check bookmark availability.
/// </summary>
public static class BraveProfileFinder
{
    /// <summary>
    /// Discovers all browser profiles for the specified Brave application installation.
    /// </summary>
    /// <param name="appInfo">The installed Brave application metadata.</param>
    /// <returns>A read-only list of discovered <see cref="BraveProfileInfo"/> instances.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="appInfo"/> is null.</exception>
    public static IReadOnlyList<BraveProfileInfo> FindProfiles(BraveAppInfo appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        return FindProfiles(appInfo.UserDataDirectory);
    }

    /// <summary>
    /// Discovers browser profiles within the specified user data directory using <c>Local State</c> or filesystem fallback.
    /// </summary>
    /// <param name="userDataDir">The path to the browser's User Data directory.</param>
    /// <returns>A read-only list of discovered <see cref="BraveProfileInfo"/> instances.</returns>
    public static IReadOnlyList<BraveProfileInfo> FindProfiles(string? userDataDir)
    {
        if (string.IsNullOrWhiteSpace(userDataDir) || !Directory.Exists(userDataDir))
        {
            return Array.Empty<BraveProfileInfo>().AsReadOnly();
        }

        var localStatePath = Path.Combine(userDataDir, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                var profilesFromLocalState = ParseLocalStateProfiles(userDataDir, localStatePath);
                if (profilesFromLocalState is not null)
                {
                    return profilesFromLocalState;
                }
            }
            catch
            {
                // If Local State is corrupted, fall back to directory inspection
            }
        }

        return FallbackDiscoverProfiles(userDataDir);
    }

    /// <summary>
    /// Checks whether the specified profile has an existing Bookmarks file on disk.
    /// </summary>
    /// <param name="profileInfo">The profile metadata to inspect.</param>
    /// <returns><c>true</c> if a Bookmarks file exists for the profile; otherwise <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="profileInfo"/> is null.</exception>
    public static bool HasBookmarks(BraveProfileInfo profileInfo)
    {
        ArgumentNullException.ThrowIfNull(profileInfo);
        return !string.IsNullOrEmpty(profileInfo.BookmarksPath) && File.Exists(profileInfo.BookmarksPath);
    }

    private static IReadOnlyList<BraveProfileInfo>? ParseLocalStateProfiles(string userDataDir, string localStatePath)
    {
        var json = File.ReadAllText(localStatePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("profile", out var profileElement) || profileElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        JsonElement infoCacheElement = default;
        bool hasInfoCache = false;

        if (profileElement.TryGetProperty("info_cache", out var infoCache) && infoCache.ValueKind == JsonValueKind.Object)
        {
            infoCacheElement = infoCache;
            hasInfoCache = true;
        }
        else if (profileElement.TryGetProperty("profiles_attributes_storage", out var attributesStorage) && attributesStorage.ValueKind == JsonValueKind.Object)
        {
            infoCacheElement = attributesStorage;
            hasInfoCache = true;
        }

        if (!hasInfoCache)
        {
            return null;
        }

        var profileEntries = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var prop in infoCacheElement.EnumerateObject())
        {
            profileEntries[prop.Name] = prop.Value;
        }

        List<string>? profilesOrder = null;
        if (profileElement.TryGetProperty("profiles_order", out var orderElement) && orderElement.ValueKind == JsonValueKind.Array)
        {
            profilesOrder = new List<string>();
            foreach (var item in orderElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } orderKey && !string.IsNullOrWhiteSpace(orderKey))
                {
                    profilesOrder.Add(orderKey);
                }
            }
        }

        var orderedKeys = new List<string>();
        if (profilesOrder is not null)
        {
            foreach (var key in profilesOrder)
            {
                if (profileEntries.ContainsKey(key) && !orderedKeys.Contains(key))
                {
                    orderedKeys.Add(key);
                }
            }
        }

        foreach (var key in profileEntries.Keys)
        {
            if (!orderedKeys.Contains(key))
            {
                orderedKeys.Add(key);
            }
        }

        var profiles = new List<BraveProfileInfo>();
        foreach (var profileKey in orderedKeys)
        {
            var profileDir = Path.Combine(userDataDir, profileKey);
            if (!Directory.Exists(profileDir))
            {
                continue;
            }

            var entry = profileEntries[profileKey];
            var profileName = profileKey;

            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("name", out var nameProp) &&
                nameProp.ValueKind == JsonValueKind.String)
            {
                var nameVal = nameProp.GetString();
                if (!string.IsNullOrWhiteSpace(nameVal))
                {
                    profileName = nameVal;
                }
            }

            var bookmarksPath = Path.Combine(profileDir, "Bookmarks");
            profiles.Add(new BraveProfileInfo(profileName, profileDir, bookmarksPath));
        }

        return profiles.AsReadOnly();
    }

    private static IReadOnlyList<BraveProfileInfo> FallbackDiscoverProfiles(string userDataDir)
    {
        var profiles = new List<BraveProfileInfo>();

        var defaultProfileDir = Path.Combine(userDataDir, "Default");
        if (Directory.Exists(defaultProfileDir))
        {
            profiles.Add(new BraveProfileInfo(
                "Default",
                defaultProfileDir,
                Path.Combine(defaultProfileDir, "Bookmarks")));
        }

        var profileDirs = Directory.GetDirectories(userDataDir, "Profile *")
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var profileDir in profileDirs)
        {
            var profileName = Path.GetFileName(profileDir);
            profiles.Add(new BraveProfileInfo(
                profileName,
                profileDir,
                Path.Combine(profileDir, "Bookmarks")));
        }

        return profiles.AsReadOnly();
    }
}
