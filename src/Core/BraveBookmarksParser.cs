using System.Text.Json;

namespace BraveBackup.Core;

public static class BraveBookmarksParser
{
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

public sealed record BraveBookmarksFile(int Version, IReadOnlyList<BraveBookmarkRoot> Roots);

public sealed record BraveBookmarkRoot(
    string Key,
    string Name,
    string? Id,
    string? Guid,
    IReadOnlyList<BraveBookmark> Children);

public enum BraveBookmarkType
{
    Folder,
    Url
}

public sealed record BraveBookmark(
    BraveBookmarkType Type,
    string Name,
    string? Id,
    string? Guid,
    string? DateAdded,
    string? DateModified,
    string? Url,
    IReadOnlyList<BraveBookmark> Children);
