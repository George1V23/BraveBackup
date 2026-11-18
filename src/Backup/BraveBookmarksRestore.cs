using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>One explicit source-profile to destination-profile bookmarks mapping (archive profile identity, never a directory name).</summary>
public sealed record BraveBookmarksRestoreItem(Guid SourceProfileId, BraveAppInfo Installation, BraveProfileInfo Profile);

/// <summary>
/// Bookmarks-only restore: the archive's exact native <c>Bookmarks</c> byte snapshot (<c>native-source</c>) becomes the destination
/// profile's <c>Bookmarks</c> file. No format is converted or invented. Nothing else in the profile is read or written.
/// </summary>
/// <remarks>
/// Only snapshots that parse as Chromium bookmarks <c>version</c> 1 and are at most 64 MiB are restorable. Existing destinations keep
/// their permissions (modified time becomes the restore time). Absent destinations (Linux only) borrow the permissions of the same
/// profile's existing <c>Preferences</c> file; without it the profile has never been used and Brave must create it first.
/// <c>Bookmarks.bak</c>, sync state and favicons are not touched; Brave regenerates them. Windows is not natively validated.
/// </remarks>
public static class BraveBookmarksRestore
{
    public const string FileName = "Bookmarks";
    public const int MaximumBytes = 64 * 1024 * 1024;

    /// <summary>Reads and verifies the native bookmark snapshot of one archived profile; the caller clears the returned bytes.</summary>
    public static byte[] ReadSource(Stream archive, string? password, ArchiveManifest manifest, Guid sourceProfileId, CancellationToken token = default)
    {
        var profile = manifest.Installations.SelectMany(i => i.Profiles).FirstOrDefault(p => p.Id == sourceProfileId)
            ?? throw new InvalidDataException("The archive does not contain the selected source profile.");
        var category = profile.Categories.FirstOrDefault(c => c.Id == "bookmarks" && c.Version == 1)
            ?? throw new InvalidDataException("The selected source profile has no supported bookmarks backup.");
        var file = category.Files.SingleOrDefault(f => f.Role == "native-source")
            ?? throw new InvalidDataException("The archive has no native bookmarks snapshot for this profile.");
        if (file.Length > MaximumBytes) throw new InvalidDataException("The bookmarks snapshot exceeds the 64 MiB restore limit.");
        archive.Position = 0;
        using var decrypted = ArchiveEncryption.IsEncrypted(archive) ? ArchiveEncryption.OpenReader(archive, password ?? string.Empty, token) : null;
        var content = decrypted ?? archive; content.Position = 0;
        using var zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var bytes = new byte[checked((int)file.Length)];
        try
        {
            using (var input = zip.GetEntry(file.Path)?.Open() ?? throw new InvalidDataException("Archive bookmarks payload is missing."))
            {
                input.ReadExactly(bytes);
                if (input.ReadByte() != -1) throw new InvalidDataException("Archive bookmarks payload length differs from its manifest.");
            }
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256.ToLowerInvariant())
                throw new InvalidDataException("Archive bookmarks payload hash mismatch.");
            CheckSupported(bytes);
            return bytes;
        }
        catch { CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    /// <summary>Requires strict UTF-8 JSON that parses as Chromium bookmarks <c>version</c> 1 (a leading BOM is tolerated, as in capture).</summary>
    public static void CheckSupported(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The bookmarks snapshot exceeds the 64 MiB restore limit.");
        var parsed = BraveBookmarksParser.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'));
        if (parsed.Version != 1) throw new InvalidDataException("Only Chromium bookmarks version 1 can be restored.");
    }

    /// <summary>Builds the replacement image for a destination profile directory, with metadata taken from that profile's existing files.</summary>
    public static RestoreFileImage BuildImage(ReadOnlySpan<byte> sourceBytes, string profileDirectory, DateTime? nowUtc = null)
    {
        CheckSupported(sourceBytes);
        var now = (nowUtc ?? DateTime.UtcNow).Ticks;
        var directory = Path.GetFullPath(profileDirectory);
        var existing = Path.Combine(directory, FileName);
        var exists = SnapshotFileAccess.Kind(existing) == SnapshotNodeKind.RegularFile;
        string donor;
        if (exists) donor = existing;
        else if (OperatingSystem.IsLinux() && SnapshotFileAccess.Kind(Path.Combine(directory, "Preferences")) == SnapshotNodeKind.RegularFile)
            donor = Path.Combine(directory, "Preferences");
        else throw new InvalidDataException(OperatingSystem.IsLinux()
            ? "The destination profile has no Bookmarks or Preferences file; open it in Brave once first."
            : "Restoring into a destination without a Bookmarks file is not supported on this platform yet.");
        using var handle = File.OpenHandle(donor, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var metadata = OperatingSystem.IsLinux() ? LinuxRecoveryMetadata.Capture(handle)
            : OperatingSystem.IsWindows() ? WindowsRecoveryMetadata.Capture(handle) : throw new PlatformNotSupportedException();
        // Modified time becomes the restore time. Linux stores it at 12; Windows stores write time at 16 and access time at 24.
        if (OperatingSystem.IsLinux()) BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(12), now);
        else { BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(16), now); BinaryPrimitives.WriteInt64LittleEndian(metadata.AsSpan(24), now); }
        try { return new RestoreFileImage(true, sourceBytes, metadata); }
        finally { CryptographicOperations.ZeroMemory(metadata); }
    }
}

