using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class TabManagerPlusCaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "TmpCapture_" + Guid.NewGuid().ToString("N"));
    private string Profile => Path.Combine(_root, "Default");
    private string Package => Path.Combine(Profile, "Extensions", SavedWindows.OfficialExtensionId, "7.0.0_0");
    private string Storage => Path.Combine(Profile, "Local Extension Settings", SavedWindows.OfficialExtensionId);

    private void Prepare(string fixture = "table-and-wal")
    {
        Directory.CreateDirectory(Package);
        Directory.CreateDirectory(Storage);
        File.WriteAllText(Path.Combine(Package, "manifest.json"),
            "{\"name\":\"Fixture\",\"version\":\"7.0.0\",\"manifest_version\":3}");
        var source = Path.Combine(AppContext.BaseDirectory, "Fixtures", "TabManagerPlus", "storage", fixture);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(Storage, Path.GetFileName(file)));
    }

    private BraveTabManagerPlusCapture Read() =>
        BraveTabManagerPlusCapture.Read(Profile, () => ProcessPresence.Stopped);

    private Dictionary<string, string> Hashes() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    [Theory]
    [InlineData("table-and-wal", 1)]
    [InlineData("table-only", 1)]
    [InlineData("empty", 0)]
    public void CapturesSupportedStoredWindowsWithoutWritingAnyFiles(string fixture, int count)
    {
        Prepare(fixture);
        var before = Hashes();
        using var capture = Read();
        Assert.Equal(count, capture.Data.Windows.Length);
        Assert.Equal("7.0.0", capture.Data.ExtensionVersion);
        Assert.Equal(before.Count, capture.SourceFileCount);
        Assert.InRange(capture.CapturedUtc, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        capture.VerifyUnchanged();
        Assert.Equal(before.OrderBy(pair => pair.Key), Hashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public void DeletedCollectionIsUnavailableRatherThanEmpty()
    {
        Prepare("deleted");
        var error = Assert.Throws<BackupOperationException>(() => Read());
        Assert.Equal("tabmanagerplus-data-unavailable", error.Code);
    }

    [Theory]
    [InlineData(ProcessPresence.Running)]
    [InlineData(ProcessPresence.Unknown)]
    [InlineData((ProcessPresence)99)]
    public void RunningOrIndeterminateBrowserBlocksRead(ProcessPresence state)
    {
        Prepare();
        var before = Hashes();
        var error = Assert.Throws<BackupOperationException>(() =>
            BraveTabManagerPlusCapture.Read(Profile, () => state));
        Assert.Equal("tabmanagerplus-browser-not-stopped", error.Code);
        Assert.Equal(before.OrderBy(pair => pair.Key), Hashes().OrderBy(pair => pair.Key));
    }

    [Fact]
    public void FailedProcessObserverFailsClosedWithoutLeakingItsMessage()
    {
        Prepare();
        var error = Assert.Throws<BackupOperationException>(() => BraveTabManagerPlusCapture.Read(Profile,
            () => throw new IOException("private-secret")));
        Assert.Equal("tabmanagerplus-browser-not-stopped", error.Code);
        Assert.DoesNotContain("private-secret", error.ToString());
    }

    [Fact]
    public void BrowserStartingDuringCaptureBlocksTheResult()
    {
        Prepare();
        var calls = 0;
        var error = Assert.Throws<BackupOperationException>(() => BraveTabManagerPlusCapture.Read(Profile,
            () => ++calls == 1 ? ProcessPresence.Stopped : ProcessPresence.Running));
        Assert.Equal("tabmanagerplus-browser-not-stopped", error.Code);
        Assert.True(calls >= 2);
    }

    [Fact]
    public void BrowserStartingAfterCaptureBlocksPublicationCheck()
    {
        Prepare();
        var state = ProcessPresence.Stopped;
        using var capture = BraveTabManagerPlusCapture.Read(Profile, () => state);
        state = ProcessPresence.Running;
        Assert.Equal("tabmanagerplus-browser-not-stopped",
            Assert.Throws<BackupOperationException>(() => capture.VerifyUnchanged()).Code);
    }

    [Fact]
    public void SameSizeManifestChangeWithRestoredTimestampIsDetectedByHash()
    {
        Prepare();
        using var capture = Read();
        var path = Path.Combine(Package, "manifest.json");
        var timestamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, File.ReadAllText(path).Replace("Fixture", "Changed"));
        File.SetLastWriteTimeUtc(path, timestamp);
        Assert.Equal("tabmanagerplus-source-changed",
            Assert.Throws<BackupOperationException>(() => capture.VerifyUnchanged()).Code);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("alter")]
    public void SourceTreeAndDatabaseChangesBlockPublicationCheck(string mutation)
    {
        Prepare();
        using var capture = Read();
        var path = Path.Combine(Storage, "000004.log");
        if (mutation == "add") File.WriteAllText(Path.Combine(Storage, "000099.log"), "new");
        else if (mutation == "remove") File.Delete(path);
        else
        {
            var timestamp = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path);
            bytes[^1] ^= 1;
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, timestamp);
        }

        Assert.Equal("tabmanagerplus-source-changed",
            Assert.Throws<BackupOperationException>(() => capture.VerifyUnchanged()).Code);
    }

    [Theory]
    [InlineData("6.0.0", "3")]
    [InlineData("7.0.1", "3")]
    [InlineData("7.0.0", "2")]
    [InlineData("7.0.0", "\"3\"")]
    public void UnsupportedPackageManifestBlocksCapture(string version, string format)
    {
        Prepare();
        File.WriteAllText(Path.Combine(Package, "manifest.json"),
            "{\"version\":\"" + version + "\",\"manifest_version\":" + format + "}");
        Assert.Equal("tabmanagerplus-version-unsupported",
            Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void RetainedDifferentPackageVersionIsAmbiguousAndBlocked()
    {
        Prepare();
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Package)!, "6.0.0_0"));
        Assert.Equal("tabmanagerplus-version-unsupported",
            Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void StorageVersionMustMatchSupportedPackage()
    {
        Prepare();
        // The frozen upstream table contains the stored version. Altering its bytes must
        // never result in a successful version guess from the directory name.
        var path = Path.Combine(Storage, "000005.ldb");
        var bytes = File.ReadAllBytes(path);
        bytes[8] ^= 1;
        File.WriteAllBytes(path, bytes);
        var error = Assert.Throws<BackupOperationException>(() => Read());
        Assert.Equal("tabmanagerplus-source-invalid", error.Code);
        Assert.DoesNotContain(Profile, error.ToString());
    }

    [Theory]
    [InlineData("\"6.0.0\"")]
    [InlineData("\"7.0.1\"")]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData(null)]
    public void ValidDatabaseWithWrongOrMissingStoredVersionIsUnsupported(string? version)
    {
        Prepare();
        AppendStorageOperation("version", version);
        Assert.Equal("tabmanagerplus-version-unsupported",
            Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void InvalidSavedWindowInOtherwiseValidDatabaseIsNotDropped()
    {
        Prepare();
        AppendStorageOperation("sessions", "{\"private-title\":{\"tabs\":[]}}");
        var error = Assert.Throws<BackupOperationException>(() => Read());
        Assert.Equal("tabmanagerplus-source-invalid", error.Code);
        Assert.DoesNotContain("private-title", error.ToString());
        Assert.Null(error.InnerException);
    }

    // Synthetic update encoded from the pinned LevelDB write-batch/log specification;
    // this does not call the production decoder to construct or checksum test data.
    private void AppendStorageOperation(string key, string? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(5UL); // Frozen source's last WAL sequence is four.
            writer.Write(1U);
            writer.Write((byte)(value is null ? 0 : 1));
            var keyBytes = Encoding.UTF8.GetBytes(key);
            writer.Write((byte)keyBytes.Length);
            writer.Write(keyBytes);
            if (value is not null)
            {
                var valueBytes = Encoding.UTF8.GetBytes(value);
                Assert.True(valueBytes.Length < 128);
                writer.Write((byte)valueBytes.Length);
                writer.Write(valueBytes);
            }
        }

        var payload = stream.ToArray();
        var record = new byte[payload.Length + 7];
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)payload.Length);
        record[6] = 1;
        payload.CopyTo(record, 7);
        uint crc = 0xffffffff;
        foreach (var valueByte in record.AsSpan(6))
        {
            crc ^= valueByte;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0x82f63b78 ^ (crc >> 1) : crc >> 1;
        }

        crc ^= 0xffffffff;
        BinaryPrimitives.WriteUInt32LittleEndian(record, unchecked(((crc >> 15) | (crc << 17)) + 0xa282ead8));
        using var output = new FileStream(Path.Combine(Storage, "000004.log"), FileMode.Append);
        output.Write(record);
    }

    [Fact]
    public void DevelopmentIdentityIsNotAnAliasForStoreIdentity()
    {
        Prepare();
        Directory.Move(Path.GetDirectoryName(Package)!,
            Path.Combine(Profile, "Extensions", "jajkejgbojkolgjiimaebpgenkgnejfe"));
        Assert.Throws<BackupOperationException>(() => Read());
    }

    [Fact]
    public void RejectsOversizedSourceBeforeReadingItsContents()
    {
        Prepare();
        using (var file = new FileStream(Path.Combine(Storage, "000099.ldb"), FileMode.CreateNew))
            file.SetLength((long)OfflineLevelDb.MaximumSnapshotBytes + 1);
        Assert.Equal("tabmanagerplus-source-limit", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void RejectsDirectoryAsDatabaseFile()
    {
        Prepare();
        Directory.CreateDirectory(Path.Combine(Storage, "nested"));
        Assert.Equal("tabmanagerplus-source-unsafe", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void RejectsLinkedSourceOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        Prepare();
        var outside = Path.Combine(_root, "outside.log");
        File.WriteAllText(outside, "not-source");
        File.CreateSymbolicLink(Path.Combine(Storage, "000099.log"), outside);
        Assert.Equal("tabmanagerplus-source-unsafe", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void DisposalPreventsUseAndCancellationStopsCaptureAndVerification()
    {
        Prepare();
        var capture = Read();
        capture.Dispose();
        capture.Dispose();
        Assert.Throws<ObjectDisposedException>(() => capture.Data);
        Assert.Throws<ObjectDisposedException>(() => capture.VerifyUnchanged());
        using var cancel = new CancellationTokenSource();
        using var active = BraveTabManagerPlusCapture.Read(Profile, () => ProcessPresence.Stopped, cancel.Token);
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => active.VerifyUnchanged());
        Assert.Throws<OperationCanceledException>(() => BraveTabManagerPlusCapture.Read(Profile,
            () => ProcessPresence.Stopped, cancel.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}