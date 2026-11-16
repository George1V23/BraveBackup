using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Application-owned stored preference values and explicit absent keys; not a native browser file.</summary>
public sealed record PortableSettings(int SchemaVersion, Dictionary<string, JsonElement> Values, string[] AbsentKeys)
{
    /// <summary>Checks generic settings schema shape. Browser-specific allowlists belong in Backup.</summary>
    public static PortableSettings Parse(JsonElement root)
    {
        var model = root.Deserialize<PortableSettings>(ArchiveManifest.JsonOptions);
        if (model is null || model.SchemaVersion != 1 || model.Values is null || model.AbsentKeys is null ||
            model.Values.Count + model.AbsentKeys.Length > 1024)
            throw new InvalidDataException("Invalid portable settings schema.");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in model.Values)
        {
            CheckKey(pair.Key, keys);
            var value = pair.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.True:
                case JsonValueKind.False: break;
                case JsonValueKind.Number when value.TryGetInt32(out _): break;
                case JsonValueKind.String when value.GetString()!.Length <= 32768: break;
                case JsonValueKind.Array when value.GetArrayLength() <= 1024 &&
                                              value.EnumerateArray().All(item =>
                                                  item.ValueKind == JsonValueKind.String &&
                                                  item.GetString()!.Length <= 32768): break;
                default: throw new InvalidDataException("Unsupported portable settings value.");
            }
        }

        foreach (var key in model.AbsentKeys) CheckKey(key, keys);
        return model;
    }

    private static void CheckKey(string? key, HashSet<string> keys)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 256 || !keys.Add(key))
            throw new InvalidDataException("Duplicate or invalid settings key.");
    }
}