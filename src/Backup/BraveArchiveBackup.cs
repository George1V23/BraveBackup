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
public sealed record BackupResult(string Destination, string Report, long Bytes)
{
    /// <summary>Gets stored/absent Settings counts for completion feedback, without preference values.</summary>
    public string SettingsSummary { get; init; } = string.Empty;

    /// <summary>Gets extension file/version/storage counts without extension data values or identities.</summary>
    public string ExtensionsSummary { get; init; } = string.Empty;

    /// <summary>Gets last-persisted session counts and limits without URLs, titles or source paths.</summary>
    public string SessionsSummary { get; init; } = string.Empty;

    /// <summary>Gets saved-window counts and capture limits without browsing content or source paths.</summary>
    public string TabManagerPlusSummary { get; init; } = string.Empty;

    /// <summary>Portable credential/note counts only, without sites, usernames, secrets or source paths.</summary>
    public string PasswordsSummary { get; init; } = string.Empty;
}

/// <summary>User-facing operation failure with a stable sanitized error code.</summary>
public sealed class BackupOperationException(string code, string message) : IOException(message)
{
    public string Code { get; } = code;
}

/// <summary>Creates selected supported-category archives transactionally, without any profile write or restore.</summary>
public static class BraveArchiveBackup
{
    /// <summary>Captures selected profile snapshots, writes, verifies and publishes one local archive.</summary>
    /// <remarks>Password is deliberately a method argument, never a record field or operation report.</remarks>
    public static async Task<BackupResult> CreateAsync(IReadOnlyList<BraveBackupSelection> selections,
        IReadOnlyList<string> categories, string destination, string? password, string compression,
        IReadOnlyList<string> protectedDirectories, string applicationVersion,
        Func<string, Task<bool>> confirmOverwrite, IProgress<BackupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await CreateAsync(selections, categories, destination, password, compression, protectedDirectories,
            applicationVersion, confirmOverwrite, progress, cancellationToken, new ExtensionBackupOptions());
    }

    /// <summary>Creates an archive with explicit host confirmation and injectable extension process observation.</summary>
    public static async Task<BackupResult> CreateAsync(IReadOnlyList<BraveBackupSelection> selections,
        IReadOnlyList<string> categories, string destination, string? password, string compression,
        IReadOnlyList<string> protectedDirectories, string applicationVersion,
        Func<string, Task<bool>> confirmOverwrite, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken, ExtensionBackupOptions extensionOptions)
    {
        return await CreateAsync(selections, categories, destination, password, compression, protectedDirectories,
            applicationVersion, confirmOverwrite, progress, cancellationToken, extensionOptions,
            new SessionBackupOptions());
    }

    /// <summary>Creates an archive with frozen native-session choices and per-operation raw-data consent.</summary>
    public static async Task<BackupResult> CreateAsync(IReadOnlyList<BraveBackupSelection> selections,
        IReadOnlyList<string> categories, string destination, string? password, string compression,
        IReadOnlyList<string> protectedDirectories, string applicationVersion,
        Func<string, Task<bool>> confirmOverwrite, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken, ExtensionBackupOptions extensionOptions,
        SessionBackupOptions sessionOptions)
    {
        return await CreateAsync(selections, categories, destination, password, compression, protectedDirectories,
            applicationVersion, confirmOverwrite, progress, cancellationToken, extensionOptions, sessionOptions,
            new TabManagerPlusBackupOptions(), null);
    }

    /// <summary>Preserves the existing archive API without enabling password access for existing callers.</summary>
    public static Task<BackupResult> CreateAsync(IReadOnlyList<BraveBackupSelection> selections,
        IReadOnlyList<string> categories, string destination, string? password, string compression,
        IReadOnlyList<string> protectedDirectories, string applicationVersion,
        Func<string, Task<bool>> confirmOverwrite, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken, ExtensionBackupOptions extensionOptions,
        SessionBackupOptions sessionOptions, TabManagerPlusBackupOptions tabManagerPlusOptions) =>
        CreateAsync(selections, categories, destination, password, compression, protectedDirectories,
            applicationVersion, confirmOverwrite, progress, cancellationToken, extensionOptions, sessionOptions,
            tabManagerPlusOptions, null);