/// <summary>
/// Owns everything for one explicit bookmarks restore: frozen context, recovery artifacts, retained targets and the transaction.
/// Creating it writes nothing; the host must call <see cref="Transaction"/> Prepare/Run only after final user approval.
/// </summary>
public sealed class BraveBookmarksRestoreSession : IDisposable
{
    private readonly IDisposable _artifacts;
    private readonly RestoreTransactionStore _store;
    public RestoreTransaction Transaction { get; }
    /// <summary>Full path of the encrypted safety artifact that Prepare will create.</summary>
    public string SafetyPath { get; }
    /// <summary>Full path of the encrypted journal artifact.</summary>
    public string JournalPath { get; }

    private BraveBookmarksRestoreSession(RestoreTransaction transaction, RestoreTransactionStore store, IDisposable artifacts, RestoreArtifactScope scope)
    { Transaction = transaction; _store = store; _artifacts = artifacts; SafetyPath = scope.SafetyPath; JournalPath = scope.JournalPath; }

    /// <summary>Validates the archive and every mapping, freezes the context and retains targets. Throws without writing on any problem.</summary>
    public static BraveBookmarksRestoreSession Create(string archivePath, string? archivePassword, IReadOnlyList<BraveBookmarksRestoreItem> items,
        string recoveryDirectory, string safetyPassword, Func<ProcessPresence>? observe = null, CancellationToken token = default)
    {
        if (items.Count == 0) throw new InvalidDataException("Select at least one bookmarks restore mapping.");
        var inspection = BraveArchiveInspection.Read(archivePath, archivePassword, token);
        var manifest = inspection.Manifest ?? throw new InvalidDataException("Unlock the archive first.");
        var images = new List<RestoreFileImage>();
        try
        {
            using (var source = SnapshotFileAccess.OpenRead(inspection.SourcePath))
                foreach (var item in items)
                {
                    var bytes = BraveBookmarksRestore.ReadSource(source, archivePassword, manifest, item.SourceProfileId, token);
                    try { images.Add(BraveBookmarksRestore.BuildImage(bytes, item.Profile.ProfileDirectory)); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
            var context = BraveRestoreContext.Capture(inspection.SourcePath, items.Select(i => (i.Installation, i.Profile)).ToArray(), observe, token);
            var scope = new RestoreArtifactScope(Guid.NewGuid(), recoveryDirectory, inspection.SourcePath,
                items.Select(i => Path.GetFullPath(i.Installation.UserDataDirectory)).Distinct());
            IRestoreArtifacts artifacts = OperatingSystem.IsLinux() ? RestoreArtifactLease.Acquire(scope)
                : OperatingSystem.IsWindows() ? WindowsRestoreArtifacts.Acquire(scope) : throw new PlatformNotSupportedException();
            RestoreTransactionStore? store = null;
            try
            {
                var ids = items.Select(_ => Guid.NewGuid()).ToArray();
                store = RestoreTransactionStore.Create(context, artifacts,
                    items.Select((item, index) => (ids[index], (Path.GetFullPath(item.Profile.ProfileDirectory), BraveBookmarksRestore.FileName)))
                        .ToDictionary(pair => pair.Item1, pair => pair.Item2), token);
                var mutations = items.Select((item, index) => new RestoreMutation(ids[index], manifest.Installations.First(i => i.Profiles.Any(p => p.Id == item.SourceProfileId)).Id, item.SourceProfileId, "bookmarks", images[index])).ToArray();
                var transaction = new RestoreTransaction(mutations, store, safetyPassword);
                return new(transaction, store, (IDisposable)artifacts, scope);
            }
            catch { store?.Dispose(); (artifacts as IDisposable)?.Dispose(); throw; }
        }
        finally { foreach (var image in images) image.Dispose(); }
    }

    /// <summary>Disposes the transaction, retained targets and artifact ownership; recovery artifacts stay on disk.</summary>
    public void Dispose() { Transaction.Dispose(); _store.Dispose(); _artifacts.Dispose(); }
}
