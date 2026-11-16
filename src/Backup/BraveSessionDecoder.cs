using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using BraveBackup.Core;

namespace BraveBackup.Core;

/// <summary>Bounded replay of the pinned 64-bit little-endian Chromium SNSS-v3 command projection.</summary>
public static class BraveSessionDecoder
{
    /// <summary>Decodes one complete unencrypted persisted source; unknown/incomplete states never become partial success.</summary>
    public static PortableSession Decode(ReadOnlyMemory<byte> bytes, string sourcePath, DateTimeOffset capturedUtc,
        bool includeNativeFiles = false, CancellationToken token = default)
    {
        if (bytes.Length is < 8 or > ArchiveLimits.MaxBookmarkBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span) != 0x53534E53)
            throw Error("session-header", "The selected session file has an invalid header.");
        var version = BinaryPrimitives.ReadInt32LittleEndian(bytes.Span[4..]);
        if (version != 3)
            throw Error("session-format-unsupported",
                "Only tested unencrypted session format 3 is supported. Encrypted and legacy formats cannot be substituted with a partial backup.");
        var replay = new Replay(token);
        var position = 8;
        var marker = false;
        var commands = 0;
        while (position < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (++commands > PortableSessionFormat.MaxCommands) throw Limit();
            if (bytes.Length - position < 2) throw Malformed();
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Span[position..]);
            position += 2;
            if (length < 1 || length > bytes.Length - position) throw Malformed();
            var id = bytes.Span[position];
            var payload = bytes.Slice(position + 1, length - 1);
            position += length;
            if (id == 255)
            {
                if (marker || payload.Length != 0) throw Malformed();
                marker = true;
                continue;
            }

