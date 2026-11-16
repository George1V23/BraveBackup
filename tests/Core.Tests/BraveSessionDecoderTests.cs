using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BraveBackup.Core;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class BraveSessionDecoderTests
{
    internal static byte[] Fixture(string name = "Session_10") =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Sessions", name));

    private static PortableSession Decode(byte[] bytes) =>
        BraveSessionDecoder.Decode(bytes, "Sessions/Session_10", DateTimeOffset.UnixEpoch);

    [Fact]
    public void IndependentCppFixture_ReplaysCurrentNavigationOrderMoveClosePinAndGroups()
    {
        var model = Decode(Fixture());
        Assert.Equal(new[] { 1, 2 }, model.Windows.Select(window => window.Id));
        Assert.Equal(1, model.ActiveWindowId);
        var first = model.Windows[0];
        Assert.Single(first.Tabs);
        Assert.Equal(11, first.Tabs[0].Id);
        Assert.Equal("https://selected.example.test/", first.Tabs[0].Url);
        Assert.Equal("Selected ☃", first.Tabs[0].Title);
        Assert.True(first.Tabs[0].Pinned);
        Assert.Equal(0, first.SelectedTabIndex);
        var group = Assert.Single(first.Groups);
        Assert.Equal("Group Îñ", group.Title);
        Assert.Equal(6u, group.Color);
        Assert.True(group.Collapsed);
        Assert.Equal("112233445566778899aabbccddeeff00", group.Id);
        Assert.Equal(group.Id, first.Tabs[0].GroupId);
        var second = model.Windows[1];
        Assert.Equal(new[] { 21, 12 }, second.Tabs.Select(tab => tab.Id));
        Assert.Equal(1, second.SelectedTabIndex);
        var bytes = PortableSessionFormat.SerializeBounded(model, PortableSessionFormat.MaxPortableBytes);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("old.example", json);
        Assert.DoesNotContain("forward.example", json);
        Assert.DoesNotContain("closed.example", json);
        Assert.DoesNotContain("excluded-page-state", json);
        Assert.DoesNotContain("excluded-referrer", json);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(2, PortableSessionFormat.Parse(document.RootElement).Windows.Length);
    }

    [Theory]
    [InlineData("Session.encrypted-header.bin", "session-format-unsupported")]
    [InlineData("Session.truncated.bin", "session-malformed")]
    [InlineData("Session.unknown-command.bin", "session-command-unsupported")]
    public void UnsupportedOrIncompleteFile_NeverReturnsAPartialSession(string name, string code) =>
        Assert.Equal(code, Assert.Throws<BackupOperationException>(() => Decode(Fixture(name))).Code);

    [Fact]
    public void CompleteEmptySnapshot_IsDifferentFromMissingMarker()
    {
        Assert.Empty(Decode(Fixture("Session.empty-v3.bin")).Windows);
        Assert.Equal("session-marker-missing", Assert.Throws<BackupOperationException>(() => Decode(Header())).Code);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(11)]
    [InlineData(24)]
    public void NavigationPruning_ReplaysSelectionWithoutReturningRemovedHistory(int command)
    {
        var data = Fixture();
        byte[] payload = command == 24 ? Ints(11, 1, 1) : Ints(11, command == 5 ? 1 : 1);
        data = Append(data, (byte)command, payload);
        var selected = Decode(data).Windows[0].Tabs[0].Url;
        Assert.Equal(
            command == 5 ? "https://old.example.test/" :
            command == 11 ? "https://selected.example.test/" : "https://old.example.test/", selected);
    }

    [Fact]
    public void MissingGroupMetadata_IsExplicitAndClosingAWindowDoesNotKeepItsTabs()
    {
        var bytes = Fixture();
        var stripped = Header();
        for (var offset = 8; offset < bytes.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset));
            if (bytes[offset + 2] != 27)
                stripped = stripped.Concat(bytes.AsSpan(offset, length + 2).ToArray()).ToArray();
            offset += length + 2;
        }

        var group = Assert.Single(Decode(stripped).Windows[0].Groups);
        Assert.Null(group.Title);
        Assert.Null(group.Color);
        Assert.Null(group.Collapsed);
        var close = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(close, 1);
        var model = Decode(Append(bytes, 17, close));
        Assert.Single(model.Windows);
        Assert.Equal(2, model.Windows[0].Id);
        Assert.Null(model.ActiveWindowId);
    }

    [Fact]
    public void InvalidPickleAlignmentLengthsAndNativeAbi_AreRejected()
    {
        var badLength = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(badLength, int.MaxValue);
        Assert.Throws<BackupOperationException>(() => Decode(Append(Fixture(), 6, badLength)));
        Assert.Throws<BackupOperationException>(() => Decode(Append(Fixture(), 25, new byte[24])));
        Assert.Throws<BackupOperationException>(() => Decode(Append(Fixture(), 16, new byte[12])));
        Assert.Throws<BackupOperationException>(() => Decode(Append(Fixture(), 24, Ints(11, int.MaxValue, 1))));
        Assert.Throws<BackupOperationException>(() => Decode(Append(Fixture(), 255, Array.Empty<byte>())));
    }

    [Fact]
    public void MissingNavigationAndActiveSplitState_BlockInsteadOfDroppingTabs()
    {
        var incomplete = Append(Append(Fixture(), 0, Ints(1, 90)), 2, Ints(90, 2));
        Assert.Equal("session-incomplete", Assert.Throws<BackupOperationException>(() => Decode(incomplete)).Code);
        var split = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(split, 11);
        split[8] = 1;
        split[24] = 1;
        Assert.Equal("session-incomplete",
            Assert.Throws<BackupOperationException>(() => Decode(Append(Fixture(), 36, split))).Code);
    }

    [Fact]
    public void CancellationAndCommandLimit_AreBounded()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => BraveSessionDecoder.Decode(Fixture(), "Sessions/Session_10",
            DateTimeOffset.UnixEpoch, token: cancellation.Token));
        using var source = new MemoryStream();
        source.Write(Header());
        var command = Append(Array.Empty<byte>(), 20, Ints(1));
        for (var index = 0; index <= PortableSessionFormat.MaxCommands; index++) source.Write(command);
        Assert.Equal("session-limit", Assert.Throws<BackupOperationException>(() => Decode(source.ToArray())).Code);
    }

    internal static byte[] Append(byte[] bytes, byte id, byte[] payload)
    {
        var result = new byte[bytes.Length + 3 + payload.Length];
        bytes.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(bytes.Length), checked((ushort)(payload.Length + 1)));
        result[bytes.Length + 2] = id;
        payload.CopyTo(result, bytes.Length + 3);
        return result;
    }

    internal static byte[] Ints(params int[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var index = 0; index < values.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(index * 4), values[index]);
        return bytes;
    }

    private static byte[] Header() => new byte[] { 0x53, 0x4e, 0x53, 0x53, 3, 0, 0, 0 };
}