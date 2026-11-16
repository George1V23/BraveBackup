using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>A frozen browser installation/profile scope. Contains no password.</summary>
public sealed record BraveBackupSelection(BraveAppInfo Installation, IReadOnlyList<BraveProfileInfo> Profiles);

/// <summary>Progress measured from actual backup stages, without browser data or secrets.</summary>
public sealed record BackupProgress(string Stage, int CompletedProfiles, int TotalProfiles);

/// <summary>Secret-free operation outcome. Absolute destination is deliberately separate from Report.</summary>
public sealed record BackupResult(string Destination, string Report, long Bytes);

/// <summary>User-facing operation failure with a stable sanitized error code.</summary>
public sealed class BackupOperationException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>Creates bookmarks-only archives transactionally, without any profile write or restore.</summary>
public static class BraveArchiveBackup
{
    /// <summary>Captures selected bookmark snapshots, writes, verifies and publishes one local archive.</summary>
    /// <remarks>Password is deliberately a method argument, never a record field or operation report.</remarks>
    public static async Task<BackupResult> CreateAsync(IReadOnlyList<BraveBackupSelection> selections,
        IReadOnlyList<string> categories, string destination, string? password, string compression,
        IReadOnlyList<string> protectedDirectories, string applicationVersion,
        Func<string, Task<bool>> confirmOverwrite, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Freeze caller-owned collections before any await or worker-thread access.
        var scope = selections.Select(item => new BraveBackupSelection(item.Installation, item.Profiles.ToArray())).ToArray();
        var selectedCategories = categories.ToArray();
        var roots = protectedDirectories.Concat(scope.Select(item => item.Installation.UserDataDirectory))
            .Concat(scope.SelectMany(item => item.Profiles).Select(item => item.ProfileDirectory)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedCategories.Length != 1 || selectedCategories[0] != "bookmarks")
            throw new BackupOperationException("unsupported-category", "This increment supports Bookmarks only. Deselect other data categories before creating a backup.");
        if (scope.Length == 0 || scope.Any(item => item.Profiles.Count == 0))
            throw new BackupOperationException("empty-scope", "Select at least one profile for each installation included in the backup.");
        if (scope.Sum(item => (long)item.Profiles.Count) * 2 + 1 > ArchiveLimits.MaxEntries)
            throw new BackupOperationException("entry-limit", "Too many selected profiles for one archive.");
        if (password is { Length: 0 }) throw new BackupOperationException("password-required", "Enter an encryption password.");
        BackupArchive.Compression(compression);
        var target = ExportDestinationGuard.Validate(destination, roots);
        if (!string.Equals(Path.GetExtension(target), ".bravebackup", StringComparison.OrdinalIgnoreCase))
            throw new BackupOperationException("destination-extension", "Choose a destination ending in .bravebackup.");
        var initial = await Task.Run(() => Fingerprint(target, cancellationToken), cancellationToken);
        if (initial != null && !await confirmOverwrite(target)) throw new OperationCanceledException(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Run(scope, target, password, compression, roots, applicationVersion,
            initial, progress, cancellationToken), cancellationToken);
    }