    /// <summary>Creates supported-category archives with saved-window consent and process observation supplied by the host.</summary>
    public static async Task<BackupResult> CreateAsync(IReadOnlyList<BraveBackupSelection> selections,
        IReadOnlyList<string> categories, string destination, string? password, string compression,
        IReadOnlyList<string> protectedDirectories, string applicationVersion,
        Func<string, Task<bool>> confirmOverwrite, IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken, ExtensionBackupOptions extensionOptions,
        SessionBackupOptions sessionOptions, TabManagerPlusBackupOptions tabManagerPlusOptions,
        PasswordBackupOptions? passwordOptions)
    {
        // Freeze caller-owned collections before any await or worker-thread access.
        var scope = selections.Select(item => new BraveBackupSelection(item.Installation, item.Profiles.ToArray()))
            .ToArray();
        var selectedCategories = categories.ToArray();
        var roots = protectedDirectories.Concat(scope.Select(item => item.Installation.UserDataDirectory))
            .Concat(scope.SelectMany(item => item.Profiles).Select(item => item.ProfileDirectory)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedCategories.Length == 0 ||
            selectedCategories.Distinct(StringComparer.Ordinal).Count() != selectedCategories.Length ||
            selectedCategories.Any(category =>
                category is not ("bookmarks" or "settings" or "extensions" or "sessions" or "tabmanagerplus") &&
                !(category == "passwords" && passwordOptions is not null)))
            throw new BackupOperationException("unsupported-category",
                "Select supported Bookmarks, Settings, Extensions, Sessions and/or TabManagerPlus. Passwords remain unavailable.");
        if (selectedCategories.Contains("passwords"))
        {
            if (!passwordOptions!.ProtectPasswords)
                throw new BackupOperationException("passwords-mode-unsupported", "This archive path supports protected portable passwords only; CSV and raw saving are not enabled.");
            if (string.IsNullOrEmpty(password))
                throw new BackupOperationException("passwords-protection-required", "Protected passwords require archive encryption. Disabling encryption does not choose CSV.");
            if (passwordOptions.GetProvider is null)
                throw new BackupOperationException("passwords-provider-required", "Establish the supported password provider/backend for each selected profile before capture.");
        }
        if (scope.Length == 0 || scope.Any(item => item.Profiles.Count == 0))
            throw new BackupOperationException("empty-scope",
                "Select at least one profile for each installation included in the backup.");
        if (scope.Sum(item => (long)item.Profiles.Count) * (selectedCategories.Contains("bookmarks") ? 2 : 0) +
            scope.Sum(item => (long)item.Profiles.Count) * (selectedCategories.Contains("settings") ? 1 : 0) +
            scope.Sum(item => (long)item.Profiles.Count) * (selectedCategories.Contains("passwords") ? 1 : 0) + 1 >
            ArchiveLimits.MaxEntries)
            throw new BackupOperationException("entry-limit", "Too many selected profiles for one archive.");
        if (password is { Length: 0 })
            throw new BackupOperationException("password-required", "Enter an encryption password.");
        BackupArchive.Compression(compression);
        var target = ExportDestinationGuard.Validate(destination, roots);
        if (!string.Equals(Path.GetExtension(target), ".bravebackup", StringComparison.OrdinalIgnoreCase))
            throw new BackupOperationException("destination-extension", "Choose a destination ending in .bravebackup.");
        ExtensionCaptureSession? extensionSession = null;
        if (selectedCategories.Contains("extensions"))
        {
            extensionSession = new ExtensionCaptureSession(scope, extensionOptions, cancellationToken);
            await Task.Run(() => extensionSession.EnsureStopped(true), cancellationToken);
            if (password is null && (extensionOptions.ConfirmUnencryptedData is null ||
                                     !await extensionOptions.ConfirmUnencryptedData(target)))
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => extensionSession.EnsureStopped(true), cancellationToken);
        }

