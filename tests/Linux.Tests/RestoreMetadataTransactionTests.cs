using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Integrates native metadata, encrypted artifact ownership and rollback on synthetic files only.</summary>
public sealed class RestoreMetadataTransactionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackRecoversBytesPermissionsTimeAndOriginalAbsence(bool removeSourceWhilePaused)
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "MetadataTransaction_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var data = Path.Combine(root, "browser");
            var targetDirectory = Path.Combine(data, "Default");
            Directory.CreateDirectory(targetDirectory);
            var target = Path.Combine(targetDirectory, "target");
            File.WriteAllText(target, "synthetic-original");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(target, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            byte[] originalMetadata;
            using (var file = File.OpenRead(target)) originalMetadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle);
            var recovery = Path.Combine(root, "recovery"); Directory.CreateDirectory(recovery);
            var source = Path.Combine(root, "source.bravebackup");
            File.WriteAllText(source, "synthetic-source");
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var profile = Guid.NewGuid();
            var installation = Guid.NewGuid();
            var app = new BraveAppInfo(BraveAppChannel.Stable, "synthetic", "synthetic", data);
            var destination = new BraveProfileInfo("Synthetic", targetDirectory, target);
            var absent = Path.Combine(targetDirectory, "originally-absent");
            using var replacement = new RestoreFileImage(true, "synthetic-new"u8, originalMetadata);
            var presence = ProcessPresence.Stopped;
            using var session = LinuxRestoreSession.Open(source, recovery, [
                new(new(first, installation, profile, "bookmarks", replacement), app, destination, Path.GetFileName(target)),
                new(new(second, installation, profile, "bookmarks", replacement), app, destination, Path.GetFileName(absent))],
                "synthetic-password", [], () => presence);
            var transaction = session.Transaction;
            var scope = session.Artifacts;
            Assert.Empty(Directory.GetFiles(recovery));
            Assert.Contains(transaction.OperationId.ToString("N"), Path.GetFileName(scope.SafetyPath));

            transaction.Prepare();
            var safety = File.ReadAllBytes(scope.SafetyPath);
            if (removeSourceWhilePaused)
                transaction.Progress = progress =>
                {
                    if (progress.State == RestoreTransactionState.Running) transaction.RequestPause();
                };
            transaction.Run();
            Assert.Equal("synthetic-new", File.ReadAllText(target));
            if (removeSourceWhilePaused)
            {
                Assert.Equal(RestoreTransactionState.Paused, transaction.State);
                Assert.False(File.Exists(absent));
                File.Delete(source);
                Assert.ThrowsAny<IOException>(() => transaction.Run());

                presence = ProcessPresence.Running;
                Assert.Throws<IOException>(() => transaction.Rollback(true));
                Assert.Equal(RestoreTransactionState.RecoveryIncomplete, transaction.State);
                Assert.Equal("synthetic-new", File.ReadAllText(target));
                presence = ProcessPresence.Stopped;
            }
            else
            {
                Assert.Equal(RestoreTransactionState.Completed, transaction.State);
                Assert.True(File.Exists(absent));
            }

            transaction.Rollback(true);
            Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
            Assert.Equal("synthetic-original", File.ReadAllText(target));
            Assert.False(File.Exists(absent));
            using var restored = File.OpenRead(target);
            Assert.Equal(originalMetadata, LinuxRecoveryMetadata.Capture(restored.SafeFileHandle));
            Assert.Equal(safety, File.ReadAllBytes(scope.SafetyPath));
            Assert.True(File.Exists(scope.SafetyPath)); Assert.True(File.Exists(scope.JournalPath));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void RejectsDuplicateTargetsAndRecoveryWithinBrowserDataBeforeWrites()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "RestoreSessionScope_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "browser");
        var profile = Path.Combine(data, "Default");
        Directory.CreateDirectory(profile);
        try
        {
            var source = Path.Combine(root, "source.bravebackup");
            File.WriteAllText(source, "synthetic-source");
            var app = new BraveAppInfo(BraveAppChannel.Stable, "synthetic", "synthetic", data);
            var destination = new BraveProfileInfo("Synthetic", profile, Path.Combine(profile, "Bookmarks"));
            using var image = new RestoreFileImage(false, []);
            var mutation = new RestoreMutation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "bookmarks", image);
            var mapping = new LinuxRestoreDestination(mutation, app, destination, "Bookmarks");
            var duplicate = mapping with { Mutation = mutation with { Id = Guid.NewGuid() } };

            Assert.Throws<InvalidDataException>(() => LinuxRestoreSession.Open(source, root,
                [mapping, duplicate], "synthetic-password", [], () => ProcessPresence.Stopped));
            Assert.Throws<InvalidOperationException>(() => LinuxRestoreSession.Open(source, data,
                [mapping], "synthetic-password", [], () => ProcessPresence.Stopped));
            Assert.Empty(Directory.GetFiles(data, "*", SearchOption.AllDirectories));
            Assert.Equal(source, Assert.Single(Directory.GetFiles(root)));
        }
        finally { Directory.Delete(root, true); }
    }
}
