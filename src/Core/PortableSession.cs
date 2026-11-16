using System.Text.Json;

namespace BraveBackup.Core;

/// <summary>Application-owned projection of one validated last-persisted session, never a real-time browser claim.</summary>
public sealed record PortableSession(
    int SchemaVersion,
    string CaptureKind,
    DateTimeOffset CapturedUtc,
    SessionSource Source,
    bool IncludeNativeFiles,
    int? ActiveWindowId,
    SessionWindowData[] Windows);

/// <summary>Relative source identity and exact snapshot digest; no absolute user paths.</summary>
public sealed record SessionSource(string Path, int FormatVersion, long Length, string Sha256);

/// <summary>A persisted window and its visually ordered tabs/open groups.</summary>
public sealed record SessionWindowData(
    int Id,
    int Type,
    int? SelectedTabIndex,
    SessionTabData[] Tabs,
    SessionGroupData[] Groups);

/// <summary>Current persisted navigation only; navigation history/page state are deliberately absent.</summary>
public sealed record SessionTabData(int Id, int VisualIndex, string Url, string Title, bool Pinned, string? GroupId);

/// <summary>Group membership identity with nullable metadata where the source did not store it.</summary>
public sealed record SessionGroupData(string Id, string? Title, uint? Color, bool? Collapsed);

/// <summary>Exact additional raw session filenames and payload linkage; absent legacy names are explicit.</summary>
public sealed record NativeSessionInventory(int SchemaVersion, string[] AbsentLegacyFiles, NativeSessionFile[] Files);

/// <summary>A raw native session file and its archive identity/digest.</summary>
public sealed record NativeSessionFile(string RelativePath, string ArchivePath, long Length, string Sha256);

/// <summary>Browser-independent session payload invariants and bounded serialization.</summary>
public static class PortableSessionFormat
{
    public const int MaxPortableBytes = 64 * 1024 * 1024;
    public const int MaxTabs = 100_000;
    public const int MaxCommands = 1_000_000;

    /// <summary>Validates identities, counts, ordering, group references and safe source provenance.</summary>
    public static PortableSession Parse(JsonElement root)
    {
        var model = root.Deserialize<PortableSession>(ArchiveManifest.JsonOptions)
                    ?? throw new InvalidDataException("Missing portable session.");
        if (model.SchemaVersion != 1 || model.CaptureKind != "last-persisted" || model.CapturedUtc == default ||
            model.CapturedUtc.Offset != TimeSpan.Zero || model.Source is null || model.Source.FormatVersion <= 0 ||
            !SafeNativeName(model.Source.Path) || model.Source.Length is < 8 or > ArchiveLimits.MaxBookmarkBytes ||
            !ValidHash(model.Source.Sha256) || model.Windows is null || model.Windows.Length > MaxTabs)
            throw new InvalidDataException("Invalid portable session provenance.");
        var windows = new HashSet<int>();
        var tabs = new HashSet<int>();
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in model.Windows)
        {
            if (window is null || window.Id <= 0 || !windows.Add(window.Id) || window.Type is < 0 or > 4 ||
                window.Tabs is null || window.Tabs.Length is < 1 or > MaxTabs || window.Groups is null ||
                window.Groups.Length > window.Tabs.Length ||
                window.SelectedTabIndex is { } index && (index < 0 || index >= window.Tabs.Length))
                throw new InvalidDataException("Invalid portable session window.");
            var localGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in window.Groups)
                if (group is null || !ValidToken(group.Id) || !groups.Add(group.Id) || !localGroups.Add(group.Id) ||
                    group.Title is { Length: > 32768 } || group.Color is > 8 ||
                    (group.Title is null) != (group.Color is null) ||
                    group.Title is null && group.Collapsed is not null)
                    throw new InvalidDataException("Invalid portable session group.");
            var previousIndex = -1;
            var previousId = -1;
            var referencedGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tab in window.Tabs)
            {
                if (tab is null || tab.Id <= 0 || !tabs.Add(tab.Id) || tabs.Count > MaxTabs || tab.VisualIndex < 0 ||
                    tab.VisualIndex < previousIndex || tab.VisualIndex == previousIndex && tab.Id <= previousId ||
                    tab.Url is null || tab.Url.Length > 65536 || tab.Title is null || tab.Title.Length > 32768 ||
                    tab.GroupId is not null && !localGroups.Contains(tab.GroupId))
                    throw new InvalidDataException("Invalid portable session tab.");
                previousIndex = tab.VisualIndex;
                previousId = tab.Id;
                if (tab.GroupId is not null) referencedGroups.Add(tab.GroupId);
            }

            if (!referencedGroups.SetEquals(localGroups))
                throw new InvalidDataException("Unreferenced portable session group.");
        }

        if (model.ActiveWindowId is { } active && !windows.Contains(active))
            throw new InvalidDataException("Invalid active session window.");
        return model;
    }

    /// <summary>Checks source-neutral relative filename shape; browser-specific family policy belongs in Backup.</summary>
    public static bool SafeNativeName(string? name)
    {
        return ExtensionSnapshotFormat.SafePath(name) && name!.Length <= 128 && name.Split('/').Length <= 2;
    }

    public static bool ValidHash(string? value) => value is { Length: 64 } &&
                                                   value.All(character =>
                                                       character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidToken(string? value) =>
        value is { Length: 32 } && value.Any(character => character != '0') &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>Serializes JSON without allowing escaping/metadata to grow beyond a caller-specified byte ceiling.</summary>
    public static byte[] SerializeBounded<T>(T value, int limit)
    {
        using var output = new LimitedBuffer(limit);
        JsonSerializer.Serialize(output, value, ArchiveManifest.JsonOptions);
        return output.ToArray();
    }

    private sealed class LimitedBuffer(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > limit) throw new InvalidDataException("Session JSON exceeds its byte limit.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            var bytes = buffer.ToArray();
            Write(bytes, 0, bytes.Length);
        }

        public override void WriteByte(byte value)
        {
            if (Position >= limit) throw new InvalidDataException("Session JSON exceeds its byte limit.");
            base.WriteByte(value);
        }
    }
}