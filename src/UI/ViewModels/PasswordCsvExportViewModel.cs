using System;
using System.Collections.Generic;
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

/// <summary>Headless standalone password CSV behavior; no credential preview, discovery read or automatic export occurs.</summary>
public partial class PasswordCsvExportViewModel : ViewModelBase, IDisposable
{
    private BraveAppInfo? _installation;
    private BraveProfileInfo? _profile;
    private bool _disposed;
    private CancellationTokenSource? _operation;
    private readonly Dictionary<string, string> _destinations = new(HostEnvironment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private Func<PasswordCsvExportWarning, CancellationToken, Task<bool>>? _confirmation;
    public IReadOnlyList<PasswordSourceOption> PasswordSources { get; } = new BackupOptionsViewModel().PasswordSources;
    [ObservableProperty] private PasswordSourceOption? _selectedPasswordSource;
    [ObservableProperty] private string _destination = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private string _report = string.Empty;
    [ObservableProperty] private string _completedDestination = string.Empty;
    public string FullDestination
    {
        get { try { return string.IsNullOrWhiteSpace(Destination) ? "Choose a CSV destination" : Path.GetFullPath(Destination); } catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return "Invalid CSV destination"; } }
    }
    public Func<ProcessPresence>? ObserveBrowserState { get; set; }
    public Func<ReadOnlyMemory<byte>, CancellationToken, PasswordKeyMaterial>? Unlock { get; set; }
    /// <summary>Host dialog callback; no callback means the action stays unavailable, never implied consent.</summary>
    public Func<PasswordCsvExportWarning, CancellationToken, Task<bool>>? ConfirmPlaintextSave
    {
        get => _confirmation;
        set { _confirmation = value; NotifyAvailability(); }
    }
    public string Availability => IsBusy ? "Password CSV export is in progress." :
        _installation is null || _profile is null ? "Select an installation and one profile." :
        SelectedPasswordSource is null || !PasswordSources.Contains(SelectedPasswordSource) ? "Choose a supported Password source." :
        !BravePasswordCsvExport.SupportsVersion(_installation.Version) ? "CSV currently supports only the researched Brave 1.102.13 version profile; protected backup is available separately." :
        !string.IsNullOrEmpty(DestinationValidation) ? DestinationValidation :
        _confirmation is null ? "The plaintext-save confirmation dialog is unavailable." :
        "Ready to export plaintext CSV after a fresh destination-specific confirmation.";
    private string DestinationValidation
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Destination)) return "Choose a CSV destination.";
            try
            {
                var path = ExportDestinationGuard.Validate(Destination, _installation is null ? [] : [_installation.UserDataDirectory, _profile!.ProfileDirectory]);
                if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(Path.GetDirectoryName(path)))
                    return "Choose a CSV filename in an existing folder outside browser data.";
                return string.Empty;
            }
            catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException or InvalidOperationException or UnauthorizedAccessException)
            { return "Choose a safe CSV destination outside browser data."; }
        }
    }

    /// <summary>Changes metadata context only; keeps each profile's destination and never reads its saved credentials.</summary>
    public void SetContext(BraveAppInfo? installation, BraveProfileInfo? profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsBusy) throw new InvalidOperationException("Cannot change the profile during CSV export.");
        if (_profile is not null) _destinations[Path.GetFullPath(_profile.ProfileDirectory)] = Destination;
        var installationChanged = !Equals(_installation, installation);
        _installation = installation; _profile = profile;
        if (installationChanged) SelectedPasswordSource = null;
        Destination = profile is not null && _destinations.TryGetValue(Path.GetFullPath(profile.ProfileDirectory), out var previous) ? previous : string.Empty;
        Status = string.Empty; Report = string.Empty; Succeeded = false; CompletedDestination = string.Empty;
        NotifyAvailability();
    }
    partial void OnDestinationChanged(string value) { OnPropertyChanged(nameof(FullDestination)); NotifyAvailability(); }
    partial void OnSelectedPasswordSourceChanged(PasswordSourceOption? value) => NotifyAvailability();
    partial void OnIsBusyChanged(bool value) { NotifyAvailability(); CancelCommand.NotifyCanExecuteChanged(); }
    private void NotifyAvailability() { OnPropertyChanged(nameof(Availability)); ExportCsvCommand.NotifyCanExecuteChanged(); }
    private bool CanExportCsv() => !_disposed && !IsBusy && _installation is not null && _profile is not null &&
        SelectedPasswordSource is not null && PasswordSources.Contains(SelectedPasswordSource) &&
        BravePasswordCsvExport.SupportsVersion(_installation.Version) && string.IsNullOrEmpty(DestinationValidation) && _confirmation is not null;

    /// <summary>Invoking the dedicated CSV action is the explicit format choice; the separate warning must still be approved.</summary>
    [RelayCommand(CanExecute = nameof(CanExportCsv))]
    public async Task ExportCsvAsync()
    {
        if (!CanExportCsv()) return;
        var installation = _installation!; var profile = _profile!; var provider = SelectedPasswordSource!.Provider;
        var destination = Path.GetFullPath(Destination); var confirm = _confirmation!; var observe = ObserveBrowserState; var unlock = Unlock;
        var operation = new CancellationTokenSource(); _operation = operation;
        Succeeded = false; Report = string.Empty; CompletedDestination = string.Empty; Status = "Capturing supported local passwords and grouping data…"; IsBusy = true;
        try
        {
            var result = await BravePasswordCsvExport.CreateAsync(installation, profile, provider, destination, true,
                HostEnvironment.IsWindows ? PasswordCsvLineEnding.WindowsCrLf : PasswordCsvLineEnding.LinuxLf, confirm, unlock, observe, operation.Token);
            if (_disposed) return;
            Succeeded = true; CompletedDestination = result.Destination; Status = "Plaintext password CSV saved after confirmation.";
            Report = $"Source logins: {result.SourceLogins}; credential groups: {result.CredentialGroups}; CSV rows: {result.CsvRows}; excluded entries: {result.ExcludedEntries}; bytes: {result.Bytes}";
        }
        catch (OperationCanceledException) { if (!_disposed) Status = "Password CSV export cancelled. No new file was published."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or CryptographicException)
        { if (!_disposed) Status = error is BackupOperationException known ? known.Message : "Password CSV export failed. No new file was published."; }
        finally { if (ReferenceEquals(_operation, operation)) _operation = null; operation.Dispose(); IsBusy = false; }
    }
    private bool CanCancel() => !_disposed && IsBusy;
    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _operation?.Cancel();
    /// <summary>Cancels in-flight work; its operation finally block disposes resources after cancellation propagates.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _operation?.Cancel(); NotifyAvailability(); CancelCommand.NotifyCanExecuteChanged(); }
}