        SessionCaptureSession? sessionSession = null;
        if (selectedCategories.Contains("sessions"))
        {
            if (sessionOptions.IncludeNativeFiles && password is null &&
                (sessionOptions.ConfirmUnencryptedNativeFiles is null ||
                 !await sessionOptions.ConfirmUnencryptedNativeFiles(target)))
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            sessionSession = new SessionCaptureSession(sessionOptions.IncludeNativeFiles, cancellationToken);
        }

        TabManagerPlusCaptureSession? tabManagerPlus = null;
        if (selectedCategories.Contains("tabmanagerplus"))
        {
            tabManagerPlus = new TabManagerPlusCaptureSession(scope, tabManagerPlusOptions, cancellationToken);
            await Task.Run(tabManagerPlus.EnsureStopped, cancellationToken);
            if (password is null && (tabManagerPlusOptions.ConfirmUnencryptedData is null ||
                                     !await tabManagerPlusOptions.ConfirmUnencryptedData(target)))
                throw new OperationCanceledException(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(tabManagerPlus.EnsureStopped, cancellationToken);
        }

        var initial = await Task.Run(() => Fingerprint(target, cancellationToken), cancellationToken);
        if (initial != null && !await confirmOverwrite(target)) throw new OperationCanceledException(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(() => Run(scope, target, password, compression, roots, applicationVersion,
                initial, progress, cancellationToken, selectedCategories, extensionSession, sessionSession,
                tabManagerPlus, selectedCategories.Contains("passwords") ? new PasswordCaptureSession(passwordOptions!, cancellationToken) : null),
            cancellationToken);
    }

    private static BackupResult Run(BraveBackupSelection[] selections, string target, string? password,
        string compression, string[] roots, string applicationVersion, string? initialFingerprint,
        IProgress<BackupProgress>? progress, CancellationToken token, string[] categories,
        ExtensionCaptureSession? extensions, SessionCaptureSession? sessions,
        TabManagerPlusCaptureSession? tabManagerPlus, PasswordCaptureSession? passwords)
    {
        var start = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var total = selections.Sum(item => item.Profiles.Count);
        var done = 0;
        var storedSettings = 0;
        var absentSettings = 0;
        var settingsCounts = new List<int>();
        var hasBookmarks = categories.Contains("bookmarks", StringComparer.Ordinal);
        var hasSettings = categories.Contains("settings", StringComparer.Ordinal);
        var capabilities = new[] { "bookmarks", "settings", "extensions", "sessions", "tabmanagerplus", "passwords" }
            .Where(categories.Contains).Select(category => category + "-v1").ToArray();
        var entryCount = 1;
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
                using (var encrypted =
                       password is null ? null : ArchiveEncryption.CreateWriter(output, password, token))
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
                            var profileCategories = new List<ArchiveCategory>();
                            try
                            {
                                if (hasBookmarks)
                                {
                                    var snapshot = ReadSnapshot(profile.BookmarksPath, token);
                                    byte[]? portable = null;
                                    try
                                    {
                                        var parsed = BraveBookmarksParser.Parse(new UTF8Encoding(false, true)
                                            .GetString(snapshot).TrimStart('\uFEFF'));
                                        portable = JsonSerializer.SerializeToUtf8Bytes(
                                            BraveBookmarksExporter.Export(parsed),
                                            new JsonSerializerOptions { WriteIndented = true });
                                        expanded += snapshot.LongLength + portable.LongLength;
                                        if (portable.Length > ArchiveLimits.MaxBookmarkBytes ||
                                            expanded > ArchiveLimits.MaxBytes - ArchiveLimits.MaxManifestBytes)
                                            throw new BackupOperationException("size-limit",
                                                "Selected data exceeds the archive size limit.");
                                        progress?.Report(new BackupProgress("Writing", done, total));
                                        var prefix =
                                            $"installations/{installationId:D}/profiles/{profileId:D}/bookmarks/";
                                        var files = new[]
                                        {
                                            BackupArchive.WriteFile(zip, prefix + "source.json", "native-source",
                                                snapshot, compression, token),
                                            BackupArchive.WriteFile(zip, prefix + "portable-v1.json", "portable-v1",
                                                portable, compression, token)
                                        };
                                        profileCategories.Add(new ArchiveCategory("bookmarks", 1, files));
                                    }
                                    finally
                                    {
                                        CryptographicOperations.ZeroMemory(snapshot);
                                        if (portable != null) CryptographicOperations.ZeroMemory(portable);
                                    }
                                }

                                if (hasSettings)
                                {
                                    progress?.Report(new BackupProgress("Reading settings", done, total));
                                    var settings = BraveSettingsBackup.Capture(profile.ProfileDirectory, token);
                                    try
                                    {
                                        var count = BraveSettingsBackup.ValidatePayload(settings);
                                        storedSettings += count;
                                        absentSettings += 7 - count;
                                        settingsCounts.Add(count);
                                        expanded += settings.LongLength;
                                        if (expanded > ArchiveLimits.MaxBytes - ArchiveLimits.MaxManifestBytes)
                                            throw new BackupOperationException("size-limit",
                                                "Selected data exceeds the archive size limit.");
                                        progress?.Report(new BackupProgress("Writing settings", done, total));
                                        var path =
                                            $"installations/{installationId:D}/profiles/{profileId:D}/settings/allowlisted-v1.json";
                                        var file = BackupArchive.WriteFile(zip, path, "portable-settings", settings,
                                            compression, token);
                                        profileCategories.Add(new ArchiveCategory("settings", 1, new[] { file }));
                                    }
                                    finally
                                    {
                                        CryptographicOperations.ZeroMemory(settings);
                                    }
                                }

                                if (extensions != null)
                                {
                                    progress?.Report(new BackupProgress("Reading/writing extensions", done, total));
                                    extensions.EnsureStopped(true);
                                    var category = extensions.Capture(profile.ProfileDirectory,
                                        $"installations/{installationId:D}/profiles/{profileId:D}/extensions/", zip,
                                        compression,
                                        ArchiveLimits.MaxEntries - entryCount -
                                        profileCategories.Sum(item => item.Files.Length));
                                    profileCategories.Add(category);
                                }

                                if (sessions is not null)
                                {
                                    progress?.Report(new BackupProgress("Reading/writing persisted sessions", done,
                                        total));
                                    extensions?.EnsureStopped(true);
                                    profileCategories.Add(sessions.Capture(profile.ProfileDirectory,
                                        $"installations/{installationId:D}/profiles/{profileId:D}/sessions/", zip,
                                        compression,
                                        ArchiveLimits.MaxEntries - entryCount -
                                        profileCategories.Sum(item => item.Files.Length)));
                                    extensions?.EnsureStopped(true);
                                }

                                if (tabManagerPlus is not null)
                                {
                                    progress?.Report(new BackupProgress("Reading/writing saved windows", done, total));
                                    profileCategories.Add(tabManagerPlus.Capture(profile.ProfileDirectory,
                                        $"installations/{installationId:D}/profiles/{profileId:D}/tabmanagerplus/", zip,
                                        compression, ArchiveLimits.MaxEntries - entryCount -
                                                     profileCategories.Sum(item => item.Files.Length)));
                                }
                                if (passwords is not null)
                                {
                                    progress?.Report(new BackupProgress("Reading/writing protected passwords", done, total));
                                    profileCategories.Add(passwords.Capture(selection.Installation, profile,
                                        $"installations/{installationId:D}/profiles/{profileId:D}/passwords/", zip, compression));
                                }
                            }
                            catch (BackupOperationException error)
                            {
                                throw new BackupOperationException(error.Code,
                                    $"Profile {done + 1} of {total}: {error.Message}");
                            }

                            entryCount += profileCategories.Sum(item => item.Files.Length);
                            if (entryCount > ArchiveLimits.MaxEntries ||
                                expanded + (extensions?.RawBytes ?? 0) + (sessions?.Bytes ?? 0) +
                                (tabManagerPlus?.Bytes ?? 0) + (passwords?.Bytes ?? 0) >
                                ArchiveLimits.MaxBytes - ArchiveLimits.MaxManifestBytes)
                                throw new BackupOperationException("size-limit",
                                    "Selected data exceeds archive resource limits.");
                            profiles.Add(new ArchiveProfile(profileId, profile.ProfileName,
                                Path.GetFileName(Path.TrimEndingDirectorySeparator(profile.ProfileDirectory)),
                                profileCategories.ToArray()));
                            done++;
                        }

                        installations.Add(new ArchiveInstallation(installationId,
                            selection.Installation.Product.ToString(),
                            selection.Installation.Channel.ToString(), selection.Installation.Version,
                            profiles.ToArray()));
                    }

