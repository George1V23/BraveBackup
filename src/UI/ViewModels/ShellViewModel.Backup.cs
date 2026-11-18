using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BraveBackup.Core;
using BraveBackup.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

public partial class ShellViewModel
{
    private CancellationTokenSource? _backupCancellation;
    private string? _completedBackupPath;

    /// <summary>Gets or sets the host's explicit overwrite confirmation; no callback means replacement is declined.</summary>
    public Func<string, Task<bool>>? ConfirmBackupOverwrite { get; set; }

    /// <summary>Gets or sets explicit per-operation confirmation before saving raw extension data unencrypted.</summary>
    public Func<string, Task<bool>>? ConfirmUnencryptedExtensionData { get; set; }

    /// <summary>Gets or sets controlled process observation for alternate hosts/tests; null uses the actual host.</summary>
    public Func<ProcessPresence>? ExtensionBrowserState { get; set; }

    /// <summary>Gets or sets explicit confirmation before each unencrypted native-session save.</summary>
    public Func<string, Task<bool>>? ConfirmUnencryptedNativeSessions { get; set; }

    /// <summary>Gets per-save confirmation that unencrypted saved windows expose URLs, titles and private records.</summary>
    public Func<string, Task<bool>>? ConfirmUnencryptedTabManagerPlus { get; set; }

    /// <summary>Fresh raw-or-cancel choice. Called from the archive worker; the host must dispatch its UI.</summary>
    public Func<PasswordRawFallbackWarning, CancellationToken, Task<bool>>? ConfirmRawPasswordPreservation { get; set; }
    private Func<PasswordCsvArchiveWarning, CancellationToken, Task<bool>>? _confirmPasswordCsvArchiveSave;
    /// <summary>Fresh destination-specific approval for CSV entries, including encrypted archives. Missing host consent blocks CSV.</summary>
    public Func<PasswordCsvArchiveWarning, CancellationToken, Task<bool>>? ConfirmPasswordCsvArchiveSave
    {
        get => _confirmPasswordCsvArchiveSave;
        set { _confirmPasswordCsvArchiveSave = value; NotifyBackupValidation(); }
    }
    /// <summary>Controlled host/test observation; null uses the native browser process probe.</summary>
    public Func<ProcessPresence>? PasswordBrowserState { get; set; }
    /// <summary>Optional host/test owned-key adapter; the UI's default uses native key access only on an explicit operation.</summary>
    public Func<BraveProfileInfo, ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? PasswordKeyUnlocker { get; set; }

    /// <summary>Gets whether one frozen archive request is currently running.</summary>
    [ObservableProperty] private bool _isBackupRunning;

    /// <summary>Gets actual stage/profile progress; no estimated byte percentage is fabricated.</summary>
    [ObservableProperty] private string _backupStage = string.Empty;

    /// <summary>Gets the current outcome or actionable validation error.</summary>
    [ObservableProperty] private string _backupMessage = string.Empty;

    /// <summary>Gets a report without credentials, bookmark contents, paths or profile names.</summary>
    [ObservableProperty] private string _backupReport = string.Empty;

    /// <summary>Gets whether the in-app report is expanded.</summary>
    [ObservableProperty] private bool _showBackupReport;

    /// <summary>Gets whether the latest operation published a verified archive.</summary>
    [ObservableProperty] private bool _backupSucceeded;

    /// <summary>Gets whether profile/navigation controls may change while no backup is running.</summary>
    public bool IsWorkspaceEnabled => !IsRefreshing && !IsBackupRunning && !PasswordCsvExport.IsBusy && !ExportViewModel.IsBusy;

    /// <summary>Gets whether operation feedback is present.</summary>
    public bool HasBackupFeedback => !string.IsNullOrEmpty(BackupMessage);

    /// <summary>Gets whether a sanitized report is available.</summary>
    public bool HasBackupReport => !string.IsNullOrEmpty(BackupReport);

    /// <summary>Gets the validation result for the supported-category archive action.</summary>
    public string BackupAvailability => IsBackupRunning
        ? "Backup in progress; the current selection is frozen."
        : !HasBackupScope || !BackupInstallations.Any(item => item.Profiles.Any(profile => profile.IsSelected))
            ? "Select at least one profile for backup."
            : !Options.Categories.Any(item => item.IsSelected)
                ? "Select supported Bookmarks, Settings, Extensions, Sessions and/or TabManagerPlus."
                : !string.IsNullOrEmpty(Options.PasswordModeValidation)
                    ? Options.PasswordModeValidation
                : !string.IsNullOrEmpty(PasswordCsvArchiveValidation)
                    ? PasswordCsvArchiveValidation
                : Options.Categories.Any(item =>
                    item.IsSelected &&
                    item.Id is not ("bookmarks" or "settings" or "extensions" or "sessions" or "tabmanagerplus" or "passwords"))
                    ? "Bookmarks, Settings, Extensions, persisted Sessions and supported TabManagerPlus saved windows are available. Deselect other categories to create an archive."
                    : Options.EncryptBackup && !string.IsNullOrEmpty(Options.PasswordValidation)
                        ? Options.PasswordValidation
                        : !string.IsNullOrEmpty(BackupDestinationValidation)
                            ? BackupDestinationValidation
                            : "Ready to create a backup. Passwords, Extensions and TabManagerPlus require Brave closed. Restore is not available yet.";

