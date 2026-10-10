using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>
/// Provides functionality for parsing Chromium / Brave Bookmarks JSON files into strongly-typed structures.
/// </summary>
public static class BraveBookmarksParser
{
    /// <summary>
    /// Parses a raw Bookmarks JSON string into a <see cref="BraveBookmarksFile"/> instance.
    /// </summary>
    /// <param name="json">The JSON content from a Brave Bookmarks file.</param>
    /// <returns>A strongly-typed <see cref="BraveBookmarksFile"/> containing roots and bookmark trees.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="json"/> is null.</exception>
    /// <exception cref="JsonException">Thrown when the JSON format or required properties are invalid.</exception>
    public static BraveBookmarksFile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        using var document = JsonDocument.Parse(json);
        var documentElement = document.RootElement;
        RequireKind(documentElement, JsonValueKind.Object, "The bookmarks document must be an object.");

        var versionElement = GetRequiredProperty(documentElement, "version");
        if (versionElement.ValueKind != JsonValueKind.Number || !versionElement.TryGetInt32(out var version))
        {
            throw new JsonException("The bookmarks document version must be a 32-bit integer.");
        }

        var rootsElement = GetRequiredProperty(documentElement, "roots");
        RequireKind(rootsElement, JsonValueKind.Object, "The bookmarks roots must be an object.");

        var roots = new List<BraveBookmarkRoot>();
        foreach (var rootProperty in rootsElement.EnumerateObject())
        {
            var rootElement = rootProperty.Value;
            RequireKind(rootElement, JsonValueKind.Object, $"Bookmark root '{rootProperty.Name}' must be an object.");

            roots.Add(new BraveBookmarkRoot(
                rootProperty.Name,
                GetRequiredString(rootElement, "name"),
                GetOptionalString(rootElement, "id"),
                GetOptionalString(rootElement, "guid"),
                ParseChildren(GetRequiredProperty(rootElement, "children"), $"Bookmark root '{rootProperty.Name}'")));
        }

        return new BraveBookmarksFile(version, roots.AsReadOnly());
    }

    private static IReadOnlyList<BraveBookmark> ParseChildren(JsonElement element, string parentDescription)
    {
        RequireKind(element, JsonValueKind.Array, $"{parentDescription} children must be an array.");

        var children = new List<BraveBookmark>();
        foreach (var childElement in element.EnumerateArray())
        {
            RequireKind(childElement, JsonValueKind.Object, $"{parentDescription} entries must be objects.");

            var type = GetRequiredString(childElement, "type") switch
            {
                "folder" => BraveBookmarkType.Folder,
                "url" => BraveBookmarkType.Url,
                var value => throw new JsonException($"Unsupported bookmark type '{value}'.")
            };

            var name = GetRequiredString(childElement, "name");
            var childDescription = $"Bookmark '{name}'";
            var nestedChildren = type == BraveBookmarkType.Folder
                ? ParseChildren(GetRequiredProperty(childElement, "children"), childDescription)
                : Array.Empty<BraveBookmark>();

            children.Add(new BraveBookmark(
                type,
                name,
                GetOptionalString(childElement, "id"),
                GetOptionalString(childElement, "guid"),
                GetOptionalString(childElement, "date_added"),
                GetOptionalString(childElement, "date_modified"),
                type == BraveBookmarkType.Url ? GetRequiredString(childElement, "url") : null,
                nestedChildren));
        }

        return children.AsReadOnly();
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new JsonException($"Required property '{propertyName}' is missing.");
        }

        return property;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var property = GetRequiredProperty(element, propertyName);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Property '{propertyName}' must be a string.");
        }

        return property.GetString()!;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new JsonException($"Property '{propertyName}' must be a string when present.");
        }

        return property.GetString();
    }

    private static void RequireKind(JsonElement element, JsonValueKind expected, string message)
    {
        if (element.ValueKind != expected)
        {
            throw new JsonException(message);
        }
    }
}

/// <summary>
/// Represents a parsed Chromium / Brave Bookmarks file.
/// </summary>
/// <param name="Version">The file format version.</param>
/// <param name="Roots">The collection of bookmark root nodes (e.g. bookmark_bar, other, synced).</param>
public sealed record BraveBookmarksFile(int Version, IReadOnlyList<BraveBookmarkRoot> Roots);

/// <summary>
/// Represents a root folder node in the Brave bookmarks hierarchy.
/// </summary>
/// <param name="Key">The JSON property key identifying the root (e.g., 'bookmark_bar').</param>
/// <param name="Name">The display name of the root folder.</param>
/// <param name="Id">The unique ID assigned by Chromium/Brave, if present.</param>
/// <param name="Guid">The GUID assigned by Chromium/Brave, if present.</param>
/// <param name="Children">The direct child bookmarks or folders under this root.</param>
public sealed record BraveBookmarkRoot(
    string Key,
    string Name,
    string? Id,
    string? Guid,
    IReadOnlyList<BraveBookmark> Children);

/// <summary>
/// Specifies the type of bookmark node in the Brave bookmarks structure.
/// </summary>
public enum BraveBookmarkType
{
    /// <summary>
    /// A folder bookmark that can contain child nodes.
    /// </summary>
    Folder,

    /// <summary>
    /// A URL bookmark pointing to a web address.
    /// </summary>
    Url
}

/// <summary>
/// Represents a single bookmark item or folder in the Brave bookmarks hierarchy.
/// </summary>
/// <param name="Type">The bookmark node type (Folder or Url).</param>
/// <param name="Name">The display title or folder name.</param>
/// <param name="Id">The internal numeric ID string assigned by Chromium/Brave.</param>
/// <param name="Guid">The GUID assigned by Chromium/Brave.</param>
/// <param name="DateAdded">The timestamp when the bookmark was created.</param>
/// <param name="DateModified">The timestamp when the bookmark was last modified.</param>
/// <param name="Url">The URL if this item is a bookmark; otherwise <c>null</c>.</param>
/// <param name="Children">The child bookmarks if this item is a folder; otherwise empty.</param>
public sealed record BraveBookmark(
    BraveBookmarkType Type,
    string Name,
    string? Id,
    string? Guid,
    string? DateAdded,
    string? DateModified,
    string? Url,
    IReadOnlyList<BraveBookmark> Children);
