using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Read-only frozen source/target/process checks for a future explicit restore transaction adapter.</summary>
public sealed class BraveRestoreContext
{
    private readonly string _source;
    private readonly (BraveAppInfo Installation, BraveProfileInfo Profile)[] _targets;
    private readonly Func<ProcessPresence> _observe;
    private readonly string _fingerprint;
    private readonly (long Length, long WriteTicks) _stamp;
    private BraveRestoreContext(string source, (BraveAppInfo, BraveProfileInfo)[] targets, Func<ProcessPresence> observe, string fingerprint, (long, long) stamp)
    { _source = source; _targets = targets; _observe = observe; _fingerprint = fingerprint; _stamp = stamp; }
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
        var observer = observe ?? (() => ProcessPresenceProbe.Observe(["brave"], token));
        // The stamp is read before and after hashing so an edit during capture is rejected rather than frozen.
        var before = Stamp(source);
        var fingerprint = Fingerprint(source, token);
        if (Stamp(source) != before) throw new IOException("The restore source archive changed during capture.");
        var context = new BraveRestoreContext(source, frozen, observer, fingerprint, before);
        context.Verify(token); return context;
    }
    /// <summary>Full verification: rereads and hashes the entire source archive. Use before any write.</summary>
    public void Verify(CancellationToken token = default) => Verify(token, full: true);
    /// <summary>
    /// Routine verification: checks the source archive's length and modified time instead of hashing it. It catches ordinary edits and
    /// replacements but not a deliberate same-size rewrite that preserves the modified time; <see cref="Verify(CancellationToken)"/> does.
    /// </summary>
    public void VerifyQuick(CancellationToken token = default) => Verify(token, full: false);
    private void Verify(CancellationToken token, bool full)
    {
        token.ThrowIfCancellationRequested();
        if (_observe() != ProcessPresence.Stopped) throw new IOException("Close Brave; restore browser state is running or cannot be verified.");
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
        if (full ? Fingerprint(_source, token) != _fingerprint : Stamp(_source) != _stamp)
            throw new IOException("The restore source archive changed.");
        token.ThrowIfCancellationRequested();
        if (_observe() != ProcessPresence.Stopped) throw new IOException("Restore browser state changed or cannot be verified.");
    }
    private static (long Length, long WriteTicks) Stamp(string source)
    {
        using var input = SnapshotFileAccess.OpenRead(source);
        return (input.Length, File.GetLastWriteTimeUtc(input.SafeFileHandle).Ticks);
    }
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
