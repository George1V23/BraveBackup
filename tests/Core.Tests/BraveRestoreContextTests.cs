using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class BraveRestoreContextTests
{
    [Fact]
    public void FrozenContextBlocksSourceEditsAndRunningOrUnknownBrowserWithoutWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "RestoreContext_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "browser"); var profile = Path.Combine(data, "Default"); Directory.CreateDirectory(profile);
        try
        {
            var source = Path.Combine(root, "source.bravebackup"); File.WriteAllText(source, "synthetic-source");
            var state = ProcessPresence.Stopped;
            (BraveAppInfo, BraveProfileInfo)[] targets = [(new(BraveAppChannel.Stable, "synthetic", "synthetic", data), new("Synthetic", profile, "unused"))];
            var context = BraveRestoreContext.Capture(source, targets, () => state); context.Verify();
            state = ProcessPresence.Running; Assert.Throws<IOException>(() => context.Verify());
            state = ProcessPresence.Unknown; Assert.Throws<IOException>(() => context.Verify());
            state = ProcessPresence.Stopped; File.WriteAllText(source, "changed"); Assert.Throws<IOException>(() => context.Verify());
            Assert.Empty(Directory.GetFiles(data, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void QuickVerificationDetectsOrdinaryEditsButOnlyFullVerificationDetectsTimePreservingRewrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "RestoreContextQuick_" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "browser"); var profile = Path.Combine(data, "Default"); Directory.CreateDirectory(profile);
        try
        {
            var source = Path.Combine(root, "source.bravebackup"); File.WriteAllText(source, "synthetic-source");
            var written = File.GetLastWriteTimeUtc(source);
            (BraveAppInfo, BraveProfileInfo)[] targets = [(new(BraveAppChannel.Stable, "synthetic", "synthetic", data), new("Synthetic", profile, "unused"))];
            var context = BraveRestoreContext.Capture(source, targets, () => ProcessPresence.Stopped);
            context.VerifyQuick();
            File.WriteAllText(source, "SYNTHETIC-SOURCE"); File.SetLastWriteTimeUtc(source, written);
            context.VerifyQuick();                                   // same size and time: not detected cheaply
            Assert.Throws<IOException>(() => context.Verify());      // full hash detects it
            File.WriteAllText(source, "synthetic-source"); File.SetLastWriteTimeUtc(source, written); context.Verify();
            File.WriteAllText(source, "longer-synthetic-source");
            Assert.Throws<IOException>(() => context.VerifyQuick());
            File.WriteAllText(source, "synthetic-source"); File.SetLastWriteTimeUtc(source, written.AddMinutes(1));
            Assert.Throws<IOException>(() => context.VerifyQuick());
        }
        finally { Directory.Delete(root, true); }
    }
}
