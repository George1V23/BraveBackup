using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Read-only frozen source/target/process checks for a future explicit restore transaction adapter.</summary>
public sealed class BraveRestoreContext
{
    private readonly string _source;
    private readonly (BraveAppInfo Installation, BraveProfileInfo Profile)[] _targets;
    private readonly Func<ProcessPresence>? _observe;
    private readonly string _fingerprint;
    private BraveRestoreContext(string source, (BraveAppInfo, BraveProfileInfo)[] targets, Func<ProcessPresence>? observe, string fingerprint)
    { _source = source; _targets = targets; _observe = observe; _fingerprint = fingerprint; }
    /// <summary>Freezes authorized mappings and source bytes only after closed-browser/path validation; performs no writes.</summary>
    /// <remarks>Archive authentication and explicit final restore approval are separate prerequisites owned by the host.</remarks>
    public static BraveRestoreContext Capture(string sourceArchive,
        IReadOnlyList<(BraveAppInfo Installation, BraveProfileInfo Profile)> targets,
        Func<ProcessPresence>? observe = null, CancellationToken token = default)
    {
        var frozen = targets.Select(target => (
            Installation: target.Installation with { UserDataDirectory = Path.GetFullPath(target.Installation.UserDataDirectory) },
            Profile: target.Profile with { ProfileDirectory = Path.GetFullPath(target.Profile.ProfileDirectory) })).ToArray();
        if (frozen.Length == 0) throw new InvalidDataException("Select restore destinations first.");
        var source = Path.GetFullPath(sourceArchive);
        var context = new BraveRestoreContext(source, frozen, observe, Fingerprint(source, token));
        context.Verify(token); return context;
    }
    /// <summary>Rejects observed source changes, duplicate/unsafe target paths and running or unknown browser state.</summary>
    public void Verify(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Observe(token) != ProcessPresence.Stopped) throw new IOException("Close Brave; restore browser state is running or cannot be verified.");
        var paths = new HashSet<string>(HostEnvironment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var directories = new List<RecoveryDirectoryIdentity>();
        try
        {
            foreach (var target in _targets)
            {
                var path = BraveRestoreTargetGuard.Validate(target.Installation, target.Profile, _source, token);
                if (!paths.Add(path)) throw new IOException("Restore destinations must be distinct.");
                if (HostEnvironment.IsLinux)
                {
                    var identity = RecoveryDirectoryIdentity.Open(path);
                    try
                    {
                        if (directories.Any(existing => existing.RefersToSameDirectory(identity)))
                            throw new IOException("Restore destinations refer to the same physical directory.");
                        directories.Add(identity);
                    }
                    catch { identity.Dispose(); throw; }
                }
            }
            foreach (var identity in directories) identity.Verify();
        }
        finally { foreach (var identity in directories) identity.Dispose(); }
        if (Fingerprint(_source, token) != _fingerprint) throw new IOException("The restore source archive changed.");
        token.ThrowIfCancellationRequested();
        if (Observe(token) != ProcessPresence.Stopped) throw new IOException("Restore browser state changed or cannot be verified.");
    }
    /// <summary>Uses the current verification token for native process observation.</summary>
    private ProcessPresence Observe(CancellationToken token) =>
        _observe?.Invoke() ?? ProcessPresenceProbe.Observe(["brave"], token);

    private static string Fingerprint(string source, CancellationToken token)
    {
        using var input = SnapshotFileAccess.OpenRead(source);
        if (input.Length > ArchiveLimits.MaxBytes + 3L * 1024 * 1024) throw new InvalidDataException("Restore source exceeds its supported size.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536];
        try
        {
            long total = 0; int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested(); total += count;
                if (total > ArchiveLimits.MaxBytes + 3L * 1024 * 1024) throw new InvalidDataException("Restore source exceeds its supported size.");
                hash.AppendData(buffer.AsSpan(0, count));
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