                    BackupArchive.WriteManifest(zip, new ArchiveManifest("BraveBackupArchive",
                        passwords is not null ? 6 :
                        tabManagerPlus is not null ? 5 :
                        sessions is not null ? 4 :
                        extensions is not null ? 3 :
                        hasSettings ? 2 : 1, id,
                        start,
                        applicationVersion, capabilities, compression, installations.ToArray()));
                }

                token.ThrowIfCancellationRequested();
                output.Flush(flushToDisk: true);
            }

            progress?.Report(new BackupProgress("Verifying", done, total));
            using (var verify = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None))
                BraveSettingsBackup.ValidateArchive(verify, password, token);
            token.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress("Finalizing", done, total));
            ExportDestinationGuard.Validate(target, roots);
            if (Fingerprint(target, token) != initialFingerprint)
                throw new BackupOperationException("destination-changed",
                    "The destination changed during backup. Start again to review replacement.");
            sessions?.VerifySources();
            extensions?.VerifySources();
            tabManagerPlus?.VerifySources();
            passwords?.VerifySources();
            token.ThrowIfCancellationRequested();
            extensions?.EnsureStopped(true);
            var bytes = new FileInfo(temp).Length;
            tabManagerPlus?.EnsureStopped();
            if (passwords is not null)
            {
                ExportDestinationGuard.Validate(target, roots);
                if (Fingerprint(target, token) != initialFingerprint)
                    throw new BackupOperationException("destination-changed", "The destination changed during source verification. Review replacement again.");
            }
            passwords?.EnsureStopped();
            token.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: initialFingerprint != null);
            var end = DateTimeOffset.UtcNow;
            var categoryNames = string.Join(", ", capabilities.Select(value => value[..^3]));
            var extensionsSummary = extensions is null
                ? string.Empty
                : $"Extension packages: {extensions.PackageCount}; versions: {extensions.VersionCount}; storage-only identities: {extensions.StorageOnlyCount}\nExtension files: {extensions.FileCount}; bytes: {extensions.RawBytes}\nSupported storage areas: {extensions.PresentAreas} present; {extensions.EmptyAreas} empty; {extensions.AbsentAreas} absent";
            var sessionsSummary = sessions is null
                ? string.Empty
                : $"Last persisted sessions: {sessions.Profiles}; windows: {sessions.WindowCount}; tabs: {sessions.TabCount}; groups: {sessions.GroupCount}\nGroups without stored metadata: {sessions.MissingGroupMetadata}; native files: {sessions.NativeCount}\nSource: modern unencrypted session format 3. Persisted data may be older than visible browser state. Restore is not available.";
            var tabManagerPlusSummary = tabManagerPlus is null
                ? string.Empty
                : $"TabManagerPlus saved-window profiles: {tabManagerPlus.Profiles}; windows: {tabManagerPlus.WindowCount}; tabs: {tabManagerPlus.TabCount}\nSource: supported 7.0.0 stored saved windows, captured with Brave observed closed. Unsaved live windows, extension preferences and native tab-group metadata are not included. Restore is not available.";
            var passwordsSummary = passwords is null ? string.Empty :
                $"Protected password profiles: {passwords.Profiles}; credentials: {passwords.CredentialCount}; notes: {passwords.NoteCount}; excluded entries: {passwords.ExcludedCount}\nPortable capture completed inside an encrypted archive. Raw preservation and CSV were not used. Restore is not available.";
            var settingsSummary = hasSettings
                ? $"\nStored supported settings: {storedSettings}; absent keys: {absentSettings}\nStored settings per profile (scope order): {string.Join(", ", settingsCounts)}"
                : string.Empty;
            var report =
                $"Archive: {id:D}\nStarted (UTC): {start:O}\nCompleted (UTC): {end:O}\nStatus: Succeeded\nProfiles: {done}/{total}\nCategories: {categoryNames} (completed)\nBytes: {bytes}{settingsSummary}{(extensionsSummary.Length == 0 ? "" : "\n" + extensionsSummary)}{(sessionsSummary.Length == 0 ? "" : "\n" + sessionsSummary)}{(tabManagerPlusSummary.Length == 0 ? "" : "\n" + tabManagerPlusSummary)}{(passwordsSummary.Length == 0 ? "" : "\n" + passwordsSummary)}";
            return new BackupResult(target, report, bytes)
            {
                SettingsSummary = settingsSummary.TrimStart(), ExtensionsSummary = extensionsSummary,
                SessionsSummary = sessionsSummary, TabManagerPlusSummary = tabManagerPlusSummary, PasswordsSummary = passwordsSummary
            };
        }
        finally

        {
            passwords?.Dispose();
            tabManagerPlus?.Dispose();
            try
            {
                if (temporaryCreated) File.Delete(temp);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new BackupOperationException("cleanup-failed",
                    "Backup did not complete and its temporary output could not be removed. Check the destination folder.");
            }
        }
    }

    private static byte[] ReadSnapshot(string path, CancellationToken token)
    {
        for (var attempt = 0;; attempt++)
        {
            token.ThrowIfCancellationRequested();
            byte[]? bytes = null;
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                var stamp = File.GetLastWriteTimeUtc(path);
                var length = input.Length;
                if (length > ArchiveLimits.MaxBookmarkBytes)
                    throw new BackupOperationException("source-size", "A selected bookmark file exceeds 256 MiB.");
                bytes = new byte[checked((int)length)];
                for (var offset = 0; offset < bytes.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    var count = input.Read(bytes, offset, Math.Min(65536, bytes.Length - offset));
                    if (count == 0) throw new IOException("Source changed while reading.");
                    offset += count;
                }

                if (input.Length != length || File.GetLastWriteTimeUtc(path) != stamp || input.ReadByte() != -1)
                    throw new IOException("Source changed while reading.");
                BraveBookmarksParser.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'));
                return bytes;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                                              or DecoderFallbackException)
            {
                if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
                if (error is BackupOperationException) throw;
                if (attempt >= 2)
                    throw new BackupOperationException("source-unreadable",
                        "A selected profile's Bookmarks file could not be read as a valid snapshot. Refresh the profile list or close Brave and try again.");
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
            if (Directory.Exists(path))
                throw new BackupOperationException("destination-folder", "Choose a filename, not a folder.");
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

            return
                $"{input.Length}:{File.GetLastWriteTimeUtc(path).Ticks}:{Convert.ToHexString(hash.GetHashAndReset())}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }
}