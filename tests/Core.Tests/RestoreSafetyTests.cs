using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class RestoreSafetyTests
{
    [Fact]
    public void SnapshotPreservesBytesMetadataAndOriginalAbsence()
    {
        using var snapshot = new RestoreSafetySnapshot(Guid.NewGuid(), [
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "bookmarks", new(true, "synthetic-original"u8, "metadata"u8)),
            new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "settings", new(false, []))]);
        using var bytes = RestoreSafetyFormat.Encode(snapshot);
        using var decoded = RestoreSafetyFormat.Decode(bytes.Bytes.Span);
        Assert.Equal(snapshot.OperationId, decoded.OperationId);
        Assert.Equal("synthetic-original"u8.ToArray(), decoded.Entries[0].Original.Content.Bytes.ToArray());
        Assert.Equal("metadata"u8.ToArray(), decoded.Entries[0].Original.Metadata.Bytes.ToArray());
        Assert.False(decoded.Entries[1].Original.Exists);
        Assert.Throws<InvalidDataException>(() => RestoreSafetyFormat.Decode(bytes.Bytes.Span[..^1]));
    }
    [Fact]
    public void JournalRejectsUnknownStateAndTrailingBytes()
    {
        var operation = Guid.NewGuid();
        var bytes = RestoreJournalFormat.Encode(operation, 1, RestoreTransactionState.Paused, [(Guid.NewGuid(), RestoreEntryState.Intent)]);
        Assert.Equal(operation, RestoreJournalFormat.Decode(bytes).Operation);
        Assert.Equal(RestoreTransactionState.Paused, RestoreJournalFormat.Decode(bytes).State);
        Assert.Throws<InvalidDataException>(() => RestoreJournalFormat.Decode(bytes.Concat(new byte[] { 0 }).ToArray()));
        bytes[32] = 255;
        Assert.Throws<InvalidDataException>(() => RestoreJournalFormat.Decode(bytes));
    }
    [Fact]
    public void AbsentImagesCannotContainDataAndDisposedImagesAreCleared()
    {
        Assert.Throws<InvalidDataException>(() => new RestoreFileImage(false, "invalid"u8));
        var image = new RestoreFileImage(true, "synthetic"u8); var borrowed = image.Content.Bytes;
        image.Dispose(); Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
    }
}
