using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>One explicitly mapped file mutation and its destination profile.</summary>
internal sealed record LinuxRestoreDestination(
    RestoreMutation Mutation, BraveAppInfo Installation, BraveProfileInfo Profile, string FileName);

/// <summary>Owns the coordinator, frozen target checkpoints and encrypted artifacts for a Linux restore.</summary>
/// <remarks>Internal integration only. The caller must validate the source archive and authorize the frozen plan.</remarks>
internal sealed class LinuxRestoreSession : IDisposable
{
    private readonly Store _store;
    internal RestoreTransaction Transaction { get; }
    internal RestoreArtifactScope Artifacts { get; }

    private LinuxRestoreSession(Store store, RestoreTransaction transaction, RestoreArtifactScope artifacts)
    {
        _store = store;
        Transaction = transaction;
        Artifacts = artifacts;
    }

    /// <summary>Freezes mappings and captures target baselines without publishing any files.</summary>
    internal static LinuxRestoreSession Open(string sourceArchive, string recoveryDirectory,
        IReadOnlyList<LinuxRestoreDestination> destinations, string safetyPassword,
        IEnumerable<string> protectedDirectories, Func<ProcessPresence>? observe = null,
        CancellationToken token = default)
    {
        if (!HostEnvironment.IsLinux) throw new PlatformNotSupportedException("Linux restore storage is required.");
        token.ThrowIfCancellationRequested();
        if (destinations.Count is < 1 or > 10_000)
            throw new InvalidDataException("Restore requires between 1 and 10,000 file mutations.");
        var frozen = destinations.Select(destination => destination with
        {
            Installation = destination.Installation with
            {
                UserDataDirectory = Path.GetFullPath(destination.Installation.UserDataDirectory)
            },
            Profile = destination.Profile with
            {
                ProfileDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Profile.ProfileDirectory))
            }
        }).ToArray();
        var profiles = frozen.Select(target => (target.Installation, target.Profile)).Distinct().ToArray();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        foreach (var target in frozen)
        {
            var name = target.FileName;
            if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0)
                throw new ArgumentException("A single restore target filename is required.");
            if (!paths.Add(Path.Combine(target.Profile.ProfileDirectory, name)) || !ids.Add(target.Mutation.Id))
                throw new InvalidDataException("Restore file mappings must be unique.");
        }

        var operation = Guid.NewGuid();
        var source = Path.GetFullPath(sourceArchive);
        var context = BraveRestoreContext.Capture(source, profiles, observe, token);
        var scope = new RestoreArtifactScope(operation, recoveryDirectory, source,
            protectedDirectories.Concat(profiles.Select(profile => profile.Installation.UserDataDirectory)));
        var store = new Store(context, RestoreArtifactLease.Acquire(scope));
        RestoreTransaction? transaction = null;
        try
        {
            transaction = new RestoreTransaction(frozen.Select(target => target.Mutation).ToArray(),
                store, safetyPassword, operation);
            long retained = 0;
            foreach (var target in frozen)
            {
                token.ThrowIfCancellationRequested();
                var checkpoint = new LinuxRestoreExpectedState(target.Profile.ProfileDirectory, target.FileName, token);
                store.Targets.Add(target.Mutation.Id, checkpoint);
                using var original = checkpoint.CopyOriginal();
                retained += original.Content.Bytes.Length + original.Metadata.Bytes.Length;
                if (retained > RestoreSafetyFormat.MaximumBytes)
                    throw new InvalidDataException("Restore originals exceed the supported retention limit.");
            }
            store.VerifyContext(token);
            return new LinuxRestoreSession(store, transaction, scope);
        }
        catch
        {
            transaction?.Dispose();
            store.Dispose();
            throw;
        }
    }

    /// <summary>Requires the transaction to use the same source bytes as its category planner.</summary>
    internal void VerifySource(BraveRestoreContext expected) => _store.VerifySource(expected);

    /// <summary>Releases the coordinator and retained handles while preserving encrypted recovery files.</summary>
    public void Dispose()
    {
        Transaction.Dispose();
        _store.Dispose();
    }

    private sealed class Store(BraveRestoreContext context, RestoreArtifactLease artifacts) : IRestoreTransactionStore, IDisposable
    {
        public Dictionary<Guid, LinuxRestoreExpectedState> Targets { get; } = [];

        internal void VerifySource(BraveRestoreContext expected) => context.VerifySameSource(expected);

        public void VerifyContext(CancellationToken token)
        {
            artifacts.Verify();
            context.Verify(token);
        }

        public void VerifyRecoveryContext(CancellationToken token)
        {
            artifacts.Verify();
            context.VerifyRecovery(token);
        }

        public RestoreFileImage ReadTarget(Guid mutationId, CancellationToken token) => Targets[mutationId].ReadCurrent(token);

        public void WriteTargetAtomic(Guid mutationId, RestoreFileImage image) =>
            Targets[mutationId].Publish(image, () => VerifyContext(CancellationToken.None));

        public void WriteRecoveryTargetAtomic(Guid mutationId, RestoreFileImage image) =>
            Targets[mutationId].Publish(image, () => VerifyRecoveryContext(CancellationToken.None));

        public void SaveSafety(ReadOnlyMemory<byte> encryptedBytes) => artifacts.SaveSafety(encryptedBytes);
        public byte[] ReadSafety() => artifacts.ReadSafety();
        public void SaveJournal(ReadOnlyMemory<byte> encryptedBytes) => artifacts.SaveJournal(encryptedBytes);
        public byte[] ReadJournal() => artifacts.ReadJournal();

        public void Dispose()
        {
            foreach (var target in Targets.Values) target.Dispose();
            artifacts.Dispose();
        }
    }
}