            replay.Apply(id, payload);
        }

        if (!marker)
            throw Error("session-marker-missing",
                "The selected session has no completed initial-state marker. Retry after Brave persists its state.");
        var result =
            replay.Build(
                new SessionSource(sourcePath, 3, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes.Span))),
                capturedUtc, includeNativeFiles);
        return result;
    }

    private sealed class Tab
    {
        public int Window;
        public int Index = -1;
        public int SelectedNavigation = -1;
        public bool Pinned;
        public string? Group;
        public bool Split;
        public SortedDictionary<int, Navigation> Navigations { get; } = new();
    }

    private sealed class Window
    {
        public bool Typed;
        public int Type;
        public int? Selected;
        public bool Closed;
    }

    private sealed record Navigation(string Url, string Title);

    private sealed class Replay(CancellationToken token)
    {
        private readonly Dictionary<int, Tab> _tabs = new();
        private readonly Dictionary<int, Window> _windows = new();
        private readonly Dictionary<string, SessionGroupData> _groups = new(StringComparer.Ordinal);
        private int? _active;
        private int _work;

        private void Work(int amount = 1)
        {
            token.ThrowIfCancellationRequested();
            _work = checked(_work + amount);
            if (_work > 5_000_000) throw Limit();
        }

        private Tab GetTab(int id)
        {
            if (id <= 0) throw Malformed();
            if (!_tabs.TryGetValue(id, out var tab))
            {
                if (_tabs.Count >= PortableSessionFormat.MaxTabs) throw Limit();
                _tabs.Add(id, tab = new Tab());
            }

            return tab;
        }

        private Window GetWindow(int id)
        {
            if (id <= 0) throw Malformed();
            if (!_windows.TryGetValue(id, out var window))
            {
                if (_windows.Count >= PortableSessionFormat.MaxTabs) throw Limit();
                _windows.Add(id, window = new Window());
            }

            return window;
        }

        public void Apply(byte id, ReadOnlyMemory<byte> memory)
        {
            Work();
            var data = memory.Span;
            int I(int offset) => BinaryPrimitives.ReadInt32LittleEndian(memory.Span[offset..]);

            void Size(int size)
            {
                if (memory.Length != size) throw Malformed();
            }

            switch (id)
            {
                case 0:
                    Size(8);
                    GetWindow(I(0));
                    GetTab(I(4)).Window = I(0);
                    break;
                case 2:
                    Size(8);
                    if (I(4) < 0) throw Malformed();
                    GetTab(I(0)).Index = I(4);
                    break;
                case 5:
                    Size(8);
                    if (I(4) < 0) throw Malformed();
                    var back = GetTab(I(0));
                    Work(back.Navigations.Count);
                    foreach (var index in back.Navigations.Keys.Where(index => index >= I(4)).ToArray())
                        back.Navigations.Remove(index);
                    break;
                case 11:
                    Size(8);
                    Prune(GetTab(I(0)), 0, I(4));
                    break;
                case 24:
                    Size(12);
                    Prune(GetTab(I(0)), I(4), I(8));
                    break;
                case 6:
                    var navigation = new Pickle(memory);
                    var tab = GetTab(navigation.Int());
                    var navIndex = navigation.Int();
                    if (navIndex < 0) throw Malformed();
                    var url = navigation.String();
                    var title = navigation.String16();
                    navigation.SkipString();
                    navigation.Int();
                    // The upstream reader treats this historical tail as optional; present fields remain bounded/validated.
                    if (!navigation.Done)
                    {
                        navigation.Int();
                        if (!navigation.Done) navigation.SkipString();
                        if (!navigation.Done) navigation.Int();
                        if (!navigation.Done) navigation.SkipString();
                        if (!navigation.Done) navigation.Bool();
                        if (!navigation.Done) navigation.UInt64();
                        if (!navigation.Done) navigation.SkipString16();
                        if (!navigation.Done) navigation.Int();
                        if (!navigation.Done) navigation.Int();
                        if (!navigation.Done)
                        {
                            var count = navigation.Count();
                            for (var i = 0; i < count; i++)
                            {
                                Work();
                                navigation.SkipString();
                                navigation.SkipString();
                            }
                        }

                        for (var i = 0; i < 3 && !navigation.Done; i++) navigation.UInt64();
                        if (!navigation.Done)
                        {
                            var count = navigation.Count();
                            for (var i = 0; i < count; i++)
                            {
                                Work();
                                navigation.UInt64();
                            }
                        }
                    }

                    navigation.End();
                    tab.Navigations[navIndex] = new Navigation(url, title);
                    break;
                case 7:
                    Size(8);
                    if (I(4) < -1) throw Malformed();
                    GetTab(I(0)).SelectedNavigation = I(4);
                    break;
                case 8:
                    Size(8);
                    if (I(4) < -1) throw Malformed();
                    GetWindow(I(0)).Selected = I(4);
                    break;
                case 9:
                    Size(8);
                    if (I(4) is < 0 or > 4) throw Malformed();
                    var window = GetWindow(I(0));
                    window.Typed = true;
                    window.Type = I(4);
                    window.Closed = false;
                    break;
                case 10:
                case 14:
                    Size(24);
                    GetWindow(I(0));
                    break; // Native bounds/show-state outside portable projection.
                case 12:
                    Size(8);
                    GetTab(I(0)).Pinned = Flag(data[4]);
                    break;
                case 16:
                    Size(16);
                    if (I(0) <= 0) throw Malformed();
                    _tabs.Remove(I(0));
                    break;
                case 17:
                    Size(16);
                    if (I(0) <= 0) throw Malformed();
                    _windows[I(0)] = new Window { Closed = true };
                    break;
                case 20:
                    Size(4);
                    if (I(0) <= 0) throw Malformed();
                    _active = I(0);
                    break;
                case 21:
                    Size(16);
                    GetTab(I(0));
                    break;
                case 32:
                    Size(8);
                    GetWindow(I(0));
                    Flag(data[4]);
                    break;
                case 25:
                case 36:
                    Size(32);
                    var grouped = GetTab(I(0));
                    var has = Flag(data[24]);
                    var group = has
                        ? Token(BinaryPrimitives.ReadUInt64LittleEndian(data[8..]),
                            BinaryPrimitives.ReadUInt64LittleEndian(data[16..]))
                        : null;
                    if (id == 25) grouped.Group = group;
                    else grouped.Split = has;
                    break;
                case 27:
                    var metadata = new Pickle(memory);
                    var groupId = Token(metadata.UInt64(), metadata.UInt64());
                    var groupTitle = metadata.String16();
                    var color = metadata.UInt32();
                    if (color > 8) throw Malformed();
                    bool? collapsed = metadata.Done ? null : metadata.Bool();
                    if (!metadata.Done && metadata.Bool())
                        metadata.String(); // Saved-group identifier is outside open-group projection.
                    metadata.End();
                    if (_groups.Count >= PortableSessionFormat.MaxTabs && !_groups.ContainsKey(groupId)) throw Limit();
                    _groups[groupId] = new SessionGroupData(groupId, groupTitle, color, collapsed);
                    break;
                case 13:
                case 15:
                case 18:
                case 19:
                case 23:
                case 28:
                case 31:
                    var text = new Pickle(memory);
                    var identity = text.Int();
                    text.String();
                    text.End();
                    if (id is 15 or 23 or 31) GetWindow(identity);
                    else GetTab(identity);
                    break;
                case 29:
                    var agent = new Pickle(memory);
                    GetTab(agent.Int());
                    agent.SkipString();
                    if (agent.Bool()) agent.SkipString();
                    agent.End();
                    break;
                case 30:
                    var map = new Pickle(memory);
                    GetTab(map.Int());
                    var entries = map.Count();
                    for (var i = 0; i < entries; i++)
                    {
                        Work();
                        map.SkipString();
                        map.SkipString();
                    }

                    map.End();
                    break;
                case 33:
                case 34:
                    var extra = new Pickle(memory);
                    var owner = extra.Int();
                    extra.SkipString();
                    extra.SkipString();
                    extra.End();
                    if (id == 33) GetTab(owner);
                    else GetWindow(owner);
                    break;
                case 35:
                    var platform = new Pickle(memory);
                    platform.SkipString();
                    platform.End();
                    break;
                case 37:
                    var split = new Pickle(memory);
                    Token(split.UInt64(), split.UInt64());
                    split.UInt64();
                    split.String();
                    split.End();
                    break;
                default:
                    throw Error("session-command-unsupported",
                        "The selected session contains a command this decoder cannot safely replay. No partial session was saved.");
            }
        }

        private void Prune(Tab tab, int start, int count)
        {
            if (start < 0 || count <= 0 || (long)start + count > int.MaxValue) throw Malformed();
            Work(tab.Navigations.Count);
            var end = start + count;
            if (tab.SelectedNavigation >= start && tab.SelectedNavigation < end) tab.SelectedNavigation = start - 1;
            else if (tab.SelectedNavigation >= end) tab.SelectedNavigation -= count;
            var kept = tab.Navigations.Where(pair => pair.Key < start || pair.Key >= end).Select(pair =>
                new KeyValuePair<int, Navigation>(pair.Key >= end ? pair.Key - count : pair.Key, pair.Value)).ToArray();
            tab.Navigations.Clear();
            foreach (var pair in kept) tab.Navigations.Add(pair.Key, pair.Value);
        }

        public PortableSession Build(SessionSource source, DateTimeOffset captured, bool native)
        {
            var result = new List<SessionWindowData>();
            var usedGroups = new HashSet<string>(StringComparer.Ordinal);
            Work(_tabs.Count);
            var byWindow = _tabs.ToLookup(tab => tab.Value.Window);
            foreach (var pair in _windows.OrderBy(pair => pair.Key))
            {
                Work();
                if (pair.Value.Closed) continue;
                var tabs = byWindow[pair.Key].OrderBy(tab => tab.Value.Index).ThenBy(tab => tab.Key).ToArray();
                if (tabs.Length == 0) continue;
                if (!pair.Value.Typed) throw Incomplete();
                var output = new List<SessionTabData>();
                foreach (var entry in tabs)
                {
                    Work();
                    var tab = entry.Value;
                    if (tab.Index < 0 || tab.Navigations.Count == 0 || tab.Split) throw Incomplete();
                    var current = tab.Navigations.FirstOrDefault(nav => nav.Key >= tab.SelectedNavigation);
                    var navigation = current.Value ?? tab.Navigations.Last().Value;
                    output.Add(new SessionTabData(entry.Key, tab.Index, navigation.Url, navigation.Title, tab.Pinned,
                        tab.Group));
                }

                var groups = output.Where(tab => tab.GroupId is not null).Select(tab => tab.GroupId!)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(group =>
                    {
                        if (!usedGroups.Add(group)) throw Incomplete();
                        return _groups.GetValueOrDefault(group) ?? new SessionGroupData(group, null, null, null);
                    }).ToArray();
                int? selected = pair.Value.Selected is null
                    ? null
                    : Math.Max(0, Array.FindIndex(tabs, tab => tab.Value.Index == pair.Value.Selected));
                result.Add(new SessionWindowData(pair.Key, pair.Value.Type, selected, output.ToArray(), groups));
            }

            if (_tabs.Any(pair => pair.Value.Window == 0 && pair.Value.Navigations.Count > 0)) throw Incomplete();
            return new PortableSession(1, "last-persisted", captured, source, native,
                result.Any(window => window.Id == _active) ? _active : null, result.ToArray());
        }
    }

    private sealed class Pickle
    {
        private readonly ReadOnlyMemory<byte> _bytes;
        private int _position = 4;

        public Pickle(ReadOnlyMemory<byte> bytes)
        {
            if (bytes.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span) != bytes.Length - 4)
                throw Malformed();
            _bytes = bytes;
        }

        public bool Done => _position == _bytes.Length;

        private ReadOnlySpan<byte> Read(int length)
        {
            if (length < 0 || length > _bytes.Length - _position) throw Malformed();
            var aligned = checked((length + 3) & ~3);
            if (aligned > _bytes.Length - _position) throw Malformed();
            var value = _bytes.Span.Slice(_position, length);
            _position += aligned;
            return value;
        }

        public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Read(4));
        public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Read(4));
        public ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Read(8));

        public bool Bool()
        {
            var bytes = Read(1);
            return Flag(bytes[0]);
        }

        public int Count()
        {
            var value = Int();
            if (value is < 0 or > 10000) throw Malformed();
            return value;
        }

        public string String()
        {
            var count = Int();
            if (count is < 0 or > 65536) throw Malformed();
            try
            {
                return new UTF8Encoding(false, true).GetString(Read(count));
            }
            catch (DecoderFallbackException)
            {
                throw Malformed();
            }
        }

        public string String16()
        {
            var count = Int();
            if (count is < 0 or > 32768) throw Malformed();
            try
            {
                return new UnicodeEncoding(false, false, true).GetString(Read(checked(count * 2)));
            }
            catch (DecoderFallbackException)
            {
                throw Malformed();
            }
        }

        public void SkipString()
        {
            var count = Int();
            if (count is < 0 or > 65536) throw Malformed();
            Read(count);
        }

        public void SkipString16()
        {
            var count = Int();
            if (count is < 0 or > 32768) throw Malformed();
            Read(checked(count * 2));
        }

        public void End()
        {
            if (!Done) throw Malformed();
        }
    }

    private static bool Flag(byte value) => value switch { 0 => false, 1 => true, _ => throw Malformed() };

    private static string Token(ulong high, ulong low) =>
        high == 0 && low == 0 ? throw Malformed() : $"{high:x16}{low:x16}";

    private static BackupOperationException Malformed() => Error("session-malformed",
        "The selected session is truncated, malformed or uses an unsupported native payload layout.");

    private static BackupOperationException Incomplete() => Error("session-incomplete",
        "The persisted session lacks required tab/window data or contains unsupported split-tab state. No incomplete projection was saved.");

    private static BackupOperationException Limit() =>
        Error("session-limit", "The persisted session exceeds decoder resource limits.");

    private static BackupOperationException Error(string code, string message) => new(code, message);
}