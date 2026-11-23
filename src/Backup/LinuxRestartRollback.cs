using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Read-only result for one exact destination in a persisted rollback operation.</summary>
internal sealed record RestartRollbackTargetReview(string Path, string Category, RestoreRollbackDecision Decision);

/// <summary>Frozen confirmation data. Artifact fingerprints and observed decisions are rechecked before execution.</summary>
internal sealed record RestartRollbackReview(Guid Operation, long Revision, string ArtifactFingerprint,
    IReadOnlyList<RestartRollbackTargetReview> Targets);

/// <summary>Reopens an explicitly selected same-boot Linux operation for rollback without its source archive.</summary>
internal sealed class LinuxRestartRollback : IDisposable
{
    private readonly RestoreArtifactLease _lease;
    private readonly VerifiedRestoreRestartArtifacts _verified;
    private readonly string _safetyHash;
    private readonly Func<ProcessPresence> _observe;
    private string? _password;
    private readonly Target[] _targets;
    private readonly (Guid Id, RestoreEntryState State)[] _entries;
    private long _revision;

    private sealed record Target(string Parent, RestoreRestartTarget Mapping, RestoreSafetyEntry Original,
        ILinuxRestoreTarget Checkpoint);

    internal RestartRollbackReview Review { get; }

    internal (RestartRollbackTargetReview Target, bool Resolved)[] TargetResults() =>
        Review.Targets.Select((target, index) => (target,
            target.Decision != RestoreRollbackDecision.Conflict &&
            (target.Decision == RestoreRollbackDecision.Skip || _entries[index].State == RestoreEntryState.Recovered))).ToArray();

    private LinuxRestartRollback(RestoreArtifactLease lease, VerifiedRestoreRestartArtifacts verified, string safetyHash,
        Func<ProcessPresence> observe, string password, Target[] targets, RestartRollbackReview review)
    {
        _lease = lease;
        _verified = verified;
        _safetyHash = safetyHash;
        _observe = observe;
        _password = password;
        _targets = targets;
        Review = review;
        _revision = review.Revision;
        _entries = verified.Inspection.Journal.Entries.Select(entry => (entry.MutationId, entry.State)).ToArray();
    }

    /// <summary>Authenticates the sibling artifacts and reads only explicitly recognized, still-verified destinations.</summary>
    internal static LinuxRestartRollback Open(string selectedRecord, string password,
        IReadOnlyList<(BraveAppInfo Installation, BraveProfileInfo Profile)> discovered,
        Func<ProcessPresence>? observe = null, CancellationToken token = default)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        const string suffix = ".restart.encrypted";
        var path = Path.GetFullPath(selectedRecord);
        var name = Path.GetFileName(path);
        if (!name.EndsWith(suffix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[..^suffix.Length], "N", out var operation) || operation == Guid.Empty ||
            name != operation.ToString("N") + suffix)
            throw new InvalidDataException("Choose an operation's encrypted restart record.");

