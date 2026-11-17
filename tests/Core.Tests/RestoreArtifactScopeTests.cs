using Xunit;
namespace BraveBackup.Core.Tests;
public sealed class RestoreArtifactScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RecoveryScope_" + Guid.NewGuid().ToString("N"));
    public RestoreArtifactScopeTests() => Directory.CreateDirectory(_root);
    [Fact]
    public void ScopeIsWriteFreeAndRejectsBrowserRootsAndSourceCollision()
    {
        var id = Guid.NewGuid(); var source = Path.Combine(_root, "source.bravebackup");
        var scope = new RestoreArtifactScope(id, _root, source, []);
        Assert.Empty(Directory.GetFiles(_root)); Assert.NotEqual(scope.SafetyPath, scope.JournalPath);
        Assert.Throws<InvalidOperationException>(() => new RestoreArtifactScope(id, _root, source, [_root]));
        Assert.Throws<InvalidOperationException>(() => new RestoreArtifactScope(id, _root, scope.SafetyPath, []));
    }
    [Fact]
    public void SafetyCannotBeReplacedButJournalCanAdvance()
    {
        var scope = new RestoreArtifactScope(Guid.NewGuid(), _root, Path.Combine(_root, "source"), []);
        using var output = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(output, "synthetic-password")) writer.Write("fixture"u8);
        var bytes = output.ToArray(); scope.SaveSafety(bytes);
        Assert.Throws<IOException>(() => scope.SaveSafety(bytes)); Assert.Equal(bytes, scope.ReadSafety());
        scope.SaveJournal(bytes); scope.SaveJournal(bytes); Assert.Equal(bytes, scope.ReadJournal());
        Assert.Equal(2, Directory.GetFiles(_root).Length);
    }
    public void Dispose() => Directory.Delete(_root, true);
}
