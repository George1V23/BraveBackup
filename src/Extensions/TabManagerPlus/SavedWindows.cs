using System.Globalization;
using System.Text.Json;

namespace BraveBackup.Extensions.TabManagerPlus;

/// <summary>Supported saved-window metadata only; transient extension state and native tab groups are excluded.</summary>
public sealed record SavedWindow(
    string Id,
    string Name,
    string Color,
    long Date,
    long SessionStartTime,
    long? Updated,
    double? Order,
    bool CustomName,
    bool Incognito,
    SavedWindowGeometry Geometry,
    SavedTab[] Tabs);

/// <summary>Stored window properties; null preserves an absent property instead of supplying defaults.</summary>
public sealed record SavedWindowGeometry(
    int? Id,
    string? Type,
    string? State,
    int? Left,
    int? Top,
    int? Width,
    int? Height,
    bool? Incognito,
    bool? Focused,
    bool? AlwaysOnTop);

/// <summary>One stored tab in array order. URLs are data, never navigated or executed by this reader.</summary>
public sealed record SavedTab(int Index, string Url, string? Title, bool? Pinned, bool? Active, bool? Incognito);

/// <summary>A validated version-specific collection, ordered as the extension lists saved windows.</summary>
public sealed record SavedWindowCollection(string ExtensionVersion, SavedWindow[] Windows)
{
    public int TabCount => Windows.Sum(window => window.Tabs.Length);
}

/// <summary>Strict bounded parser for the researched 7.0.0 consolidated storage.local sessions map.</summary>
public static class SavedWindows
{
    public const string SupportedVersion = "7.0.0";
    public const string OfficialExtensionId = "cnkdjjdmfiffagllbiiilooaoofcoeff";
    public const int MaximumWindows = 10_000;
    public const int MaximumTabs = 100_000;

    /// <summary>
    /// Parses stored JSON without repairing records, inferring missing URLs or dropping invalid windows.
    /// Unsupported versions and malformed input throw a fixed, content-free InvalidDataException.
    /// </summary>
    public static SavedWindowCollection Parse(ReadOnlyMemory<byte> sessions, string extensionVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (extensionVersion != SupportedVersion || sessions.Length > OfflineLevelDb.MaximumRecordBytes)
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(sessions, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            long text = 0;
            var nodes = 0;
            CheckTree(root, ref text, ref nodes, cancellationToken);
            var windows = new List<(string Key, SavedWindow Window)>();
            var tabsTotal = 0;
            foreach (var entry in root.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (windows.Count == MaximumWindows) throw Invalid();
                var source = entry.Value;
                if (source.ValueKind != JsonValueKind.Object) throw Invalid();
                var id = Text(source, "id", 512, true)!;
                if (id.Length == 0 || id != entry.Name) throw Invalid();
                var name = Text(source, "name", 32768, true)!;
                var color = Text(source, "color", 128, true)!;
                var date = Milliseconds(source, "date", true)!.Value;
                var start = Milliseconds(source, "sessionStartTime", true)!.Value;
                var updated = Milliseconds(source, "updated", false);
                var order = OptionalNumber(source, "order");
                var customName = Boolean(source, "customName", true)!.Value;
                var incognito = Boolean(source, "incognito", true)!.Value;
                if (!source.TryGetProperty("windowsInfo", out var info) || info.ValueKind != JsonValueKind.Object ||
                    !source.TryGetProperty("tabs", out var tabs) ||
                    tabs.ValueKind != JsonValueKind.Array) throw Invalid();
                var geometry = new SavedWindowGeometry(Integer(info, "id"), Text(info, "type", 64),
                    Text(info, "state", 64), Integer(info, "left"), Integer(info, "top"),
                    Integer(info, "width", true), Integer(info, "height", true), Boolean(info, "incognito"),
                    Boolean(info, "focused"), Boolean(info, "alwaysOnTop"));
                var list = new List<SavedTab>();
                var indexes = new HashSet<int>();
                foreach (var tab in tabs.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++tabsTotal > MaximumTabs || tab.ValueKind != JsonValueKind.Object) throw Invalid();
                    var index = Integer(tab, "index");
                    var url = Text(tab, "url", 1024 * 1024, true)!;
                    if (index is null or < 0 || !indexes.Add(index.Value) || url.Length == 0) throw Invalid();
                    list.Add(new SavedTab(index.Value, url, Text(tab, "title", 32768), Boolean(tab, "pinned"),
                        Boolean(tab, "active"), Boolean(tab, "incognito")));
                }

                windows.Add((entry.Name, new SavedWindow(id, name, color, date, start, updated, order,
                    customName, incognito, geometry, list.ToArray())));
            }

            // Object.values uses ECMAScript property order: canonical uint32 keys first,
            // except 2^32-1, followed by other keys in insertion order. Subsequent sorting is stable.
            var propertyOrder = windows.OrderBy(window => ArrayIndex(window.Key).HasValue ? 0 : 1)
                .ThenBy(window => ArrayIndex(window.Key) ?? 0);
            var ordered = propertyOrder.OrderBy(window => window.Window.Order.HasValue ? 0 : 1)
                .ThenBy(window => window.Window.Order ?? 0)
                .ThenByDescending(window => window.Window.Order.HasValue ? 0 : window.Window.Date)
                .Select(window => window.Window).ToArray();
            return new SavedWindowCollection(extensionVersion, ordered);
        }
        // JsonDocument can defer invalid UTF-8/unpaired-surrogate errors until GetString,
        // where they surface as InvalidOperationException rather than JsonException.
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw Invalid();
        }
    }

    private static uint? ArrayIndex(string name) => uint.TryParse(name, NumberStyles.None,
                                                        CultureInfo.InvariantCulture, out var value) &&
                                                    value != uint.MaxValue &&
                                                    value.ToString(CultureInfo.InvariantCulture) == name
        ? value
        : null;

    private static void CheckTree(JsonElement value, ref long text, ref int nodes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (++nodes > 1_000_000) throw Invalid();
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    text += property.Name.Length;
                    if (text > 16 * 1024 * 1024 || !names.Add(property.Name)) throw Invalid();
                    CheckTree(property.Value, ref text, ref nodes, token);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CheckTree(item, ref text, ref nodes, token);
                break;
            case JsonValueKind.String:
                text += value.GetString()!.Length;
                if (text > 16 * 1024 * 1024) throw Invalid();
                break;
        }
    }

    private static string? Text(JsonElement source, string name, int maximum, bool required = false)
    {
        if (!source.TryGetProperty(name, out var value)) return required ? throw Invalid() : null;
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var result = value.GetString()!;
        return result.Length <= maximum ? result : throw Invalid();
    }

    private static bool? Boolean(JsonElement source, string name, bool required = false)
    {
        if (!source.TryGetProperty(name, out var value)) return required ? throw Invalid() : null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid()
        };
    }

    private static int? Integer(JsonElement source, string name, bool positive = false)
    {
        if (!source.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) ||
            (positive && number <= 0)) throw Invalid();
        return number;
    }

    private static long? Milliseconds(JsonElement source, string name, bool required)
    {
        if (!source.TryGetProperty(name, out var value)) return required ? throw Invalid() : null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number) ||
            number is < 0 or > 9_007_199_254_740_991L) throw Invalid();
        return number;
    }

    private static double? OptionalNumber(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Invalid();
        return number;
    }

    private static InvalidDataException Invalid() => new("Unsupported or invalid Tab Manager Plus saved-window data.");
}