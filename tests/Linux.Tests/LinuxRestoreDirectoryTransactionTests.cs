using System.Runtime.Versioning;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class LinuxRestoreDirectoryTransactionTests : IDisposable
{
    private const string ExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string SafetyPassword = "synthetic-directory-password";
    private static readonly DateTime OriginalTime = new(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DirectoryRestore_" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestartRollbackReopensPersistedDirectoryAndFileEvidence(bool changeFile)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp();
        if (!RecoveryDirectoryIdentityTests.SupportsRestartEvidence(Path.GetDirectoryName(setup.Target)!)) return;
        using var original = Capture(setup.Target);
        using var replacement = Capture(setup.Replacement);
        using var bookmarks = CaptureBookmarkFile(setup.Profile.ProfileDirectory);
        RestoreArtifactScope artifacts;
        using (var live = Open(setup, replacement, bookmarks))
        {
            artifacts = live.Artifacts;
            live.Transaction.Prepare();
            live.Transaction.Run();
            Assert.Equal(RestoreTransactionState.Completed, live.Transaction.State);
        }
        File.Delete(setup.Source);
        if (changeFile) File.WriteAllText(setup.Profile.BookmarksPath, "external bookmark edit");
        var initialJournal = File.ReadAllBytes(artifacts.JournalPath);
        var detected = new[] { (setup.App, setup.Profile) };
        using var reopened = LinuxRestartRollback.Open(artifacts.RestartRecordPath, SafetyPassword, detected, () => setup.Presence);
        var review = reopened.Review;
        Assert.Equal(RestoreRollbackDecision.RestoreOriginal, review.Targets[0].Decision);
        Assert.Equal(changeFile ? RestoreRollbackDecision.Conflict : RestoreRollbackDecision.RestoreOriginal, review.Targets[1].Decision);
        Assert.Equal(initialJournal, File.ReadAllBytes(artifacts.JournalPath));
        AssertImage(setup.Target, replacement);

        var result = reopened.Rollback(review);

        Assert.Equal(changeFile ? RestoreTransactionState.RecoveryIncomplete : RestoreTransactionState.RolledBack, result);
        AssertImage(setup.Target, original);
        Assert.Equal(changeFile ? "external bookmark edit" : "original-bookmarks", File.ReadAllText(setup.Profile.BookmarksPath));
        AssertNoStaging(setup.Target);
        var journal = RestoreArtifactStorage.InspectRestart(artifacts.RestartRecordPath, artifacts.SafetyPath,
            artifacts.JournalPath, SafetyPassword).Journal;
        Assert.Equal(result, journal.State);
        Assert.Equal(RestoreEntryState.Recovered, journal.Entries[0].State);
        Assert.Equal(changeFile ? RestoreEntryState.Applied : RestoreEntryState.Recovered, journal.Entries[1].State);
    }

    [Fact]
    public void RestartRejectsUnknownProfileAndOlderJournalDoesNotAuthorizeAnyWrites()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp();
        if (!RecoveryDirectoryIdentityTests.SupportsRestartEvidence(Path.GetDirectoryName(setup.Target)!)) return;
        using var replacement = Capture(setup.Replacement);
        RestoreArtifactScope artifacts;
        byte[] preparedJournal;
        using (var live = Open(setup, replacement))
        {
            artifacts = live.Artifacts;
            live.Transaction.Prepare();
            preparedJournal = File.ReadAllBytes(artifacts.JournalPath);
            live.Transaction.Run();
        }
        File.WriteAllBytes(artifacts.JournalPath, preparedJournal);
        Assert.Throws<InvalidDataException>(() => LinuxRestartRollback.Open(artifacts.RestartRecordPath, SafetyPassword,
            [], () => ProcessPresence.Stopped));
        Assert.Throws<IOException>(() => LinuxRestartRollback.Open(artifacts.RestartRecordPath, SafetyPassword,
            [(setup.App, setup.Profile)], () => ProcessPresence.Running));
        using var reopened = LinuxRestartRollback.Open(artifacts.RestartRecordPath, SafetyPassword,
            [(setup.App, setup.Profile)], () => ProcessPresence.Stopped);
        Assert.Equal(RestoreRollbackDecision.Conflict, Assert.Single(reopened.Review.Targets).Decision);
        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, reopened.Rollback(reopened.Review));
        AssertImage(setup.Target, replacement);
        Assert.Equal(preparedJournal, File.ReadAllBytes(artifacts.JournalPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedWholeDirectoryRestoreRollsBackFromVerifiedSafetyAfterSourceRemoval(bool initiallyEmpty)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp(initiallyEmpty);
        using var original = Capture(setup.Target);
        using var replacement = Capture(setup.Replacement);
        using var bookmarks = CaptureBookmarkFile(setup.Profile.ProfileDirectory);
        using var session = Open(setup, replacement, bookmarks);
        var transaction = session.Transaction;
        Assert.Empty(Directory.GetFiles(setup.Recovery));
        AssertImage(setup.Target, original);

        transaction.Prepare();
        var encrypted = File.ReadAllBytes(session.Artifacts.SafetyPath);
        using (var stream = File.OpenRead(session.Artifacts.SafetyPath))
        using (var plaintext = ArchiveEncryption.OpenReader(stream, SafetyPassword))
        using (var buffer = new MemoryStream())
        {
            plaintext.CopyTo(buffer);
            using var snapshot = RestoreSafetyFormat.Decode(buffer.ToArray());
            var directory = Assert.Single(snapshot.Entries, entry => entry.Category == "extensions");
            Assert.True(LinuxFlatDirectoryImage.Matches(original, directory.Original));
        }

        transaction.Progress = update =>
        {
            if (update.State == RestoreTransactionState.Running && update.Category == "extensions")
                transaction.RequestPause();
        };
        transaction.Run();

        Assert.Equal(RestoreTransactionState.Paused, transaction.State);
        Assert.Equal(new[] { RestoreEntryState.Applied, RestoreEntryState.Untouched }, transaction.EntryStates);
        AssertImage(setup.Target, replacement);
        Assert.False(File.Exists(Path.Combine(setup.Target, "old-only.ldb")));
        Assert.Equal("unrelated", File.ReadAllText(setup.Unrelated));
        Assert.Equal("original-bookmarks", File.ReadAllText(setup.Profile.BookmarksPath));
        AssertNoStaging(setup.Target);

        File.Delete(setup.Source);
        Assert.Throws<IOException>(() => transaction.Run());
        setup.Presence = ProcessPresence.Running;
        Assert.Throws<IOException>(() => transaction.Rollback(true));
        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, transaction.State);
        AssertImage(setup.Target, replacement);
        setup.Presence = ProcessPresence.Stopped;

        transaction.Rollback(true);

        Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
        AssertImage(setup.Target, original);
        Assert.Equal(OriginalTime, Directory.GetLastWriteTimeUtc(setup.Target));
        Assert.Equal("original-bookmarks", File.ReadAllText(setup.Profile.BookmarksPath));
        Assert.Equal("unrelated", File.ReadAllText(setup.Unrelated));
        Assert.Equal(encrypted, File.ReadAllBytes(session.Artifacts.SafetyPath));
        AssertNoStaging(setup.Target);
    }

    [Fact]
    public void ChangesMadeDuringFinalContextCheckPreventPublicationAndCleanStaging()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp();
        using var replacement = Capture(setup.Replacement);
        var manifest = Path.Combine(setup.Target, "CURRENT");
        using var checkpoint = new LinuxRestoreDirectoryCheckpoint(Path.GetDirectoryName(setup.Target)!, ExtensionId,
            replacement, CancellationToken.None);
        var originalTime = File.GetLastWriteTimeUtc(manifest);

        Assert.Throws<IOException>(() => checkpoint.Publish(replacement, () =>
        {
            File.WriteAllText(manifest, "external");
            File.SetLastWriteTimeUtc(manifest, originalTime);
        }));

        Assert.Equal("external", File.ReadAllText(manifest));
        Assert.True(File.Exists(Path.Combine(setup.Target, "old-only.ldb")));
        Assert.False(File.Exists(Path.Combine(setup.Target, "new-only.ldb")));
        Assert.Equal("unrelated", File.ReadAllText(setup.Unrelated));
        AssertNoStaging(setup.Target);
    }

    [Fact]
    public void ChangedPrivateStagingIsPreservedAndNeverPublished()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp();
        using var replacement = Capture(setup.Replacement);
        using var original = Capture(setup.Target);
        var parent = Path.GetDirectoryName(setup.Target)!;
        using var checkpoint = new LinuxRestoreDirectoryCheckpoint(parent, ExtensionId, replacement, CancellationToken.None);

        Assert.Throws<IOException>(() => checkpoint.Publish(replacement, () =>
        {
            var stage = Assert.Single(Directory.EnumerateDirectories(parent, ".restore-directory-*"));
            File.WriteAllText(Path.Combine(stage, "image", "CURRENT"), "externally changed");
        }));

        AssertImage(setup.Target, original);
        var retained = Assert.Single(Directory.EnumerateDirectories(parent, ".restore-directory-*"));
        Assert.Equal("externally changed", File.ReadAllText(Path.Combine(retained, "image", "CURRENT")));
    }

    [Fact]
    public void RollbackPreservesAnExternallyChangedPublishedDirectory()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp();
        using var replacement = Capture(setup.Replacement);
        using var session = Open(setup, replacement);
        session.Transaction.Prepare();
        session.Transaction.Run();
        Assert.Equal(RestoreTransactionState.Completed, session.Transaction.State);

        var manifest = Path.Combine(setup.Target, "CURRENT");
        var timestamp = File.GetLastWriteTimeUtc(manifest);
        File.WriteAllText(manifest, "external");
        File.SetLastWriteTimeUtc(manifest, timestamp);
        session.Transaction.Rollback(true);

        Assert.Equal(RestoreTransactionState.RecoveryIncomplete, session.Transaction.State);
        Assert.Equal("external", File.ReadAllText(manifest));
        Assert.True(File.Exists(Path.Combine(setup.Target, "new-only.ldb")));
        Assert.True(File.Exists(session.Artifacts.SafetyPath));
        AssertNoStaging(setup.Target);
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("linked-file")]
    [InlineData("linked-directory")]
    [InlineData("missing")]
    public void UnsafeOrAbsentStoreIsRejectedBeforeCreatingArtifacts(string caseName)
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp(initiallyEmpty: true);
        using var replacement = Capture(setup.Replacement);
        switch (caseName)
        {
            case "nested":
                Directory.CreateDirectory(Path.Combine(setup.Target, "nested"));
                break;
            case "linked-file":
                File.CreateSymbolicLink(Path.Combine(setup.Target, "linked"), setup.Unrelated);
                break;
            case "linked-directory":
                Directory.Delete(setup.Target);
                Directory.CreateSymbolicLink(setup.Target, setup.Replacement);
                break;
            case "missing":
                Directory.Delete(setup.Target);
                break;
        }

        Assert.Throws<IOException>(() => Open(setup, replacement));
        Assert.Empty(Directory.GetFiles(setup.Recovery));
        Assert.Equal("unrelated", File.ReadAllText(setup.Unrelated));
        AssertNoStaging(setup.Target);
    }

    [Fact]
    public void TraversalInDirectoryImageIsRejectedBeforeCreatingArtifacts()
    {
        if (!OperatingSystem.IsLinux()) return;
        var setup = SetUp();
        using var replacement = Capture(setup.Replacement);
        var bytes = replacement.Content.Bytes.ToArray();
        "../test"u8.CopyTo(bytes.AsSpan(20)); // Replaces the first canonical filename, CURRENT.
        using var unsafeImage = new RestoreFileImage(true, bytes, replacement.Metadata.Bytes.Span);

        Assert.Throws<InvalidDataException>(() => Open(setup, unsafeImage));
        Assert.Empty(Directory.GetFiles(setup.Recovery));
        Assert.Equal("original", File.ReadAllText(Path.Combine(setup.Target, "CURRENT")));
        AssertNoStaging(setup.Target);
    }

    [SupportedOSPlatform("linux")]
    private TestScope SetUp(bool initiallyEmpty = false)
    {
        var browser = new BraveAppInfo(BraveAppChannel.Stable, "1.96.61", "synthetic", Path.Combine(_root, "browser"));
        var profilePath = Path.Combine(browser.UserDataDirectory, "Default");
        var profile = new BraveProfileInfo("Synthetic", profilePath, Path.Combine(profilePath, "Bookmarks"));
        var local = Path.Combine(profilePath, "Local Extension Settings");
        var target = Directory.CreateDirectory(Path.Combine(local, ExtensionId)).FullName;
        var unrelated = Path.Combine(Directory.CreateDirectory(Path.Combine(local, OtherId)).FullName, "CURRENT");
        File.WriteAllText(unrelated, "unrelated");
        File.WriteAllText(profile.BookmarksPath, "original-bookmarks");
        if (!initiallyEmpty)
        {
            File.WriteAllText(Path.Combine(target, "CURRENT"), "original");
            var oldFile = Path.Combine(target, "old-only.ldb");
            File.WriteAllBytes(oldFile, [1, 0, 255]);
            File.SetUnixFileMode(oldFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(oldFile, OriginalTime);
        }
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.SetLastWriteTimeUtc(target, OriginalTime);
        var replacement = Directory.CreateDirectory(Path.Combine(_root, "replacement")).FullName;
        File.WriteAllText(Path.Combine(replacement, "CURRENT"), "archived");
        File.WriteAllBytes(Path.Combine(replacement, "new-only.ldb"), [4, 0, 187]);
        var source = Path.Combine(_root, "source.bravebackup");
        File.WriteAllText(source, "synthetic source");
        var recovery = Directory.CreateDirectory(Path.Combine(_root, "recovery")).FullName;
        return new(browser, profile, target, unrelated, replacement, source, recovery);
    }

    private static LinuxRestoreSession Open(TestScope setup, RestoreFileImage replacement, RestoreFileImage? bookmarks = null)
    {
        var installation = Guid.NewGuid();
        var profile = Guid.NewGuid();
        var directory = new LinuxRestoreDestination(new(Guid.NewGuid(), installation, profile, "extensions", replacement),
            setup.App, setup.Profile, ExtensionId) { IsLocalExtensionDirectory = true };
        var targets = new List<LinuxRestoreDestination> { directory };
        if (bookmarks is not null)
            targets.Add(new(new(Guid.NewGuid(), installation, profile, "bookmarks", bookmarks), setup.App, setup.Profile, "Bookmarks"));
        return LinuxRestoreSession.Open(setup.Source, setup.Recovery, targets, SafetyPassword,
            [setup.App.UserDataDirectory], () => setup.Presence);
    }

    private static RestoreFileImage Capture(string path)
    {
        using var directory = RecoveryDirectoryIdentity.Open(path);
        return LinuxFlatDirectoryImage.Capture(directory);
    }

    private static RestoreFileImage CaptureBookmarkFile(string path)
    {
        using var original = LinuxRestoreFileCapture.Read(path, "Bookmarks");
        return new RestoreFileImage(true, "replacement-bookmarks"u8, original.Metadata.Bytes.Span);
    }

    private static void AssertImage(string path, RestoreFileImage expected)
    {
        using var actual = Capture(path);
        Assert.True(LinuxFlatDirectoryImage.Matches(expected, actual));
    }

    private static void AssertNoStaging(string target) =>
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(target)!, ".restore-directory-*"));

    private sealed record TestScope(BraveAppInfo App, BraveProfileInfo Profile, string Target, string Unrelated,
        string Replacement, string Source, string Recovery)
    {
        public ProcessPresence Presence { get; set; } = ProcessPresence.Stopped;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