    /// <summary>Checks metadata and host consent availability only; never probes password sources.</summary>
    private string PasswordCsvArchiveValidation => !Options.PasswordsSelected || Options.ProtectPasswords ? string.Empty
        : BackupInstallations.Any(item => item.Profiles.Any(profile => profile.IsSelected) &&
            !BravePasswordCsvExport.SupportsVersion(item.AppInfo.Version))
            ? "Password CSV backup supports only the researched Brave 1.102.13 version profile. Use Protect passwords for other versions."
        : ConfirmPasswordCsvArchiveSave is null ? "A plaintext password CSV confirmation dialog is required before backup."
        : string.Empty;

    private string BackupDestinationValidation
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Options.BackupPath)) return "Choose a .bravebackup destination.";
            try
            {
                var path = Path.GetFullPath(Options.BackupPath);
                if (!string.Equals(Path.GetExtension(path), ".bravebackup", StringComparison.OrdinalIgnoreCase))
                    return "Choose a destination ending in .bravebackup.";
                return Directory.Exists(path) ? "Choose a filename, not a folder." : string.Empty;
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
            {
                return "Enter a valid .bravebackup destination path.";
            }
        }
    }

    private bool CanCreateBackup() => !_disposed && IsWorkspaceEnabled &&
                                      BackupInstallations.Any(item =>
                                          item.Profiles.Any(profile => profile.IsSelected)) &&
                                      Options.Categories.Any(item => item.IsSelected) &&
                                      Options.Categories.All(item =>
                                          !item.IsSelected ||
                                          item.Id is "bookmarks" or "settings" or "extensions" or "sessions"
                                              or "tabmanagerplus" or "passwords") &&
                                      string.IsNullOrEmpty(Options.PasswordModeValidation) &&
                                      string.IsNullOrEmpty(PasswordCsvArchiveValidation) &&
                                      (!Options.EncryptBackup || string.IsNullOrEmpty(Options.PasswordValidation)) &&
                                      string.IsNullOrEmpty(BackupDestinationValidation);

    private void NotifyBackupValidation()
    {
        OnPropertyChanged(nameof(BackupAvailability));
        CreateBackupCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Creates a verified supported-category archive from an immutable copy of the current form choices.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateBackup))]
    public async Task CreateBackupAsync()
    {
        if (!CanCreateBackup()) return;
        var selections = BackupInstallations.Select(item => new BraveBackupSelection(item.AppInfo,
                item.Profiles.Where(profile => profile.IsSelected).Select(profile => profile.ProfileInfo).ToArray()))
            .Where(item => item.Profiles.Count > 0).ToArray();
        var categories = Options.Categories.Where(item => item.IsSelected).Select(item => item.Id).ToArray();
        var includeNativeSessions = Options.IncludeNativeSessionFiles;
        var destination = Options.BackupPath;
        var password = Options.EncryptBackup ? Options.Password : null;
        var selectedPasswordSource = Options.SelectedPasswordSource;
        var protectPasswords = Options.ProtectPasswords;
        var rawChoice = ConfirmRawPasswordPreservation;
        var csvChoice = ConfirmPasswordCsvArchiveSave;
        var passwordObserver = PasswordBrowserState;
        var passwordUnlocker = PasswordKeyUnlocker;
        var passwordOptions = categories.Contains("passwords") ? new PasswordBackupOptions
        {
            ProtectPasswords = protectPasswords,
            ConfirmCsvArchiveSave = csvChoice,
            CsvLineEnding = HostEnvironment.IsWindows ? PasswordCsvLineEnding.WindowsCrLf : PasswordCsvLineEnding.LinuxLf,
            GetProvider = (_, _) => selectedPasswordSource!.Provider,
            ObserveBrowserState = passwordObserver,
            Unlock = passwordUnlocker,
            ChooseRawPreservation = rawChoice
        } : null;
        var compression = Options.Compression.ToLowerInvariant();
        var roots = Installations.Select(item => item.AppInfo.UserDataDirectory).ToArray();
        var version = typeof(ShellViewModel).Assembly.GetName().Version?.ToString() ?? "Unknown";
        var start = DateTimeOffset.UtcNow;
        _backupCancellation = new CancellationTokenSource();
        var operationCancellation = _backupCancellation;
        var token = operationCancellation.Token;
        IsBackupRunning = true;
        BackupSucceeded = false;
        _completedBackupPath = null;
        BackupMessage = string.Empty;
        BackupReport = string.Empty;
        ShowBackupReport = false;
        BackupStage = "Validating";
        var progress = new Progress<BackupProgress>(value =>
        {
            if (!_disposed && IsBackupRunning && ReferenceEquals(_backupCancellation, operationCancellation))
                BackupStage = $"{value.Stage} · profiles {value.CompletedProfiles}/{value.TotalProfiles}";
        });
        try
        {
            var result = await BraveArchiveBackup.CreateAsync(selections, categories, destination, password,
                compression, roots, version,
                path => ConfirmBackupOverwrite?.Invoke(path) ?? Task.FromResult(false), progress, token,
                new ExtensionBackupOptions
                {
                    ObserveBrowserState = ExtensionBrowserState,
                    ConfirmUnencryptedData = path =>
                        ConfirmUnencryptedExtensionData?.Invoke(path) ?? Task.FromResult(false)
                }, new SessionBackupOptions
                {
                    IncludeNativeFiles = includeNativeSessions,
                    ConfirmUnencryptedNativeFiles = path =>
                        ConfirmUnencryptedNativeSessions?.Invoke(path) ?? Task.FromResult(false)
                }, new TabManagerPlusBackupOptions
                {
                    ObserveBrowserState = ExtensionBrowserState,
                    ConfirmUnencryptedData = path =>
                        ConfirmUnencryptedTabManagerPlus?.Invoke(path) ?? Task.FromResult(false)
                }, passwordOptions);
            if (_disposed) return;
            _completedBackupPath = result.Destination;
            BackupSucceeded = true;
            BackupMessage = $"Backup created and verified: {result.Destination} ({result.Bytes:N0} bytes)";
            if (categories.Contains("settings"))
                BackupMessage += "\n" + result.SettingsSummary + "\nOnly the supported stored settings are included.";
            if (categories.Contains("extensions"))
                BackupMessage += "\n" + result.ExtensionsSummary +
                                 "\nRaw files were saved; extensions have not been installed or restored.";
            if (categories.Contains("passwords")) BackupMessage += "\n" + result.PasswordsSummary;
            if (categories.Contains("sessions")) BackupMessage += "\n" + result.SessionsSummary;
            if (categories.Contains("tabmanagerplus")) BackupMessage += "\n" + result.TabManagerPlusSummary;
            BackupReport = result.Report;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed)
            {
                BackupMessage = "Backup cancelled. No new archive was published.";
                BackupReport = FailureReport(start, "Cancelled", "cancelled",
                    selections.Sum(item => item.Profiles.Count), categories);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
                                          or ArgumentException or
                                          InvalidOperationException or CryptographicException
                                          or System.Text.Json.JsonException)
        {
            if (!_disposed)
            {
                var code = error is BackupOperationException operation ? operation.Code :
                    error is UnauthorizedAccessException ? "access-denied" :
                    error is CryptographicException ? "authentication-failed" : "backup-failed";
                BackupMessage = error is BackupOperationException known
                    ? known.Message
                    : "Backup could not be completed. Check destination permissions, free disk space and profile availability, then try again.";
                BackupReport = FailureReport(start, "Failed", code, selections.Sum(item => item.Profiles.Count),
                    categories);
            }
        }
        finally
        {
            password = null;
            Options.ClearSecrets();
            _backupCancellation.Dispose();
            _backupCancellation = null;
            IsBackupRunning = false;
        }
    }

    /// <summary>Requests cancellation before publication; the writer removes incomplete output.</summary>
    [RelayCommand]
    public void CancelBackup() => _backupCancellation?.Cancel();

    /// <summary>Shows or hides the latest sanitized operation report within the application.</summary>
    [RelayCommand]
    public void ToggleBackupReport() => ShowBackupReport = !ShowBackupReport;

    /// <summary>Opens the actual completed archive's folder through Platform.</summary>
    [RelayCommand]
    public void OpenBackupFolder()
    {
        if (!BackupSucceeded || _completedBackupPath == null) return;
        try
        {
            BackupFileOperations.OpenFolder(Path.GetDirectoryName(_completedBackupPath)!);
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception
                                          or UnauthorizedAccessException)
        {
            BackupMessage = "The backup was saved, but its destination folder could not be opened.";
        }
    }

    private static string FailureReport(DateTimeOffset start, string state, string code, int requestedProfiles,
        string[] categories) =>
        $"Started (UTC): {start:O}\nEnded (UTC): {DateTimeOffset.UtcNow:O}\nStatus: {state}\nError: {code}\nRequested profiles: {requestedProfiles}\nRequested categories: {string.Join(", ", categories)}\nPublished: no";

    partial void OnIsBackupRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowBackupActions));
        NotifyExportAvailability();
    }

    partial void OnBackupStageChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnBackupMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasBackupFeedback));
        OnPropertyChanged(nameof(ShowBackupActions));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnBackupReportChanged(string value) => OnPropertyChanged(nameof(HasBackupReport));
}