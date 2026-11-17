using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class RawPasswordSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RawPasswords_" + Guid.NewGuid().ToString("N"));
    public RawPasswordSnapshotTests() => Directory.CreateDirectory(_root);
    private void FileAt(string name, byte[]? bytes = null) => File.WriteAllBytes(Path.Combine(_root, name), bytes ?? "synthetic raw bytes"u8.ToArray());
    private BraveRawPasswordSnapshot Read(bool selected = true, Func<ProcessPresence>? observe = null, CancellationToken token = default)
        => BraveRawPasswordSnapshot.Read(_root, selected, observe ?? (() => ProcessPresence.Stopped), token);

    [Fact]
    public void PreservesCorruptFilesAndAllKnownCompanionsWithoutInterpretationOrWrites()
    {
        foreach (var name in new[] { "Login Data", "Login Data For Account" })
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) FileAt(name + suffix);
        FileAt("Local State"); FileAt("Cookies"); FileAt("Login Data.unknown"); FileAt("Login Data.json");
        var before = Directory.GetFiles(_root).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        using var snapshot = Read();
        Assert.Equal(8, snapshot.Files.Count); Assert.Empty(snapshot.MissingMainDatabases);
        foreach (var file in snapshot.Files) Assert.Equal(before[file.Key], file.Value.ToArray());
        Assert.DoesNotContain("Local State", snapshot.Files.Keys); Assert.DoesNotContain("Cookies", snapshot.Files.Keys);
        snapshot.VerifyUnchanged();
        foreach (var entry in before) Assert.Equal(entry.Value, File.ReadAllBytes(Path.Combine(_root, entry.Key!)));
        Assert.Equal(before.Count, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void NoExplicitChoiceDoesNotInspectEvenMissingProfile()
    {
        var calls = 0;
        var error = Assert.Throws<BackupOperationException>(() => BraveRawPasswordSnapshot.Read(Path.Combine(_root, "missing"), false,
            () => { calls++; return ProcessPresence.Stopped; }));
        Assert.Equal("passwords-raw-not-selected", error.Code); Assert.Equal(0, calls);
    }

    [Fact]
    public void MissingIsDifferentFromPresentZeroLengthRawFile()
    {
        Assert.Equal("passwords-raw-source-missing", Assert.Throws<BackupOperationException>(() => Read()).Code);
        FileAt("Login Data", []);
        using var snapshot = Read();
        Assert.Empty(Assert.Single(snapshot.Files).Value.ToArray());
    }

    [Fact]
    public void OrphanCompanionsArePreservedAndExplicitlyMarkedIncomplete()
    {
        FileAt("Login Data-wal"); FileAt("Login Data For Account-journal");
        using var snapshot = Read();
        Assert.Equal(2, snapshot.Files.Count);
        Assert.Equal(new[] { "Login Data", "Login Data For Account" }, snapshot.MissingMainDatabases);
    }

    [Theory]
    [InlineData(ProcessPresence.Running)]
    [InlineData(ProcessPresence.Unknown)]
    public void RunningOrUnknownBrowserIsNeverRawSuccess(ProcessPresence state)
    {
        FileAt("Login Data");
        Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => Read(observe: () => state)).Code);
    }

    [Fact]
    public void ObservationFailureIsNotTreatedAsStopped()
    {
        FileAt("Login Data");
        Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => Read(observe: () => throw new IOException())).Code);
    }

    [Fact]
    public void BorrowedRawBuffersAreClearedOnDisposal()
    {
        FileAt("Login Data");
        var snapshot = Read(); var bytes = snapshot.Files["Login Data"];
        snapshot.Dispose(); snapshot.Dispose();
        Assert.All(bytes.ToArray(), value => Assert.Equal(0, value));
        Assert.Throws<ObjectDisposedException>(() => snapshot.Files);
        Assert.Throws<ObjectDisposedException>(() => snapshot.MissingMainDatabases);
        Assert.Throws<ObjectDisposedException>(() => snapshot.VerifyUnchanged());
        Assert.DoesNotContain("synthetic", snapshot.ToString());
    }

    [Theory]
    [InlineData("change")]
    [InlineData("add")]
    [InlineData("remove")]
    public void PublicationChecksDetectChangedSetOrBytes(string mutation)
    {
        FileAt("Login Data"); using var snapshot = Read();
        if (mutation == "change")
        {
            var path = Path.Combine(_root, "Login Data"); var timestamp = File.GetLastWriteTimeUtc(path);
            var bytes = File.ReadAllBytes(path); bytes[0] ^= 1; File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, timestamp);
        }
        else if (mutation == "add") FileAt("Login Data-wal");
        else File.Delete(Path.Combine(_root, "Login Data"));
        Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged());
    }

    [Fact]
    public void BrowserStartingAfterCaptureBlocksPublication()
    {
        FileAt("Login Data"); var state = ProcessPresence.Stopped;
        using var snapshot = Read(observe: () => state); state = ProcessPresence.Running;
        Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged()).Code);
    }

    [Fact]
    public void CancellationBeforeAndAfterCaptureIsRespected()
    {
        FileAt("Login Data"); using var cancellation = new CancellationTokenSource();
        using var snapshot = Read(token: cancellation.Token); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => snapshot.VerifyUnchanged());
        Assert.Throws<OperationCanceledException>(() => Read(token: cancellation.Token));
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("directory")]
    public void NonRegularKnownFilesAreRejected(string kind)
    {
        if (kind == "symlink" && !OperatingSystem.IsLinux()) return;
        if (kind == "directory") Directory.CreateDirectory(Path.Combine(_root, "Login Data"));
        else { FileAt("target"); File.CreateSymbolicLink(Path.Combine(_root, "Login Data"), Path.Combine(_root, "target")); }
        Assert.Equal("passwords-raw-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void OversizedFileFailsBeforeAllocation()
    {
        using (var stream = File.Create(Path.Combine(_root, "Login Data"))) stream.SetLength(64L * 1024 * 1024 + 1);
        Assert.Equal("passwords-raw-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void AggregateLimitFailsBeforeReadingSparseFiles()
    {
        foreach (var name in new[] { "Login Data", "Login Data-wal", "Login Data-shm" })
        { using var stream = File.Create(Path.Combine(_root, name)); stream.SetLength(64L * 1024 * 1024); }
        Assert.Equal("passwords-raw-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
