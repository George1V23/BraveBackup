using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Frozen, operation-scoped recovery paths outside browser/source data. No constructor writes occur.</summary>
public sealed class RestoreArtifactScope
{
    private readonly string[] _protectedRoots;
    private readonly string? _source;
    public string SafetyPath { get; }
    public string JournalPath { get; }
    public string RestartRecordPath { get; }
    public RestoreArtifactScope(Guid operationId, string directory, string sourceArchive, IEnumerable<string> protectedDirectories)
        : this(operationId, directory, protectedDirectories, Path.GetFullPath(sourceArchive))
    {
    }

    internal static RestoreArtifactScope ForRestart(Guid operationId, string directory, IEnumerable<string> protectedDirectories) =>
        new(operationId, directory, protectedDirectories, null);

    private RestoreArtifactScope(Guid operationId, string directory, IEnumerable<string> protectedDirectories, string? source)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A recovery operation identity is required.");
        _protectedRoots = protectedDirectories.Select(Path.GetFullPath).ToArray();
        _source = source;
        var folder = Path.GetFullPath(directory);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException("Choose an existing recovery folder.");
        SafetyPath = Path.Combine(folder, operationId.ToString("N") + ".safety.encrypted");
        JournalPath = Path.Combine(folder, operationId.ToString("N") + ".journal.encrypted");
        RestartRecordPath = Path.Combine(folder, operationId.ToString("N") + ".restart.encrypted");
        Validate();
    }
    /// <summary>Rechecks fixed output paths against frozen browser roots, source collisions and filesystem links.</summary>
    public void Validate()
    {
        foreach (var path in new[] { SafetyPath, JournalPath, RestartRecordPath })
        {
            ExportDestinationGuard.Validate(path, _protectedRoots);
            if (string.Equals(path, _source, HostEnvironment.PathComparison))
                throw new InvalidOperationException("Recovery artifacts cannot replace the source archive.");
        }
    }
    /// <summary>Creates the safety artifact once; existing safety state is never refreshed during resume.</summary>
    public void SaveSafety(ReadOnlyMemory<byte> encrypted) { Validate(); RestoreArtifactStorage.Publish(SafetyPath, encrypted); }
    /// <summary>Reads the fixed safety artifact for authenticated coordinator verification.</summary>
    public byte[] ReadSafety() { Validate(); return RestoreArtifactStorage.Read(SafetyPath); }
    /// <summary>Publishes a journal revision at its fixed authorized path.</summary>
    public void SaveJournal(ReadOnlyMemory<byte> encrypted)
    {
        Validate(); RestoreArtifactStorage.Publish(JournalPath, encrypted, File.Exists(JournalPath));
    }
    /// <summary>Reads the fixed journal path for authenticated coordinator verification.</summary>
    public byte[] ReadJournal() { Validate(); return RestoreArtifactStorage.Read(JournalPath); }
}
