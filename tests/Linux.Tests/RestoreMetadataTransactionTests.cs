using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Integrates native metadata, encrypted artifact ownership and rollback on synthetic files only.</summary>
public sealed class RestoreMetadataTransactionTests
{
    [Fact]
    public void RollbackRecoversBytesPermissionsTimeAndOriginalAbsence()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "MetadataTransaction_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target"); File.WriteAllText(target, "synthetic-original");
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetLastWriteTimeUtc(target, new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            byte[] originalMetadata;
            using (var file = File.OpenRead(target)) originalMetadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle);
            var recovery = Path.Combine(root, "recovery"); Directory.CreateDirectory(recovery);
            var scope = new RestoreArtifactScope(Guid.NewGuid(), recovery, Path.Combine(root, "source"), []);
            using var lease = RestoreArtifactLease.Acquire(scope);
            var first = Guid.NewGuid(); var second = Guid.NewGuid(); var profile = Guid.NewGuid(); var installation = Guid.NewGuid();
            var absent = Path.Combine(root, "originally-absent");
            using var replacement = new RestoreFileImage(true, "synthetic-new"u8, originalMetadata);
            using var existingState = new LinuxRestoreExpectedState(root, Path.GetFileName(target));
            using var missingState = new LinuxRestoreExpectedState(root, Path.GetFileName(absent));
            var store = new Store(lease, new() { [first] = existingState, [second] = missingState });
            using var transaction = new RestoreTransaction([
                new(first, installation, profile, "bookmarks", replacement),
                new(second, installation, profile, "bookmarks", replacement)], store, "synthetic-password");
            transaction.Prepare(); transaction.Run();
            Assert.Equal(RestoreTransactionState.Completed, transaction.State);
            Assert.Equal("synthetic-new", File.ReadAllText(target)); Assert.True(File.Exists(absent));
            transaction.Rollback(true); Assert.Equal(RestoreTransactionState.RolledBack, transaction.State);
            Assert.Equal("synthetic-original", File.ReadAllText(target)); Assert.False(File.Exists(absent));
            using var restored = File.OpenRead(target);
            Assert.Equal(originalMetadata, LinuxRecoveryMetadata.Capture(restored.SafeFileHandle));
            Assert.True(File.Exists(scope.SafetyPath)); Assert.True(File.Exists(scope.JournalPath));
        }
        finally { Directory.Delete(root, true); }
    }

    // Fixture-only mapping. Application wiring still needs to authorize profile destinations and check Brave state.
    private sealed class Store(RestoreArtifactLease artifacts, Dictionary<Guid, LinuxRestoreExpectedState> targets)
        : IRestoreTransactionStore
    {
        public void VerifyContext(CancellationToken token) { token.ThrowIfCancellationRequested(); artifacts.Verify(); }
        public RestoreFileImage ReadTarget(Guid id, CancellationToken token) => targets[id].ReadCurrent(token);
        public void WriteTargetAtomic(Guid id, RestoreFileImage image) =>
            targets[id].Publish(image, () => VerifyContext(CancellationToken.None));
        public void SaveSafety(ReadOnlyMemory<byte> bytes) => artifacts.SaveSafety(bytes);
        public byte[] ReadSafety() => artifacts.ReadSafety();
        public void SaveJournal(ReadOnlyMemory<byte> bytes) => artifacts.SaveJournal(bytes);
        public byte[] ReadJournal() => artifacts.ReadJournal();
    }
}
