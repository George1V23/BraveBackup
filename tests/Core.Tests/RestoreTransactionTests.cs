using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;

public sealed class RestoreTransactionTests : IDisposable
{
    private readonly Store _store = new();
    private readonly List<RestoreFileImage> _images = [];
    private RestoreTransaction Create()
    {
        var installation = Guid.NewGuid(); var profile = Guid.NewGuid();
        var mutations = Enumerable.Range(0, 2).Select(index =>
        {
            var id = Guid.NewGuid(); _store.Paths[id] = Path.Combine(_store.Root, "target-" + index);
            if (index == 0) File.WriteAllText(_store.Paths[id], "original");
            var image = new RestoreFileImage(true, System.Text.Encoding.UTF8.GetBytes("replacement-" + index)); _images.Add(image);
            return new RestoreMutation(id, installation, profile, "bookmarks", image);
        }).ToArray();
        return new(mutations, _store, "synthetic-safety-password");
    }
    [Fact]
    public void PauseDeclineAndExplicitResumeKeepOriginalSafetySnapshot()
    {
        using var transaction = Create(); transaction.Prepare(); var safety = File.ReadAllBytes(_store.Safety);
        var reports = new List<RestoreTransactionProgress>();
        transaction.Progress = progress => { reports.Add(progress); if (progress.CompletedFiles == 1) transaction.RequestPause(); };
        transaction.Run(); Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.False(reports[0].CategoryCompleted); transaction.Rollback(false);
        Assert.Equal(RestoreTransactionState.Paused, transaction.State); Assert.Equal(1, _store.Writes);
        transaction.Run(); Assert.Equal(RestoreTransactionState.Completed, transaction.State);
        Assert.True(reports[^1].CategoryCompleted); Assert.Equal(safety, File.ReadAllBytes(_store.Safety));
        transaction.Rollback(true); Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First())); Assert.False(File.Exists(_store.Paths.Values.Last()));
        using var encrypted = File.OpenRead(_store.Safety); Assert.True(ArchiveEncryption.IsEncrypted(encrypted));
        Assert.True(File.Exists(_store.Journal));
    }
    [Theory]
    [InlineData("safety")]
    [InlineData("journal")]
    public void CorruptArtifactReadbackPreventsDestinationWrites(string artifact)
    {
        using var transaction = Create(); _store.Corrupt = artifact;
        Assert.ThrowsAny<Exception>(() => transaction.Prepare());
        Assert.Equal(0, _store.Writes); Assert.Equal(RestoreTransactionState.Failed, transaction.State);
    }
    [Fact]
    public void CancellationDuringResumePreflightKeepsTheTransactionPaused()
    {
        using var transaction = Create();
        transaction.Prepare();
        transaction.RequestPause();
        transaction.Run();
        using var pause = new CancellationTokenSource();
        _store.OnVerify = pause.Cancel;

        transaction.Run(pause.Token);

        Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.Equal(0, _store.Writes);
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First()));
    }
    [Fact]
    public void FailedResumeContextStaysPausedAndCannotOverwriteExternalChanges()
    {
        using var transaction = Create(); transaction.Prepare(); transaction.RequestPause(); transaction.Run();
        _store.Unsafe = true; Assert.Throws<IOException>(() => transaction.Run());
        Assert.Equal(RestoreTransactionState.Paused, transaction.State); Assert.Equal(0, _store.Writes);
        _store.Unsafe = false; File.WriteAllText(_store.Paths.Values.First(), "external");
        Assert.Throws<IOException>(() => transaction.Run()); Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.Equal("external", File.ReadAllText(_store.Paths.Values.First()));
    }
    [Fact]
    public void FailureAfterPublicationStillRollsBackIntentIncludingIncompleteCategory()
    {
        using var transaction = Create(); transaction.Prepare(); _store.ThrowAfterWrite = true;
        Assert.Throws<IOException>(() => transaction.Run()); Assert.Equal(RestoreEntryState.Intent, transaction.EntryStates[0]);
        var recovery = new List<RestoreTransactionProgress>(); transaction.Progress = recovery.Add;
        _store.ThrowAfterWrite = false; transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.True(recovery[^1].CategoryCompleted);
        Assert.Equal(recovery[^1].TotalFiles, recovery[^1].CompletedFiles);
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First())); Assert.False(File.Exists(_store.Paths.Values.Last()));
    }
    [Fact]
    public void RollbackConflictReportsPartialRecoveryAndCanBeRetried()
    {
        using var transaction = Create(); transaction.Prepare(); transaction.Run();
        var first = _store.Paths.Values.First(); File.WriteAllText(first, "external");
        transaction.Rollback(true); Assert.Equal(RestoreTransactionState.RecoveryIncomplete, transaction.State);
        Assert.Equal("external", File.ReadAllText(first)); Assert.False(File.Exists(_store.Paths.Values.Last()));
        File.WriteAllText(first, "replacement-0"); transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State); Assert.Equal("original", File.ReadAllText(first));
    }
    [Fact]
    public void JournalFailureBeforeIntentPreventsWriteAndPrecancelledPrepareWritesNothing()
    {
        using var transaction = Create(); transaction.Prepare(); _store.FailJournal = true;
        Assert.Throws<IOException>(() => transaction.Run()); Assert.Equal(0, _store.Writes);
        _store.FailJournal = false;
        using var another = Create(); Assert.Throws<OperationCanceledException>(() => another.Prepare(new CancellationToken(true)));
        Assert.Equal(0, _store.Writes);
    }
    [Fact]
    public void ChangedSafetyAfterPreparationPreventsExecution()
    {
        using var transaction = Create(); transaction.Prepare(); _store.Corrupt = "safety";
        Assert.ThrowsAny<Exception>(() => transaction.Run()); Assert.Equal(0, _store.Writes);
        Assert.Equal(RestoreTransactionState.Prepared, transaction.State);
    }

    [Fact]
    public void PauseDuringIntentJournalStopsBeforePublication()
    {
        using var transaction = Create(); transaction.Prepare();
        _store.OnJournal = () =>
        {
            if (transaction.EntryStates.Contains(RestoreEntryState.Intent)) transaction.RequestPause();
        };
        transaction.Run();
        Assert.Equal(RestoreTransactionState.Paused, transaction.State); Assert.Equal(0, _store.Writes);
        Assert.Equal(RestoreEntryState.Intent, transaction.EntryStates[0]);
        _store.OnJournal = null; transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State); Assert.Equal(0, _store.Writes);
    }
    [Fact]
    public void LostPublicationIsNotReportedAsApplied()
    {
        using var transaction = Create(); transaction.Prepare(); _store.DropWrite = true;
        Assert.Throws<IOException>(() => transaction.Run());
        Assert.Equal(RestoreTransactionState.Failed, transaction.State);
        Assert.Equal(RestoreEntryState.Intent, transaction.EntryStates[0]);
        _store.DropWrite = false; transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First()));
    }
    [Fact]
    public void RollbackWriteFailureKeepsRecoveryIncompleteAndSupportsExplicitRetry()
    {
        using var transaction = Create(); transaction.Prepare(); transaction.Run();
        _store.ThrowBeforeWrite = true; transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, transaction.State);
        Assert.All(transaction.EntryStates, entry => Assert.Equal(RestoreEntryState.Applied, entry));
        _store.ThrowBeforeWrite = false; transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First()));
        Assert.False(File.Exists(_store.Paths.Values.Last()));
    }
    [Fact]
    public void UnsafeRollbackContextCannotModifyTargets()
    {
        using var transaction = Create(); transaction.Prepare(); transaction.Run(); var writes = _store.Writes;
        _store.Unsafe = true; Assert.Throws<IOException>(() => transaction.Rollback(true));
        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, transaction.State); Assert.Equal(writes, _store.Writes);
        _store.Unsafe = false; transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
    }

    [Fact]
    public void ChangeDuringRestoreIntentPublicationPreservesExternalData()
    {
        using var transaction = Create();
        transaction.Prepare();
        var target = _store.Paths.Values.First();
        _store.OnJournal = () =>
        {
            if (transaction.State == RestoreTransactionState.Running &&
                transaction.EntryStates[0] == RestoreEntryState.Intent)
                File.WriteAllText(target, "external");
        };

        Assert.Throws<IOException>(() => transaction.Run());

        Assert.Equal(0, _store.Writes);
        Assert.Equal(RestoreTransactionState.Failed, transaction.State);
        Assert.Equal("external", File.ReadAllText(target));
    }

    [Fact]
    public void ChangeDuringRollbackIntentPublicationPreservesExternalData()
    {
        using var transaction = Create();
        transaction.Prepare();
        transaction.Run();
        var target = _store.Paths.Values.Last();
        var rollingBackRevisions = 0;
        _store.OnJournal = () =>
        {
            if (transaction.State == RestoreTransactionState.RollingBack && ++rollingBackRevisions == 2)
                File.WriteAllText(target, "external");
        };

        transaction.Rollback(true);

        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, transaction.State);
        Assert.Equal(RestoreEntryState.Applied, transaction.EntryStates[1]);
        Assert.Equal("external", File.ReadAllText(target));
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First()));
    }

    [Fact]
    public void ChangedSourceBlocksResumeButStillAllowsExplicitRollback()
    {
        using var transaction = Create();
        transaction.Prepare();
        transaction.Progress = progress =>
        {
            if (progress.State == RestoreTransactionState.Running) transaction.RequestPause();
        };
        transaction.Run();
        Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.Equal(1, _store.Writes);

        _store.SourceUnavailable = true;
        Assert.Throws<IOException>(() => transaction.Run());
        Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.Equal(1, _store.Writes);

        transaction.Rollback(true);
        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        Assert.Equal("original", File.ReadAllText(_store.Paths.Values.First()));
        Assert.False(File.Exists(_store.Paths.Values.Last()));
    }

    public void Dispose() { foreach (var image in _images) image.Dispose(); _store.Dispose(); }

    // Synthetic file-only adapter: deliberately has no production destination/path or metadata guarantees.
    private sealed class Store : IRestoreTransactionStore, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "RestoreTxn_" + Guid.NewGuid().ToString("N"));
        public Dictionary<Guid, string> Paths { get; } = [];
        public string Safety => Path.Combine(Root, "safety.encrypted");
        public string Journal => Path.Combine(Root, "journal.encrypted");
        public Action? OnVerify;
        public Action? OnJournal; public bool DropWrite; public bool ThrowBeforeWrite;
        public int Writes; public bool Unsafe; public bool ThrowAfterWrite; public bool FailJournal; public string? Corrupt;
        public Store() => Directory.CreateDirectory(Root);
        public bool SourceUnavailable;
        public void VerifyContext(CancellationToken token)
        {
            VerifyRecoveryContext(token);
            OnVerify?.Invoke();
            if (SourceUnavailable) throw new IOException("Synthetic source unavailable.");
        }
        public void VerifyRecoveryContext(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Unsafe) throw new IOException("Synthetic unsafe context.");
        }
        public RestoreFileImage ReadTarget(Guid id, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return File.Exists(Paths[id]) ? new(true, File.ReadAllBytes(Paths[id])) : new(false, []); }
        public void WriteTargetAtomic(Guid id, RestoreFileImage image)
        {
            Assert.True(File.Exists(Safety)); Assert.True(File.Exists(Journal));
            if (ThrowBeforeWrite) throw new IOException("Synthetic pre-publication failure.");
            Writes++; if (DropWrite) return;
            if (!image.Exists) File.Delete(Paths[id]);
            else { var temp = Paths[id] + ".tmp"; File.WriteAllBytes(temp, image.Content.Bytes.ToArray()); File.Move(temp, Paths[id], true); }
            if (ThrowAfterWrite) throw new IOException("Synthetic failure after publish.");
        }
        private static void Publish(string path, ReadOnlyMemory<byte> bytes)
        {
            var temp = path + ".tmp";
            using (var stream = BackupFileOperations.CreatePrivateFile(temp)) { stream.Write(bytes.Span); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        public void SaveSafety(ReadOnlyMemory<byte> bytes) => Publish(Safety, bytes);
        public byte[] ReadSafety() => Corrupt == "safety" ? [0] : File.ReadAllBytes(Safety);
        public void SaveJournal(ReadOnlyMemory<byte> bytes) { if (FailJournal) throw new IOException("Synthetic journal failure."); Publish(Journal, bytes); OnJournal?.Invoke(); }
        public byte[] ReadJournal() => Corrupt == "journal" ? [0] : File.ReadAllBytes(Journal);
        public void Dispose() => Directory.Delete(Root, true);
    }
}
