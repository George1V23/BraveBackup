using System.Security.Cryptography;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class AffiliationSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AffiliationSnapshot_" + Guid.NewGuid().ToString("N"));
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Affiliations");
    private string Main => Path.Combine(_root, BraveAffiliationSnapshot.DatabaseFileName);
    public AffiliationSnapshotTests() => Directory.CreateDirectory(_root);
    private void Prepare() => File.Copy(Path.Combine(Fixtures, "Affiliation.v7.synthetic.sqlite"), Main);
    private BraveAffiliationSnapshot Read(Func<ProcessPresence>? observe = null, CancellationToken token = default) =>
        BraveAffiliationSnapshot.Read(_root, observe ?? (() => ProcessPresence.Stopped), token);
    private Dictionary<string, string> Hashes() => Directory.GetFiles(_root).ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));

    [Fact]
    public void CapturesOnlyKnownCacheAndLeavesAllSourceFilesUnchanged()
    {
        Prepare(); File.WriteAllText(Path.Combine(_root, "Cookies"), "excluded");
        var before = Hashes(); using var snapshot = Read();
        Assert.Equal(3, snapshot.Cache.Groups.Count); snapshot.VerifyUnchanged();
        Assert.Equal(before.OrderBy(p => p.Key), Hashes().OrderBy(p => p.Key)); Assert.Equal(2, Directory.GetFiles(_root).Length);
        Assert.DoesNotContain("example.com", snapshot.ToString());
        snapshot.Dispose(); Assert.Throws<ObjectDisposedException>(() => snapshot.Cache); Assert.Throws<ObjectDisposedException>(() => snapshot.VerifyUnchanged());
    }

    [Fact]
    public void AppliesCommittedWalWithoutOpeningOrModifyingSourceSharedMemory()
    {
        foreach (var path in Directory.GetFiles(Path.Combine(Fixtures, "Wal"))) File.Copy(path, Path.Combine(_root, Path.GetFileName(path)));
        var before = Hashes(); using var snapshot = Read();
        Assert.All(snapshot.Cache.GetGroup("https://alpha.example.com").Facets, facet => Assert.Equal("wal.synthetic.invalid", facet.MainDomain));
        snapshot.VerifyUnchanged(); Assert.Equal(before.OrderBy(p => p.Key), Hashes().OrderBy(p => p.Key));
    }

    [Fact]
    public void MissingOrOrphanCacheIsNeverAnAssumedEmptyCache()
    {
        Assert.Equal("passwords-affiliation-source-missing", Assert.Throws<BackupOperationException>(() => Read()).Code);
        File.WriteAllBytes(Main + "-wal", []);
        Assert.Equal("passwords-affiliation-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Theory]
    [InlineData(ProcessPresence.Running)]
    [InlineData(ProcessPresence.Unknown)]
    public void RunningOrUnknownBrowserBlocksCapture(ProcessPresence state)
    {
        Prepare(); Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => Read(() => state)).Code);
    }

    [Fact]
    public void FailedObservationAndLateStartBlockCaptureOrPublication()
    {
        Prepare(); Assert.Throws<BackupOperationException>(() => Read(() => throw new IOException()));
        var state = ProcessPresence.Stopped; using var snapshot = Read(() => state); state = ProcessPresence.Running;
        Assert.Equal("passwords-close-brave", Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged()).Code);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("added")]
    [InlineData("removed")]
    public void ChecksDetectChangedBytesEvenWithRestoredStampAndChangedFileSets(string change)
    {
        Prepare(); using var snapshot = Read();
        if (change == "bytes")
        {
            var stamp = File.GetLastWriteTimeUtc(Main); var bytes = File.ReadAllBytes(Main); bytes[^1] ^= 1;
            File.WriteAllBytes(Main, bytes); File.SetLastWriteTimeUtc(Main, stamp);
        }
        else if (change == "added") File.WriteAllBytes(Main + "-shm", []);
        else File.Delete(Main);
        Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged());
    }

    [Fact]
    public void NonemptyJournalIsNotRecoveredOrSilentlyIgnored()
    {
        Prepare(); File.WriteAllText(Main + "-journal", "synthetic-journal"); var before = Hashes();
        Assert.Equal("passwords-affiliation-journal-unsupported", Assert.Throws<BackupOperationException>(() => Read()).Code);
        Assert.Equal(before.OrderBy(p => p.Key), Hashes().OrderBy(p => p.Key));
    }

    [Fact]
    public void EmptyJournalIsRetainedInSourceChecks()
    {
        Prepare(); File.WriteAllBytes(Main + "-journal", []); using var snapshot = Read();
        File.Delete(Main + "-journal"); Assert.Throws<BackupOperationException>(() => snapshot.VerifyUnchanged());
    }

    [Fact]
    public void CancellationBeforeAndAfterCaptureIsRespected()
    {
        Prepare(); using var cancellation = new CancellationTokenSource(); using var snapshot = Read(token: cancellation.Token);
        cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => Read(token: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => snapshot.VerifyUnchanged());
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("symlink")]
    public void UnsafeKnownFilesAreRefused(string kind)
    {
        if (kind == "symlink" && !OperatingSystem.IsLinux()) return;
        if (kind == "directory") Directory.CreateDirectory(Main);
        else { var actual = Path.Combine(_root, "actual"); File.WriteAllText(actual, "synthetic"); File.CreateSymbolicLink(Main, actual); }
        Assert.Equal("passwords-affiliation-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void IndividualAndAggregateLimitsRejectBeforeAllocatingImages()
    {
        using (var file = File.Create(Main)) file.SetLength(32L * 1024 * 1024 + 1);
        Assert.Equal("passwords-affiliation-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
        using (var file = File.Create(Main)) file.SetLength(32L * 1024 * 1024);
        foreach (var suffix in new[] { "-wal", "-shm" }) { using var file = File.Create(Main + suffix); file.SetLength(32L * 1024 * 1024); }
        Assert.Equal("passwords-affiliation-source-invalid", Assert.Throws<BackupOperationException>(() => Read()).Code);
    }

    [Fact]
    public void SourceMutationDuringCaptureNeverReturnsStaleCache()
    {
        Prepare(); var observations = 0;
        var error = Assert.Throws<BackupOperationException>(() => Read(() =>
        {
            if (++observations == 3)
            {
                var stamp = File.GetLastWriteTimeUtc(Main); var bytes = File.ReadAllBytes(Main); bytes[^1] ^= 1;
                File.WriteAllBytes(Main, bytes); File.SetLastWriteTimeUtc(Main, stamp);
            }
            return ProcessPresence.Stopped;
        }));
        Assert.Equal("passwords-affiliation-source-changed", error.Code);
    }

    [Fact]
    public void SymlinkProfileAncestorIsRejectedBeforeOpeningDatabase()
    {
        if (!OperatingSystem.IsLinux()) return;
        var actual = Path.Combine(_root, "actual"); Directory.CreateDirectory(actual);
        File.Copy(Path.Combine(Fixtures, "Affiliation.v7.synthetic.sqlite"), Path.Combine(actual, BraveAffiliationSnapshot.DatabaseFileName));
        var alias = Path.Combine(_root, "alias"); Directory.CreateSymbolicLink(alias, actual);
        Assert.Equal("passwords-affiliation-source-invalid", Assert.Throws<BackupOperationException>(() =>
            BraveAffiliationSnapshot.Read(alias, () => ProcessPresence.Stopped)).Code);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
