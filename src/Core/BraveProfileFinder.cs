using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>
/// Contains information about a discovered Brave profile, including optional appearance metadata from <c>Local State</c>.
/// </summary>
/// <param name="ProfileName">The display or configured name of the profile.</param>
/// <param name="ProfileDirectory">The absolute path to the profile directory on disk.</param>
/// <param name="BookmarksPath">The absolute path to the profile's Bookmarks file.</param>
public sealed record BraveProfileInfo(
    string ProfileName,
    string ProfileDirectory,
    string BookmarksPath)
{
    /// <summary>Gets the selected avatar resource index from 0 through 89, or placeholder index 26 when it is unavailable or unsupported.</summary>
    public int AvatarIconIndex { get; init; } = 26;

    /// <summary>Gets the profile's stored highlight color as an ARGB value, if it is a valid integer in Local State.</summary>
    public uint? ProfileHighlightColor { get; init; }

    /// <summary>Gets the stored default-avatar fill color as an ARGB value, if it is a valid integer in Local State.</summary>
    public uint? DefaultAvatarFillColor { get; init; }

    /// <summary>Gets the stored default-avatar stroke color as an ARGB value, if it is a valid integer in Local State.</summary>
    public uint? DefaultAvatarStrokeColor { get; init; }

    /// <summary>Gets the selected account-picture path when its filename names an existing direct child of the profile directory.</summary>
    public string? AvatarPicturePath { get; init; }
}

/// <summary>
/// Provides methods to discover Brave Browser profiles from user data directories, read optional appearance metadata, and check bookmark availability.
/// </summary>
public static class BraveProfileFinder
{
    private const string AvatarIconUrlPrefix = "chrome://theme/IDR_PROFILE_AVATAR_";
    private const int DefaultAvatarIconCount = 90;
    private const int PlaceholderAvatarIconIndex = 26;

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
    /// Appearance metadata is read from the profile entries when available; missing or invalid appearance values use defaults.
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

        if (profileElement.TryGetProperty("info_cache", out var infoCache) &&
            infoCache.ValueKind == JsonValueKind.Object)
        {
            infoCacheElement = infoCache;
            hasInfoCache = true;
        }
        else if (profileElement.TryGetProperty("profiles_attributes_storage", out var attributesStorage) &&
                 attributesStorage.ValueKind == JsonValueKind.Object)
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
        if (profileElement.TryGetProperty("profiles_order", out var orderElement) &&
            orderElement.ValueKind == JsonValueKind.Array)
        {
            profilesOrder = new List<string>();
            foreach (var item in orderElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } orderKey &&
                    !string.IsNullOrWhiteSpace(orderKey))
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
            profiles.Add(new BraveProfileInfo(profileName, profileDir, bookmarksPath)
            {
                AvatarIconIndex = GetAvatarIconIndex(entry),
                ProfileHighlightColor = GetProfileColor(entry, "profile_highlight_color"),
                DefaultAvatarFillColor = GetProfileColor(entry, "default_avatar_fill_color"),
                DefaultAvatarStrokeColor = GetProfileColor(entry, "default_avatar_stroke_color"),
                AvatarPicturePath = GetAvatarPicturePath(profileDir, entry)
            });
        }

        return profiles.AsReadOnly();
    }

    /// <summary>Parses a supported avatar resource index from the profile's <c>avatar_icon</c> URL.</summary>
    /// <param name="entry">The profile entry from <c>Local State</c>.</param>
    /// <returns>An index from 0 through 89, or index 26 when the URL is missing, malformed, or unsupported.</returns>
    private static int GetAvatarIconIndex(JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty("avatar_icon", out var iconElement) &&
            iconElement.ValueKind == JsonValueKind.String &&
            iconElement.GetString() is { } iconUrl &&
            iconUrl.StartsWith(AvatarIconUrlPrefix, StringComparison.Ordinal) &&
            int.TryParse(iconUrl.AsSpan(AvatarIconUrlPrefix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out var index) &&
            index >= 0 && index < DefaultAvatarIconCount)
        {
            return index;
        }

        return PlaceholderAvatarIconIndex;
    }

    /// <summary>Reads a stored signed JSON color integer and preserves its 32-bit ARGB representation.</summary>
    /// <param name="entry">The profile entry from <c>Local State</c>.</param>
    /// <param name="propertyName">The color property to read.</param>
    /// <returns>The color as an unsigned ARGB value, or <see langword="null"/> when the property is missing or invalid.</returns>
    private static uint? GetProfileColor(JsonElement entry, string propertyName)
    {
        if (entry.ValueKind == JsonValueKind.Object &&
            entry.TryGetProperty(propertyName, out var colorElement) &&
            colorElement.ValueKind == JsonValueKind.Number &&
            colorElement.TryGetInt32(out var signedColor))
        {
            return unchecked((uint)signedColor);
        }

        return null;
    }

    /// <summary>Finds an existing profile-local account picture when the entry selects one.</summary>
    /// <param name="profileDirectory">The profile directory that must contain the picture file.</param>
    /// <param name="entry">The profile entry from <c>Local State</c>.</param>
    /// <returns>The full path to a direct child picture file, or <see langword="null"/> when it is not selected or valid.</returns>
    private static string? GetAvatarPicturePath(string profileDirectory, JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object ||
            (!GetBoolean(entry, "use_gaia_picture") && !GetBoolean(entry, "is_using_default_avatar")) ||
            !entry.TryGetProperty("gaia_picture_file_name", out var fileNameElement) ||
            fileNameElement.ValueKind != JsonValueKind.String ||
            fileNameElement.GetString() is not { Length: > 0 } fileName ||
            Path.IsPathRooted(fileName) ||
            fileName.IndexOfAny(new[] { '/', '\\' }) >= 0)
        {
            return null;
        }

        try
        {
            var fullProfileDirectory = Path.GetFullPath(profileDirectory);
            var picturePath = Path.GetFullPath(Path.Combine(fullProfileDirectory, fileName));
            if (!string.Equals(Path.GetDirectoryName(picturePath), fullProfileDirectory, StringComparison.Ordinal))
            {
                return null;
            }

            return File.Exists(picturePath) ? picturePath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
                                              UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Returns whether a profile property is the JSON literal <see langword="true"/>.</summary>
    /// <param name="entry">The profile entry from <c>Local State</c>.</param>
    /// <param name="propertyName">The Boolean property to read.</param>
    /// <returns><see langword="true"/> only when the property exists and has JSON Boolean value true.</returns>
    private static bool GetBoolean(JsonElement entry, string propertyName)
    {
        return entry.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
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