    private static BackupResult Run(BraveBackupSelection[] selections, string target, string? password,
        string compression, string[] roots, string applicationVersion, string? initialFingerprint,
        IProgress<BackupProgress>? progress, CancellationToken token)
    {
        var start = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var total = selections.Sum(item => item.Profiles.Count);
        var done = 0;
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".bravebackup-{Guid.NewGuid():N}.tmp");
        var temporaryCreated = false;
        try
        {
            progress?.Report(new BackupProgress("Validating", 0, total));
            ExportDestinationGuard.Validate(target, roots);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = BackupFileOperations.CreatePrivateFile(temp))
            {
                temporaryCreated = true;
                using (var encrypted = password is null ? null : ArchiveEncryption.CreateWriter(output, password, token))
                using (var zip = new ZipArchive(encrypted ?? output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var installations = new List<ArchiveInstallation>();
                    long expanded = 0;
                    foreach (var selection in selections)
                    {
                        var installationId = Guid.NewGuid();
                        var profiles = new List<ArchiveProfile>();
                        foreach (var profile in selection.Profiles)
                        {
                            token.ThrowIfCancellationRequested();
                            progress?.Report(new BackupProgress("Reading", done, total));
                            var profileId = Guid.NewGuid();
                            byte[] snapshot;
                            try { snapshot = ReadSnapshot(profile.BookmarksPath, token); }
                            catch (BackupOperationException error)
                            {
                                throw new BackupOperationException(error.Code, $"Profile {done + 1} of {total}: {error.Message}");
                            }
                            byte[]? portable = null;
                            try
                            {
                                var parsed = BraveBookmarksParser.Parse(new UTF8Encoding(false, true).GetString(snapshot).TrimStart('\uFEFF'));
                                portable = JsonSerializer.SerializeToUtf8Bytes(BraveBookmarksExporter.Export(parsed),
                                    new JsonSerializerOptions { WriteIndented = true });
                                expanded += snapshot.LongLength + portable.LongLength;
                                if (portable.Length > ArchiveLimits.MaxBookmarkBytes || expanded > ArchiveLimits.MaxBytes - ArchiveLimits.MaxManifestBytes)
                                    throw new BackupOperationException("size-limit", "Selected data exceeds the archive size limit.");
                                progress?.Report(new BackupProgress("Writing", done, total));
                                var prefix = $"installations/{installationId:D}/profiles/{profileId:D}/bookmarks/";
                                var files = new[]
                                {
                                    BackupArchive.WriteFile(zip, prefix + "source.json", "native-source", snapshot, compression, token),
                                    BackupArchive.WriteFile(zip, prefix + "portable-v1.json", "portable-v1", portable, compression, token)
                                };
                                profiles.Add(new ArchiveProfile(profileId, profile.ProfileName,
                                    Path.GetFileName(Path.TrimEndingDirectorySeparator(profile.ProfileDirectory)),
                                    new[] { new ArchiveCategory("bookmarks", 1, files) }));
                                done++;
                            }
                            finally
                            {
                                CryptographicOperations.ZeroMemory(snapshot);
                                if (portable != null) CryptographicOperations.ZeroMemory(portable);
                            }
                        }
                        installations.Add(new ArchiveInstallation(installationId, selection.Installation.Product.ToString(),
                            selection.Installation.Channel.ToString(), selection.Installation.Version, profiles.ToArray()));
                    }
                    BackupArchive.WriteManifest(zip, new ArchiveManifest("BraveBackupArchive", 1, id, start,
                        applicationVersion, new[] { "bookmarks-v1" }, compression, installations.ToArray()));
                }
                token.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }
            progress?.Report(new BackupProgress("Verifying", done, total));
            using (var verify = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None))
                BackupArchive.Validate(verify, password, token);
            token.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress("Finalizing", done, total));
            ExportDestinationGuard.Validate(target, roots);
            if (Fingerprint(target, token) != initialFingerprint)
                throw new BackupOperationException("destination-changed", "The destination changed during backup. Start again to review replacement.");
            token.ThrowIfCancellationRequested();
            var bytes = new FileInfo(temp).Length;
            File.Move(temp, target, overwrite: initialFingerprint != null);
            var end = DateTimeOffset.UtcNow;
            var report = $"Archive: {id:D}\nStarted (UTC): {start:O}\nCompleted (UTC): {end:O}\nStatus: Succeeded\nProfiles: {done}/{total}\nCategories: bookmarks (completed)\nBytes: {bytes}";
            return new BackupResult(target, report, bytes);
        }
        finally
        {
            try { if (temporaryCreated) File.Delete(temp); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new BackupOperationException("cleanup-failed", "Backup did not complete and its temporary output could not be removed. Check the destination folder."); }
        }
    }

    private static byte[] ReadSnapshot(string path, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            byte[]? bytes = null;
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var stamp = File.GetLastWriteTimeUtc(path);
                var length = input.Length;
                if (length > ArchiveLimits.MaxBookmarkBytes) throw new BackupOperationException("source-size", "A selected bookmark file exceeds 256 MiB.");
                bytes = new byte[checked((int)length)];
                for (var offset = 0; offset < bytes.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                    if (count == 0) throw new IOException("Source changed while reading.");
                    offset += count;
                }
                if (input.Length != length || File.GetLastWriteTimeUtc(path) != stamp || input.ReadByte() != -1) throw new IOException("Source changed while reading.");
                BraveBookmarksParser.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
                return bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException)
            {
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
                if (error is BackupOperationException) throw;
                if (attempt >= 2) throw new BackupOperationException("source-unreadable", "A selected profile's Bookmarks file could not be read as a valid snapshot. Refresh the profile list or close Brave and try again.");
            }
            catch
            {
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
                throw;
            }
        }
    }

    private static string? Fingerprint(string path, CancellationToken token)
    {
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new BackupOperationException("destination-folder", "Choose a filename, not a folder.");
            return null;
        }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        try
        {
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, count);
            }
            return $"{input.Length}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(hash.GetHashAndReset())}";
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }
}
