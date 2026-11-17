using BraveBackup.Platform;

namespace BraveBackup.Core;

/// <summary>Safe standalone CSV completion metadata; contains no credential values.</summary>
public sealed record BravePasswordCsvExportResult(string Destination, int SourceLogins, int CredentialGroups,
    int CsvRows, int ExcludedEntries, long Bytes);

/// <summary>Explicit capture-to-confirmed-save operation for the researched CSV compatibility profile.</summary>
public static class BravePasswordCsvExport
{
    /// <summary>Checks only version metadata against the researched CSV profile; performs no source or credential access.</summary>
    public static bool SupportsVersion(string? value) => Version.TryParse(value, out var version) &&
        version.Major == 1 && version.Minor == 102 && version.Build == 13 && version.Revision <= 0;

    /// <summary>Captures supported credentials/cache, groups/projects them, warns freshly and atomically saves one plaintext CSV.</summary>
    /// <remarks>Real compatibility validation remains required; supported version identity is conservative, not proof of a binary's exact revision.</remarks>
    public static async Task<BravePasswordCsvExportResult> CreateAsync(BraveAppInfo installation, BraveProfileInfo profile,
        PasswordEncryptionProvider provider, string destination, bool csvExplicitlyChosen, PasswordCsvLineEnding lineEnding,
        Func<PasswordCsvExportWarning, CancellationToken, Task<bool>> confirmPlaintextSave,
        Func<ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? unlock = null,
        Func<ProcessPresence>? observeBrowserState = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(installation); ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(confirmPlaintextSave); token.ThrowIfCancellationRequested();
        if (!csvExplicitlyChosen)
            throw new BackupOperationException("passwords-csv-not-selected", "Explicitly choose plaintext password CSV before capture or saving.");
        if (!SupportsVersion(installation.Version))
            throw new BackupOperationException("passwords-csv-version-unsupported", "CSV projection currently supports only the researched Brave 1.102.13 version profile. Use a protected backup for other versions.");
        if (provider is not (PasswordEncryptionProvider.WindowsDpapiV10 or PasswordEncryptionProvider.LinuxKeyringV11))
            throw new ArgumentOutOfRangeException(nameof(provider));
        if (lineEnding is not (PasswordCsvLineEnding.LinuxLf or PasswordCsvLineEnding.WindowsCrLf))
            throw new ArgumentOutOfRangeException(nameof(lineEnding));
        var dataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installation.UserDataDirectory));
        var profileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.ProfileDirectory));
        if (!string.Equals(Path.GetDirectoryName(profileRoot), dataRoot, HostEnvironment.PathComparison))
            throw new BackupOperationException("passwords-csv-profile-scope", "The selected profile must belong to the selected installation's user-data directory.");
        string[] roots = [dataRoot, profileRoot];
        var target = ExportDestinationGuard.Validate(destination, roots);
        if (!string.Equals(Path.GetExtension(target), ".csv", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.GetDirectoryName(target)))
            throw new BackupOperationException("passwords-csv-destination", "Choose a CSV filename in an existing folder outside browser data.");
        using var context = await Task.Run(() => CaptureContext.Create(profileRoot, provider, unlock, observeBrowserState, token), token);
        token.ThrowIfCancellationRequested();
        var saved = await PasswordCsvFileExport.SaveAsync(context.Projection.Rows, lineEnding, target, true, roots,
            context.VerifySources, confirmPlaintextSave, token);
        return new BravePasswordCsvExportResult(saved.Destination, context.Projection.SourceLoginCount,
            context.Projection.CredentialGroupCount, saved.RowCount, context.ExcludedEntries, saved.Bytes);
    }

    private sealed class CaptureContext : IDisposable
    {
        private BravePasswordCapture? _passwords;
        private BraveAffiliationSnapshot? _affiliations;
        private PortablePasswordDocument? _document;
        private PasswordCsvProjection? _projection;
        public PasswordCsvProjection Projection => _projection!;
        public int ExcludedEntries { get; private set; }
        public static CaptureContext Create(string profile, PasswordEncryptionProvider provider,
            Func<ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? unlock,
            Func<ProcessPresence>? observe, CancellationToken token)
        {
            var result = new CaptureContext();
            try
            {
                result._passwords = BravePasswordCapture.Read(profile, provider, unlock, observe, token);
                result._document = BravePortablePasswords.Create(result._passwords, token);
                result.ExcludedEntries = result._document.Stores.Sum(store => store.BlockedEntries + store.FederatedEntries);
                if (result._document.Stores.Any(store => store.Logins.Count != 0))
                {
                    result._affiliations = BraveAffiliationSnapshot.Read(profile, observe, token);
                    if (result._passwords.RetainedSourceBytes + result._affiliations.RetainedSourceBytes > 128L * 1024 * 1024)
                        throw new BackupOperationException("passwords-csv-source-limit", "Selected CSV sources exceed the supported operation retention limit.");
                    var groups = BraveOfflineCsvGroups.Create(result._document, result._affiliations.Cache, token);
                    result._projection = BravePasswordCsvProjection.Create(result._document, groups.Resolve, token);
                }
                else result._projection = BravePasswordCsvProjection.Create(result._document, _ => null, token);
                result.VerifySources(); token.ThrowIfCancellationRequested(); return result;
            }
            catch { result.Dispose(); throw; }
        }
        public void VerifySources()
        {
            _passwords!.VerifyUnchanged(); _affiliations?.VerifyUnchanged(); _passwords.VerifyUnchanged();
        }
        public void Dispose() { _projection?.Dispose(); _document?.Dispose(); _affiliations?.Dispose(); _passwords?.Dispose(); }
    }
}
