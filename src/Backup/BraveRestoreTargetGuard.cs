using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Read-only destination preflight. Successful inspection does not authorize restoration or prove write access.</summary>
public static class BraveRestoreTargetGuard
{
    /// <summary>Resolves a discovered profile under its installation and rejects linked/unsafe paths and source overlap.</summary>
    public static string Validate(BraveAppInfo installation, BraveProfileInfo profile, string sourceArchive,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.UserDataDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.ProfileDirectory));
        if (string.Equals(root, Path.GetPathRoot(root), HostEnvironment.PathComparison) ||
            !string.Equals(Path.GetDirectoryName(target), root, HostEnvironment.PathComparison))
            throw new InvalidDataException("The target profile does not belong to this installation.");
        for (var directory = target; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            token.ThrowIfCancellationRequested();
            if (SnapshotFileAccess.Kind(directory) != SnapshotNodeKind.Directory)
                throw new InvalidDataException("The target path is missing, linked or unsafe.");
        }
        var source = Path.GetFullPath(sourceArchive);
        if (source.StartsWith(root + Path.DirectorySeparatorChar, HostEnvironment.PathComparison) ||
            string.Equals(source, root, HostEnvironment.PathComparison))
            throw new InvalidDataException("The source archive must be outside the destination browser data.");
        // Exercise directory enumeration without opening profile data or creating a probe file.
        using var entries = Directory.EnumerateFileSystemEntries(target).GetEnumerator();
        entries.MoveNext();
        return target;
    }
}
