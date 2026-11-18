using BraveBackup.Extensions.TabManagerPlus;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class TabManagerPlusSavedWindowRestoreTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TabManagerPlus", "storage");

    [Theory]
    [InlineData(0)]
    [InlineData(110_000)]
    public void NewLogReplacesOnlySessionsAndPreservesExistingFilesAndSettings(int urlPadding)
    {
        var files = Store();
        var before = files.ToDictionary(file => file.Key, file => file.Value.ToArray());
        var version = OfflineLevelDb.ReadValue(files, "version");
        var unrelated = OfflineLevelDb.ReadValue(files, "fixture-padding");
        Assert.NotNull(version);
        Assert.NotNull(unrelated);
        var original = ReadWindows(files);
        var window = Assert.Single(original.Windows);
        var tab = Assert.Single(window.Tabs) with { Url = "https://example.invalid/restored/" + new string('x', urlPadding) };
        var desired = original with { Windows = [window with { Name = "Restored window", Tabs = [tab] }] };

        using var update = SavedWindowRestore.Prepare(files, Archive(desired));
        Assert.False(files.ContainsKey(update.FileName));
        Assert.Equal(before.Count, files.Count);
        foreach (var file in files) Assert.Equal(before[file.Key], file.Value.ToArray());
        var bytes = update.Bytes.Span;
        if (urlPadding > 0)
        {
            Assert.Equal(2, bytes[6]);
            Assert.Equal(3, bytes[32768 + 6]);
            Assert.Equal(3, bytes[65536 + 6]);
            Assert.Equal(4, bytes[98304 + 6]);
        }
        else
        {
            Assert.Equal(1, bytes[6]);
        }

        var updated = WithUpdate(files, update);
        var restored = Assert.Single(ReadWindows(updated).Windows);
        Assert.Equal("Restored window", restored.Name);
        Assert.Equal(tab, Assert.Single(restored.Tabs));
        Assert.Equal(version, OfflineLevelDb.ReadValue(updated, "version"));
        Assert.Equal(unrelated, OfflineLevelDb.ReadValue(updated, "fixture-padding"));
    }

    [Fact]
    public void SecondUpdateUsesANewerLogAndCanRestoreAnExplicitEmptyCollection()
    {
        var original = Store();
        var saved = ReadWindows(original);
        using var empty = SavedWindowRestore.Prepare(original, Archive(saved with { Windows = [] }));
        var afterEmpty = WithUpdate(original, empty);
        Assert.Empty(ReadWindows(afterEmpty).Windows);

        using var restore = SavedWindowRestore.Prepare(afterEmpty, Archive(saved));
        Assert.True(string.CompareOrdinal(empty.FileName, restore.FileName) < 0);
        var afterRestore = WithUpdate(afterEmpty, restore);
        Assert.Equal(Assert.Single(saved.Windows).Id, Assert.Single(ReadWindows(afterRestore).Windows).Id);
        Assert.Equal(OfflineLevelDb.ReadValue(original, "fixture-padding"), OfflineLevelDb.ReadValue(afterRestore, "fixture-padding"));
    }

    [Theory]
    [InlineData("missing-map")]
    [InlineData("invalid-sessions")]
    [InlineData("unexpected-entry")]
    [InlineData("lock-contents")]
    [InlineData("wrong-version")]
    [InlineData("bad-checksum")]
    public void UnsupportedOrCorruptTargetDoesNotProduceAnUpdate(string condition)
    {
        var files = Store(condition == "missing-map" ? "deleted" : condition == "invalid-sessions" ? "fragmented" : "table-and-wal");
        var payload = Archive(ReadWindows(Store()));
        switch (condition)
        {
            case "unexpected-entry":
                files.Add("000012.dbtmp", ReadOnlyMemory<byte>.Empty);
                break;
            case "lock-contents":
                files.Add("LOCK", new byte[] { 1 });
                break;
            case "wrong-version":
                // Independently encoded FULL record with sequence 1000 and one version Put.
                files.Add("001000.log", Convert.FromBase64String("hWTGfB0AAegDAAAAAAAAAQAAAAEHdmVyc2lvbgciNi4wLjAi"));
                break;
            case "bad-checksum":
                var log = files["000004.log"].ToArray();
                log[^1] ^= 1;
                files["000004.log"] = log;
                break;
        }
        var snapshot = files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        Assert.Throws<InvalidDataException>(() => SavedWindowRestore.Prepare(files, payload));

        Assert.Equal(snapshot.Count, files.Count);
        foreach (var file in files) Assert.Equal(snapshot[file.Key], file.Value.ToArray());
    }

    [Fact]
    public void UpdateSequenceFollowsNewerRecordsForOtherKeys()
    {
        var files = Store();
        // Independently encoded FULL record with sequence 1000 and one version Put.
        files.Add("001000.log", Convert.FromBase64String("pZLzrh0AAegDAAAAAAAAAQAAAAEHdmVyc2lvbgciNy4wLjAi"));
        var saved = ReadWindows(files);

        using var update = SavedWindowRestore.Prepare(files, Archive(saved with { Windows = [] }));

        Assert.Equal("001001.log", update.FileName);
        Assert.Empty(ReadWindows(WithUpdate(files, update)).Windows);
    }

    [Fact]
    public void RejectsInvalidArchiveAndHonorsCancellation()
    {
        var files = Store();
        Assert.Throws<InvalidDataException>(() => SavedWindowRestore.Prepare(files, "{}"u8.ToArray()));
        Assert.Throws<OperationCanceledException>(() =>
            SavedWindowRestore.Prepare(files, Archive(ReadWindows(files)), new CancellationToken(true)));
    }

    [Fact]
    public void DisposalClearsOwnedLogBytes()
    {
        var files = Store();
        var update = SavedWindowRestore.Prepare(files, Archive(ReadWindows(files)));
        var bytes = update.Bytes;
        update.Dispose();
        Assert.False(bytes.Span.ContainsAnyExcept((byte)0));
        Assert.Throws<ObjectDisposedException>(() => update.Bytes);
    }

    private static byte[] Archive(SavedWindowCollection collection) =>
        SavedWindowPayload.Serialize(collection, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), 4);

    private static SavedWindowCollection ReadWindows(IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files) =>
        SavedWindows.Parse(OfflineLevelDb.ReadValue(files, "sessions")!, SavedWindows.SupportedVersion);

    private static Dictionary<string, ReadOnlyMemory<byte>> WithUpdate(
        Dictionary<string, ReadOnlyMemory<byte>> files, SavedWindowRestoreUpdate update) =>
        new(files, StringComparer.Ordinal) { [update.FileName] = update.Bytes };

    private static Dictionary<string, ReadOnlyMemory<byte>> Store(string fixture = "table-and-wal") =>
        Directory.GetFiles(Path.Combine(Root, fixture)).ToDictionary(path => Path.GetFileName(path),
            path => (ReadOnlyMemory<byte>)File.ReadAllBytes(path), StringComparer.Ordinal);
}
