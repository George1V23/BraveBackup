using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Value-free summary of the validated archive projection and its optional native source.</summary>
internal sealed record ArchivedSessionInventory(int Windows, int Tabs, SessionSource? NativeSource)
{
    internal static ArchivedSessionInventory FromValidated(PortableSession session) =>
        new(session.Windows.Length, session.Windows.Sum(window => window.Tabs.Length),
            session.IncludeNativeFiles ? session.Source : null);

    internal bool CanRestore => NativeSource is { Length: > 0 and <= LinuxRestoreFileCapture.MaximumBytes };
}

/// <summary>Binds the exact archived native snapshot and the destination's selected native file state to final review.</summary>
internal sealed record NativeSessionRestoreSelection(SessionSource Source, string FileName, string OriginalDigest);

/// <summary>Plans conservative replacements of a closed profile's newest existing plaintext native Session file.</summary>
internal static class BraveNativeSessionRestore
{
    internal const string ParentName = "Sessions";
    private const string EncryptedDirectory = "Sessions_Encrypted";
    private const int MaxEntries = 4096;

    /// <summary>Captures and validates the destination's supported native Session file without writing to the profile.</summary>
    internal static NativeSessionRestoreSelection Review(string profilePath, SessionSource source, CancellationToken token)
    {
        var name = FindLatest(profilePath, token);
        using var original = LinuxRestoreFileCapture.Read(Path.Combine(profilePath, ParentName), name, token);
        if (!original.Exists) throw ChangedDestination();
        try
        {
            BraveSessionDecoder.Decode(original.Content.Bytes, Path.Combine(ParentName, name), DateTimeOffset.UnixEpoch, token: token);
        }
        catch (BackupOperationException)
        {
            throw new BackupOperationException("restore-session-destination-format",
                "The newest destination Session file is not a supported complete plaintext SNSS v3 snapshot.");
        }
        VerifyLatest(profilePath, name, token);
        return new(source, name, Digest(original));
    }

    /// <summary>Finds the native file that the validated portable projection identifies as its source.</summary>
    internal static ArchiveFile ReviewedSourceFile(ZipArchive zip, ArchiveCategory category,
        NativeSessionRestoreSelection selection, CancellationToken token)
    {
        var model = BraveSessionBackup.ReadPortable(zip, category, token);
        if (!model.IncludeNativeFiles || model.Source != selection.Source)
            throw new BackupOperationException("restore-session-source", "The reviewed native Session snapshot is unavailable.");
        var prefix = category.Files.Single(file => file.Role == "portable-session").Path[..^"portable-v1.json".Length];
        var path = prefix + "native/" + model.Source.Path;
        return category.Files.SingleOrDefault(file => file.Role == "native-session-file" &&
            file.Length == model.Source.Length && file.Sha256 == model.Source.Sha256 && file.Path == path)
            ?? throw new BackupOperationException("restore-session-source", "The reviewed native Session snapshot is unavailable.");
    }

    /// <summary>Rechecks source selection at the destination before forward publication and resume.</summary>
    internal static void VerifyLatest(string profilePath, string name, CancellationToken token)
    {
        if (!string.Equals(FindLatest(profilePath, token), name, StringComparison.Ordinal))
            throw ChangedDestination();
    }

    /// <summary>Rejects a destination file changed since Recheck, including changes to supported metadata.</summary>
    internal static void VerifyOriginal(RestoreFileImage original, NativeSessionRestoreSelection selection)
    {
        if (!original.Exists || Digest(original) != selection.OriginalDigest) throw ChangedDestination();
    }

    private static string FindLatest(string profilePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!HostEnvironment.IsLinux || !Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Native Session restore requires 64-bit little-endian Linux.");

        var encrypted = Path.Combine(profilePath, EncryptedDirectory);
        if (SnapshotFileAccess.Kind(encrypted) != SnapshotNodeKind.Missing)
        {
            using var directory = RecoveryDirectoryIdentity.Open(encrypted);
            foreach (var entry in BoundedNames(directory, token))
                if (entry.StartsWith("Session_", StringComparison.Ordinal) || entry.StartsWith("Tabs_", StringComparison.Ordinal))
                    throw new BackupOperationException("restore-session-encrypted",
                        "The destination has encrypted session files. Brave may use them in preference to the plaintext snapshot.");
        }

        var path = Path.Combine(profilePath, ParentName);
        if (SnapshotFileAccess.Kind(path) == SnapshotNodeKind.Missing) throw Missing();
        using var sessions = RecoveryDirectoryIdentity.Open(path);
        var newest = 0L;
        string? file = null;
        foreach (var entry in BoundedNames(sessions, token))
        {
            if (!entry.StartsWith("Session_", StringComparison.Ordinal)) continue;
            if (!BraveSessionBackup.ModernName(entry, sessionOnly: true) ||
                SnapshotFileAccess.Kind(Path.Combine(path, entry)) != SnapshotNodeKind.RegularFile)
                throw new BackupOperationException("restore-session-destination-format",
                    "A destination Session filename or file type is unsafe or ambiguous.");
            var timestamp = long.Parse(entry.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture);
            if (timestamp <= newest) continue;
            newest = timestamp;
            file = entry;
        }

        return file ?? throw Missing();
    }

    private static IEnumerable<string> BoundedNames(RecoveryDirectoryIdentity directory, CancellationToken token)
    {
        var count = 0;
        foreach (var name in directory.EnumerateChildNames())
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaxEntries)
                throw new BackupOperationException("restore-session-destination-format", "Too many entries in the destination session directory.");
            yield return name;
        }
        directory.Verify();
    }

    private static string Digest(RestoreFileImage image)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(image.Metadata.Bytes.Span);
        hash.AppendData(image.Content.Bytes.Span);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static BackupOperationException Missing() =>
        new("restore-session-missing", "The destination needs an existing supported modern Session file.");

    private static BackupOperationException ChangedDestination() =>
        new("restore-session-target", "The destination Session file changed after Recheck.");
}
