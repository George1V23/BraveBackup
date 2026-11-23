using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class WindowsRecoveryFileCaptureTests
{
    [Theory]
    [InlineData("Bookmarks")]
    [InlineData("Login Data")]
    [InlineData("Session_123")]
    public void AcceptsLiteralTargetNames(string name) => WindowsRecoveryFileCapture.ValidateChildName(name);

    [Theory]
    [InlineData("../Bookmarks")]
    [InlineData("nested\\Bookmarks")]
    [InlineData("Bookmarks:alternate")]
    [InlineData("Bookmarks.")]
    [InlineData("Bookmarks ")]
    [InlineData("NUL.json")]
    [InlineData("lpt².txt")]
    [InlineData("CONIN$")]
    public void RejectsNamesThatWindowsCouldResolveDifferently(string name) =>
        Assert.Throws<ArgumentException>(() => WindowsRecoveryFileCapture.ValidateChildName(name));

    [Fact]
    public void StreamFrameMustDescribeExactlyTheDefaultDataStream()
    {
        var valid = StreamFrame("::$DATA", 12);
        Assert.True(WindowsRecoveryFileCapture.HasOnlyUnnamedDataStream(valid, 12));
        Assert.False(WindowsRecoveryFileCapture.HasOnlyUnnamedDataStream(valid, 11));
        Assert.False(WindowsRecoveryFileCapture.HasOnlyUnnamedDataStream(valid.AsSpan()[..^1], 12));
        Assert.False(WindowsRecoveryFileCapture.HasOnlyUnnamedDataStream(StreamFrame(":x:$DATA", 12), 12));
        BinaryPrimitives.WriteUInt32LittleEndian(valid, 48);
        Assert.False(WindowsRecoveryFileCapture.HasOnlyUnnamedDataStream(valid, 12));
        Assert.True(WindowsRecoveryFileCapture.HasOnlyUnnamedDataStream(StreamFrame("::$DATA", 0), 0));
    }

    [WindowsOnlyFact]
    public void CapturesContentsMetadataAndOriginalAbsenceWithoutCreatingFiles()
    {
        var directory = Directory.CreateTempSubdirectory("WindowsCapture_");
        try
        {
            var path = Path.Combine(directory.FullName, "Bookmarks");
            File.WriteAllBytes(path, "synthetic original"u8.ToArray());
            using var existing = WindowsRecoveryFileCapture.Read(directory.FullName, "Bookmarks");
            using var missing = WindowsRecoveryFileCapture.Read(directory.FullName, "Missing");

            Assert.True(existing.Exists);
            Assert.Equal("synthetic original"u8.ToArray(), existing.Content.ToArray());
            WindowsRecoveryMetadata.Validate(existing.Metadata.Span);
            Assert.False(missing.Exists);
            Assert.Empty(missing.Content.ToArray());
            Assert.Empty(missing.Metadata.ToArray());
            Assert.Single(directory.GetFiles());
            Assert.Equal(existing.Content.ToArray(), File.ReadAllBytes(path));
            var borrowed = existing.Content;
            existing.Dispose();
            Assert.All(borrowed.ToArray(), value => Assert.Equal(0, value));
        }
        finally { directory.Delete(recursive: true); }
    }

    [WindowsOnlyFact]
    public void RejectsHardLinksAndNamedStreams()
    {
        var directory = Directory.CreateTempSubdirectory("WindowsCapture_");
        try
        {
            var path = Path.Combine(directory.FullName, "Bookmarks");
            var link = Path.Combine(directory.FullName, "Alias");
            File.WriteAllText(path, "synthetic original");
            Assert.True(CreateHardLink(link, path, IntPtr.Zero), $"Could not create the fixture hard link ({Marshal.GetLastPInvokeError()}).");
            Assert.Throws<IOException>(() => WindowsRecoveryFileCapture.Read(directory.FullName, "Bookmarks"));
            File.Delete(link);

            File.WriteAllText(path + ":fixture-stream", "synthetic stream");
            Assert.Throws<IOException>(() => WindowsRecoveryFileCapture.Read(directory.FullName, "Bookmarks"));
            Assert.Equal("synthetic original", File.ReadAllText(path));
            Assert.Equal("synthetic stream", File.ReadAllText(path + ":fixture-stream"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [WindowsOnlyFact]
    public void AnAlreadyOpenedWriterBlocksStableCapture()
    {
        var directory = Directory.CreateTempSubdirectory("WindowsCapture_");
        try
        {
            var path = Path.Combine(directory.FullName, "Preferences");
            File.WriteAllText(path, "{}");
            using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Assert.Throws<IOException>(() => WindowsRecoveryFileCapture.Read(directory.FullName, "Preferences"));
        }
        finally { directory.Delete(recursive: true); }
    }

    private static byte[] StreamFrame(string name, long length)
    {
        var encoded = Encoding.Unicode.GetBytes(name);
        var result = new byte[24 + encoded.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), (uint)encoded.Length);
        BinaryPrimitives.WriteInt64LittleEndian(result.AsSpan(8), length);
        encoded.CopyTo(result.AsSpan(24));
        return result;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
}
