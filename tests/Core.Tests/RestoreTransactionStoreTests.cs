using BraveBackup.Platform;
using Xunit;
namespace BraveBackup.Core.Tests;

/// <summary>Platform-neutral store behavior with fake targets and artifacts; no browser or recovery files are touched.</summary>
public sealed class RestoreTransactionStoreTests
{
    private sealed class FakeTarget : IRestoreTargetState
    {
        public bool Disposed; public int Writes;
        public RestoreFileImage ReadCurrent(CancellationToken token = default) => new(false, []);
        public void Publish(RestoreFileImage image, CancellationToken token = default) => Writes++;
        public void Dispose() => Disposed = true;
    }
    private sealed class FakeArtifacts : IRestoreArtifacts
    {
        public int Verified;
        public void Verify() => Verified++;
        public void SaveSafety(ReadOnlyMemory<byte> encrypted) { }
        public byte[] ReadSafety() => [];
        public void SaveJournal(ReadOnlyMemory<byte> encrypted) { }
        public byte[] ReadJournal() => [];
    }
    private static BraveRestoreContext Context(string root)
    {
        var data = Path.Combine(root, "browser"); var profile = Path.Combine(data, "Default"); Directory.CreateDirectory(profile);
        var source = Path.Combine(root, "source.bravebackup"); File.WriteAllText(source, "synthetic-source");
        (BraveAppInfo, BraveProfileInfo)[] targets = [(new(BraveAppChannel.Stable, "synthetic", "synthetic", data), new("Synthetic", profile, "unused"))];
        return BraveRestoreContext.Capture(source, targets, () => ProcessPresence.Stopped);
    }
    [Fact]
    public void RoutesByIdRejectsDuplicatesAndUnknownIdsAndDisposesTargets()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "StoreFake_" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var context = Context(root); var artifacts = new FakeArtifacts(); var opened = new List<FakeTarget>();
            IRestoreTargetState Open(string d, string n, CancellationToken t) { var target = new FakeTarget(); opened.Add(target); return target; }
            Guid a = Guid.NewGuid(), b = Guid.NewGuid();
            Assert.Throws<IOException>(() => new RestoreTransactionStore(context, artifacts,
                new Dictionary<Guid, (string, string)> { [a] = (root, "x"), [b] = (root, "x") }, Open));
            Assert.All(opened, target => Assert.True(target.Disposed));
            opened.Clear();
            var store = new RestoreTransactionStore(context, artifacts, new Dictionary<Guid, (string, string)> { [a] = (root, "x"), [b] = (root, "y") }, Open);
            using var image = new RestoreFileImage(false, []);
            store.WriteTargetAtomic(b, image);
            Assert.Equal(0, opened[0].Writes); Assert.Equal(1, opened[1].Writes);
            Assert.Throws<KeyNotFoundException>(() => store.WriteTargetAtomic(Guid.NewGuid(), image));
            store.VerifyContext(CancellationToken.None); Assert.Equal(1, artifacts.Verified);
            store.Dispose(); Assert.All(opened, target => Assert.True(target.Disposed));
            Assert.Throws<ObjectDisposedException>(() => store.ReadTarget(a, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
