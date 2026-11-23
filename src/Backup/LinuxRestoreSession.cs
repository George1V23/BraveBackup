using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>One explicitly mapped restore target in an existing profile.</summary>
internal sealed record LinuxRestoreDestination(
    RestoreMutation Mutation, BraveAppInfo Installation, BraveProfileInfo Profile, string FileName)
{
    /// <summary>Resolves the supplied extension ID under the profile's existing Local Extension Settings directory.</summary>
    internal bool IsLocalExtensionDirectory { get; init; }
    internal string? RequiredOriginalDigest { get; init; }
    internal Action<CancellationToken>? VerifyApplyContext { get; init; }
    internal Action<CancellationToken>? VerifyRecoveryTarget { get; init; }
}

/// <summary>Bound target adapter for an atomic file or whole-directory mutation.</summary>
internal interface ILinuxRestoreTarget : IDisposable
{
    long ImageBytes { get; }
    RestoreFileImage ReadCurrent(CancellationToken token);
    void Publish(RestoreFileImage image, Action verifyContext);
}

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
        CancellationToken token = default) =>
        Open(Guid.NewGuid(), sourceArchive, recoveryDirectory, destinations, safetyPassword, protectedDirectories, observe, token);

    /// <summary>Opens a session using the operation ID already shown in the review.</summary>
    internal static LinuxRestoreSession Open(Guid operation, string sourceArchive, string recoveryDirectory,
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
                throw new ArgumentException("A single restore target name is required.");
            if (target.IsLocalExtensionDirectory &&
                (target.Mutation.Category is not ("extensions" or "tabmanagerplus") || !BraveExtensionBackup.ValidId(name)))
                throw new InvalidDataException("An extension directory restore requires the mapped extension identity.");
            if (target.Mutation.Category == "tabmanagerplus" &&
                (!target.IsLocalExtensionDirectory || name != BraveTabManagerPlusRestore.ExtensionId))
                throw new InvalidDataException("Tab Manager Plus restore requires its registered Local store.");
            if (target.Mutation.Category == "passwords" &&
                (target.IsLocalExtensionDirectory || name != BravePasswordRestoreTarget.FileName ||
                 target.VerifyApplyContext is null || target.VerifyRecoveryTarget is null))
                throw new InvalidDataException("Password restore requires a verified local profile store.");
            if (target.Mutation.Category == "sessions" && !BraveSessionBackup.ModernName(name, sessionOnly: true))
                throw new InvalidDataException("A native Session restore requires a modern Session filename.");
            if (!paths.Add(Path.Combine(TargetParent(target), name)) || !ids.Add(target.Mutation.Id))
                throw new InvalidDataException("Restore target mappings must be unique.");
        }

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
                ILinuxRestoreTarget checkpoint = target.IsLocalExtensionDirectory
                    ? new LinuxRestoreDirectoryCheckpoint(TargetParent(target), target.FileName, target.Mutation.Replacement, token, target.RequiredOriginalDigest)
                    : new LinuxRestoreExpectedState(TargetParent(target), target.FileName, token);
                store.Targets.Add(target.Mutation.Id, checkpoint);
                if (target.VerifyApplyContext is { } verifyApply)
                    store.ApplyGuards.Add(target.Mutation.Id, verifyApply);
                if (target.VerifyRecoveryTarget is { } verifyRecovery)
                    store.RecoveryGuards.Add(target.Mutation.Id, verifyRecovery);
                retained += checkpoint.ImageBytes;
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

    private static string TargetParent(LinuxRestoreDestination target) => target.Mutation.Category switch
    {
        "extensions" or "tabmanagerplus" when target.IsLocalExtensionDirectory =>
            Path.Combine(target.Profile.ProfileDirectory, BraveExtensionLocalRestore.ParentName),
        "sessions" => Path.Combine(target.Profile.ProfileDirectory, BraveNativeSessionRestore.ParentName),
        _ => target.Profile.ProfileDirectory
    };

    /// <summary>Requires the transaction to use the same source bytes as its category planner.</summary>
    internal void VerifySource(BraveRestoreContext expected) => _store.VerifySource(expected);

    /// <summary>Revalidates frozen category-specific destination checks before resuming a paused forward transaction.</summary>
    internal void VerifyApplyTargets(CancellationToken token)
    {
        if (_store.ApplyGuards.Count == 0) return;
        _store.VerifyBrowserStopped(token);
        foreach (var check in _store.ApplyGuards.Values) check(token);
        _store.VerifyBrowserStopped(token);
    }

    /// <summary>Releases the coordinator and retained handles while preserving encrypted recovery files.</summary>
    public void Dispose()
    {
        Transaction.Dispose();
        _store.Dispose();
    }

    private sealed class Store(BraveRestoreContext context, RestoreArtifactLease artifacts) : IRestoreTransactionStore, IDisposable
    {
        public Dictionary<Guid, ILinuxRestoreTarget> Targets { get; } = [];
        internal Dictionary<Guid, Action<CancellationToken>> ApplyGuards { get; } = [];
        internal Dictionary<Guid, Action<CancellationToken>> RecoveryGuards { get; } = [];

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

        internal void VerifyBrowserStopped(CancellationToken token) => context.VerifyBrowserStopped(token);

        public RestoreFileImage ReadTarget(Guid mutationId, CancellationToken token) => Targets[mutationId].ReadCurrent(token);

        public void WriteTargetAtomic(Guid mutationId, RestoreFileImage image)
        {
            var verify = ApplyGuards.GetValueOrDefault(mutationId);
            verify?.Invoke(CancellationToken.None);
            Targets[mutationId].Publish(image, () =>
            {
                VerifyContext(CancellationToken.None);
                if (verify is not null)
                {
                    verify(CancellationToken.None);
                    context.VerifyBrowserStopped(CancellationToken.None);
                }
            });
        }

        public void WriteRecoveryTargetAtomic(Guid mutationId, RestoreFileImage image)
        {
            var verify = RecoveryGuards.GetValueOrDefault(mutationId);
            verify?.Invoke(CancellationToken.None);
            Targets[mutationId].Publish(image, () =>
            {
                VerifyRecoveryContext(CancellationToken.None);
                verify?.Invoke(CancellationToken.None);
                if (verify is not null) context.VerifyBrowserStopped(CancellationToken.None);
            });
        }

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
