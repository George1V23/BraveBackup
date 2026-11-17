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
            var store = new Store(lease, new() { [first] = target, [second] = absent });
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

    // Test-only adapter: path races, directory metadata and production permission policy are intentionally not implemented.
    private sealed class Store(RestoreArtifactLease artifacts, Dictionary<Guid, string> paths) : IRestoreTransactionStore
    {
        public void VerifyContext(CancellationToken token) { token.ThrowIfCancellationRequested(); artifacts.Verify(); }
        public RestoreFileImage ReadTarget(Guid id, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(paths[id])) return new(false, []);
            using var file = File.OpenRead(paths[id]);
            var metadata = LinuxRecoveryMetadata.Capture(file.SafeFileHandle);
            using var bytes = new MemoryStream(); file.CopyTo(bytes);
            return new(true, bytes.ToArray(), metadata);
        }
        public void WriteTargetAtomic(Guid id, RestoreFileImage image)
        {
            if (!image.Exists) { File.Delete(paths[id]); return; }
            var temp = paths[id] + ".tmp";
            try
            {
                using (var file = BackupFileOperations.CreatePrivateFile(temp))
                {
                    file.Write(image.Content.Bytes.Span); file.Flush(true);
                    LinuxRecoveryMetadata.Apply(file.SafeFileHandle, image.Metadata.Bytes.Span); file.Flush(true);
                }
                File.Move(temp, paths[id], true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        public void SaveSafety(ReadOnlyMemory<byte> bytes) => artifacts.SaveSafety(bytes);
        public byte[] ReadSafety() => artifacts.ReadSafety();
        public void SaveJournal(ReadOnlyMemory<byte> bytes) => artifacts.SaveJournal(bytes);
        public byte[] ReadJournal() => artifacts.ReadJournal();
    }
}