        ProcessPresence Observe() => observe?.Invoke() ?? ProcessPresenceProbe.Observe(["brave"], token);
        RequireStopped(Observe);
        var scope = RestoreArtifactScope.ForRestart(operation, Path.GetDirectoryName(path)!,
            discovered.Select(destination => destination.Installation.UserDataDirectory).Distinct(StringComparer.Ordinal));
        var lease = RestoreArtifactLease.Acquire(scope);
        VerifiedRestoreRestartArtifacts? verified = null;
        var targets = new List<Target>();
        try
        {
            var recordBytes = lease.ReadRestartRecord();
            var safetyBytes = lease.ReadSafety();
            var journalBytes = lease.ReadJournal();
            using (var record = new MemoryStream(recordBytes, writable: false))
            using (var safety = new MemoryStream(safetyBytes, writable: false))
            using (var journal = new MemoryStream(journalBytes, writable: false))
                verified = RestoreRestartRecordFormat.Open(record, safety, journal, password, token);
            lease.AdoptVerifiedArtifacts(recordBytes, journalBytes);
            var inspection = verified.Inspection;
            if (inspection.Record.OperationId != operation || inspection.Journal.Revision is < 1 or > long.MaxValue - 20_002 ||
                inspection.Journal.State is RestoreTransactionState.New)
                throw new InvalidDataException("The selected files cannot identify a recoverable operation.");

            var seen = new List<(RecoveryDirectoryIdentity Directory, string Name)>();
            var reviews = new List<RestartRollbackTargetReview>();
            long retained = 0;
            try
            {
                for (var index = 0; index < inspection.Record.Targets.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    RequireStopped(Observe);
                    var target = inspection.Record.Targets[index];
                    var original = verified.Originals.Entries[index];
                    var (parent, child) = ResolveTarget(target, original.Category, scope.SafetyPath, discovered, token);
                    var identity = RecoveryDirectoryIdentity.Open(parent);
                    try
                    {
                        identity.VerifyRestartEvidence(target.DirectoryEvidence);
                        if (seen.Any(previous => previous.Name == child && previous.Directory.RefersToSameDirectory(identity)))
                            throw new InvalidDataException("Multiple recovery entries refer to the same destination.");
                        seen.Add((identity, child));
                    }
                    catch { identity.Dispose(); throw; }

                    ILinuxRestoreTarget checkpoint = target.Kind == RestoreRestartTargetKind.FlatDirectory
                        ? new LinuxRestoreDirectoryCheckpoint(parent, child, original.Original, token)
                        : new LinuxRestoreExpectedState(parent, child, token);
                    var retainedTarget = new Target(parent, target, original, checkpoint);
                    targets.Add(retainedTarget);
                    retained += checkpoint.ImageBytes;
                    if (retained > RestoreSafetyFormat.MaximumBytes)
                        throw new InvalidDataException("Recovery targets exceed the retention limit.");
                    if (checkpoint.CaptureRestartDirectoryEvidence() != target.DirectoryEvidence)
                        throw new IOException("The destination directory changed during review.");
                    reviews.Add(new(Path.Combine(parent, child), original.Category,
                        ObserveDecision(retainedTarget, inspection.Journal.Entries[index].State, token)));
                }
            }
            finally { foreach (var entry in seen) entry.Directory.Dispose(); }

            RequireStopped(Observe);
            var safetyHash = Hash(safetyBytes);
            var artifacts = Hash(recordBytes) + safetyHash + Hash(journalBytes);
            return new(lease, verified, safetyHash, observe ?? (() => ProcessPresenceProbe.Observe(["brave"])),
                password, targets.ToArray(),
                new(operation, inspection.Journal.Revision, artifacts, reviews.AsReadOnly()));
        }
        catch
        {
            foreach (var target in targets) target.Checkpoint.Dispose();
            verified?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    /// <summary>Writes only the original images authorized by an unchanged review and records partial recovery.</summary>
    internal RestoreTransactionState Rollback(RestartRollbackReview confirmed)
    {
        ObjectDisposedException.ThrowIf(_password is null, this);
        if (confirmed.Operation != Review.Operation || confirmed.ArtifactFingerprint != Review.ArtifactFingerprint ||
            !confirmed.Targets.SequenceEqual(Review.Targets))
            throw new IOException("Recovery artifacts or targets changed after review.");
        VerifyArtifacts();
        for (var index = 0; index < _targets.Length; index++)
        {
            VerifyTarget(_targets[index], checkCategory: false);
            if (ObserveDecision(_targets[index], _entries[index].State, CancellationToken.None) != Review.Targets[index].Decision)
                throw new IOException("A recovery target changed after review.");
        }

        if (!Review.Targets.Any(target => target.Decision is RestoreRollbackDecision.AlreadyOriginal or RestoreRollbackDecision.RestoreOriginal))
            return Review.Targets.Any(target => target.Decision == RestoreRollbackDecision.Conflict)
                ? RestoreTransactionState.RecoveryIncomplete : RestoreTransactionState.RolledBack;

        var incomplete = false;
        WriteJournal(RestoreTransactionState.RollingBack);
        for (var index = _targets.Length - 1; index >= 0; index--)
        {
            var decision = Review.Targets[index].Decision;
            if (decision == RestoreRollbackDecision.Skip) continue;
            if (decision == RestoreRollbackDecision.Conflict)
            {
                incomplete = true;
                continue;
            }
            var target = _targets[index];
            try
            {
                VerifyTarget(target);
                using (var observed = target.Checkpoint.ReadCurrent(CancellationToken.None))
                {
                    if (RestoreRecoveryEvidence.AssessRollback(_entries[index].State, observed,
                            target.Mapping.OriginalFingerprint, target.Mapping.ReplacementFingerprint) != decision)
                        throw new IOException("Recovery target changed after review.");
                }
                if (decision == RestoreRollbackDecision.RestoreOriginal)
                {
                    WriteJournal(RestoreTransactionState.RollingBack);
                    target.Checkpoint.Publish(target.Original.Original, () =>
                    {
                        VerifyArtifacts();
                        VerifyTarget(target);
                    });
                    using var restored = target.Checkpoint.ReadCurrent(CancellationToken.None);
                    if (RestoreRecoveryEvidence.Fingerprint(restored) != target.Mapping.OriginalFingerprint)
                        throw new IOException("Rollback publication could not be verified.");
                }
                _entries[index].State = RestoreEntryState.Recovered;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                incomplete = true;
                continue;
            }
            WriteJournal(RestoreTransactionState.RollingBack);
        }
        var status = incomplete ? RestoreTransactionState.RecoveryIncomplete : RestoreTransactionState.RolledBack;
        WriteJournal(status);
        return status;
    }

    private static (string Parent, string Child) ResolveTarget(RestoreRestartTarget target, string category,
        string artifactPath, IReadOnlyList<(BraveAppInfo Installation, BraveProfileInfo Profile)> discovered, CancellationToken token)
    {
        var matches = discovered.Where(candidate =>
            Path.GetFullPath(candidate.Installation.UserDataDirectory) == target.InstallationRoot &&
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Profile.ProfileDirectory)) == target.ProfileDirectory).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("The recorded profile no longer matches a detected Brave profile.");
        var profile = BraveRestoreTargetGuard.Validate(matches[0].Installation, matches[0].Profile, artifactPath, token);
        var parts = target.RelativeTarget.Split('/');
        var isSupported = (target.Kind, category) switch
        {
            (RestoreRestartTargetKind.File, "bookmarks") => parts is ["Bookmarks"],
            (RestoreRestartTargetKind.File, "settings") => parts is [BraveSettingsRestore.FileName],
            (RestoreRestartTargetKind.File, "passwords") => parts is [BravePasswordRestoreTarget.FileName],
            (RestoreRestartTargetKind.File, "sessions") => parts is [BraveNativeSessionRestore.ParentName, var name] &&
                BraveSessionBackup.ModernName(name, sessionOnly: true),
            (RestoreRestartTargetKind.FlatDirectory, "extensions") =>
                parts is [BraveExtensionLocalRestore.ParentName, var extension] && BraveExtensionBackup.ValidId(extension),
            (RestoreRestartTargetKind.FlatDirectory, "tabmanagerplus") =>
                parts is [BraveExtensionLocalRestore.ParentName, BraveTabManagerPlusRestore.ExtensionId],
            _ => false
        };
        if (!isSupported) throw new InvalidDataException("A recorded path is outside the supported recovery targets.");
        return (parts.Length == 1 ? profile : Path.Combine(profile, parts[0]), parts[^1]);
    }

    private static RestoreRollbackDecision ObserveDecision(Target target, RestoreEntryState state, CancellationToken token)
    {
        try
        {
            CheckCategory(target.Parent, target.Original.Category, token);
            using var image = target.Checkpoint.ReadCurrent(token);
            return RestoreRecoveryEvidence.AssessRollback(state, image, target.Mapping.OriginalFingerprint,
                target.Mapping.ReplacementFingerprint);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return RestoreRollbackDecision.Conflict;
        }
    }

    private void VerifyTarget(Target target, bool checkCategory = true)
    {
        RequireStopped(_observe);
        using var directory = RecoveryDirectoryIdentity.Open(target.Parent);
        directory.VerifyRestartEvidence(target.Mapping.DirectoryEvidence);
        if (target.Checkpoint.CaptureRestartDirectoryEvidence() != target.Mapping.DirectoryEvidence)
            throw new IOException("The retained destination directory changed.");
        if (checkCategory) CheckCategory(target.Parent, target.Original.Category, CancellationToken.None);
        RequireStopped(_observe);
    }

    private static void CheckCategory(string parent, string category, CancellationToken token)
    {
        if (category == "passwords") BravePasswordRestoreTarget.VerifySidecars(parent, token);
    }

    private void VerifyArtifacts()
    {
        RequireStopped(_observe);
        _lease.VerifyRestartRecord();
        _lease.VerifyJournal();
        if (Hash(_lease.ReadSafety()) != _safetyHash)
            throw new IOException("The verified safety backup changed.");
    }

    private void WriteJournal(RestoreTransactionState state)
    {
        VerifyArtifacts();
        using var plaintext = RestoreRestartRecordFormat.EncodeVerifiedJournal(_verified, ++_revision, state, _entries);
        using var stream = new MemoryStream();
        using (var writer = ArchiveEncryption.CreateWriter(stream, _password!))
            writer.Write(plaintext.Bytes.Span);
        _lease.SaveJournal(stream.ToArray());

        using var encrypted = new MemoryStream(_lease.ReadJournal(), writable: false);
        using var reader = ArchiveEncryption.OpenReader(encrypted, _password!);
        if (reader.Length != plaintext.Bytes.Length)
            throw new InvalidDataException("Recovery journal verification failed.");
        var bytes = new byte[(int)reader.Length];
        try
        {
            reader.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(plaintext.Bytes.Span))
                throw new InvalidDataException("Recovery journal verification failed.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireStopped(Func<ProcessPresence> observe)
    {
        if (observe() != ProcessPresence.Stopped) throw new IOException("Close Brave before reviewing or rolling back.");
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        _password = null;
        foreach (var target in _targets) target.Checkpoint.Dispose();
        _verified.Dispose();
        _lease.Dispose();
    }
}
