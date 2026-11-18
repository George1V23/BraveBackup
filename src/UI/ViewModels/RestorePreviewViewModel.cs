using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>Value-free category inventory. Capability here describes inspection, never permission to restore.</summary>
public sealed record RestoreCategoryPreview(string Label, string Description);
/// <summary>Archived profile metadata with read-only category inventory.</summary>
public sealed record RestoreProfilePreview(string Name, string DirectoryName, RestoreCategoryPreview[] Categories);
/// <summary>Archived installation metadata; never used to infer a destination.</summary>
public sealed record RestoreInstallationPreview(string Name, RestoreProfilePreview[] Profiles);

/// <summary>Owns read-only inspection requests and discards stale results and secret form text.</summary>
public partial class RestorePreviewViewModel : ViewModelBase, IDisposable
{
    private readonly Func<string, string?, CancellationToken, BraveArchiveInspectionResult> _inspect;
    private CancellationTokenSource? _inspection;
    private long _generation;
    private bool _disposed;
    /// <summary>Native local-open picker supplied by the window; choosing does not write files.</summary>
    public Func<Task<string?>>? PickSource { get; set; }
    /// <summary>Independent read-only source/target plan; no execution capability is exposed.</summary>
    public RestorePlanningViewModel Planning { get; } = new();
    /// <summary>Independent safety password, never reused from archive unlock.</summary>
    public RestoreSafetyOptionsViewModel Safety { get; } = new();
    public ObservableCollection<RestoreInstallationPreview> Installations { get; } = [];
    [ObservableProperty] private string _sourcePath = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _needsUnlock;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Choose a backup to inspect. No browser files will be changed.";
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Creates an inspection-only model; an injected reader allows deterministic cancellation/race tests.</summary>
    public RestorePreviewViewModel(Func<string, string?, CancellationToken, BraveArchiveInspectionResult>? inspect = null)
    {
        _inspect = inspect ?? BraveArchiveInspection.Read;
        Planning.ReviewInputsChanged += InvalidateReview;
        Safety.PropertyChanged += OnSafetySettingsChanged;
    }

    /// <summary>Resolved display path; invalid input is left visible for correction without file access.</summary>
    public string FullSourcePath
    {
        get { try { return string.IsNullOrWhiteSpace(SourcePath) ? string.Empty : Path.GetFullPath(SourcePath); } catch { return SourcePath; } }
    }
    partial void OnSourcePathChanged(string value) { OnPropertyChanged(nameof(FullSourcePath)); Password = string.Empty; _ = InspectAsync(false); }
    partial void OnPasswordChanged(string value) => UnlockCommand.NotifyCanExecuteChanged();
    partial void OnNeedsUnlockChanged(bool value) => UnlockCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        UnlockCommand.NotifyCanExecuteChanged();
        ReviewRestoreCommand.NotifyCanExecuteChanged();
    }
    private bool CanUnlock() => !_disposed && NeedsUnlock && !IsBusy && Password.Length != 0;

    /// <summary>Opens the native source picker without writing files; ignores results after context/lifetime changes.</summary>
    [RelayCommand]
    public async Task BrowseAsync()
    {
        if (_disposed || PickSource is null) return;
        var generation = _generation;
        try
        {
            var selected = await PickSource();
            if (!_disposed && generation == _generation && !string.IsNullOrWhiteSpace(selected))
            {
                if (SourcePath == selected) await InspectAsync(false);
                else SourcePath = selected;
            }
        }
        catch { if (!_disposed && generation == _generation) Status = "The backup file picker could not be opened."; }
    }

    /// <summary>Validates the selected encrypted archive using only the current explicit unlock request.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlock))]
    public Task UnlockAsync() => CanUnlock() ? InspectAsync(true) : Task.CompletedTask;

    /// <summary>Refreshes header/preview state when entering Restore; does not reuse an unlock password.</summary>
    public Task EnterAsync() => InspectAsync(false);

    private async Task InspectAsync(bool unlock)
    {
        if (_disposed) return;
        _inspection?.Cancel();
        var generation = ++_generation;
        var source = SourcePath;
        var secret = unlock ? Password : null;
        Password = string.Empty; Summary = string.Empty; Installations.Clear(); Planning.Reset(); Safety.ClearSecrets(); NeedsUnlock = false;
        if (string.IsNullOrWhiteSpace(source)) { IsBusy = false; Status = "Choose a backup to inspect. No browser files will be changed."; return; }
        using var cancellation = new CancellationTokenSource();
        _inspection = cancellation;
        IsBusy = true; Status = "Validating backup contents…";
        try
        {
            var result = await Task.Run(() => _inspect(source, secret, cancellation.Token), cancellation.Token);
            if (_disposed || generation != _generation) return;
            NeedsUnlock = result.Encrypted;
            if (result.Manifest is not { } manifest) { Status = "Encrypted backup. Enter its password and choose Unlock backup."; return; }
            foreach (var installation in manifest.Installations)
                Installations.Add(new($"{installation.Product} {installation.Channel} — {installation.BrowserVersion}",
                    installation.Profiles.Select(profile => new RestoreProfilePreview(profile.DisplayName, profile.DirectoryName,
                        profile.Categories.Select(Category).ToArray())).ToArray()));
            Planning.LoadInspected(result);
            Summary = $"Created (UTC): {manifest.CreatedUtc:O}\nApplication version: {manifest.ApplicationVersion}\nArchive format: {manifest.FormatVersion}\nProtection: {(result.Encrypted ? "Encrypted" : "Unencrypted")}";
            NeedsUnlock = false;
            Status = "Backup validated. Select the archived profiles and destination profiles to review.";
        }
        catch (OperationCanceledException) { if (!_disposed && generation == _generation) Status = "Inspection cancelled."; }
        catch
        {
            if (!_disposed && generation == _generation) { NeedsUnlock = unlock; Status = "Unable to validate backup. Check the file, supported archive format and password. No browser files were changed."; }
        }
        finally
        {
            secret = null;
            if (ReferenceEquals(_inspection, cancellation)) _inspection = null;
            if (!_disposed && generation == _generation) IsBusy = false;
        }
    }

    private static RestoreCategoryPreview Category(ArchiveCategory category)
    {
        var representation = category.Id == "passwords" ? category.Files.Any(file => file.Role == "plaintext-password-csv")
            ? "CSV passwords; readable after extraction. Destination compatibility must be verified."
            : category.Files.Any(file => file.Role == "raw-password-index")
                ? "Raw password files; portability and recoverability are not guaranteed."
                : "Portable password payload; destination encryption/provider compatibility must be verified."
            : "Included in backup. Restore compatibility is not established by this preview.";
        return new(category.Id + " (category v" + category.Version + ")", representation);
    }

    /// <summary>Cancels pending inspection and clears preview/password state when leaving Restore.</summary>
    public void Leave()
    {
        ++_generation; _inspection?.Cancel(); _inspection = null;
        Password = string.Empty; Summary = string.Empty; Installations.Clear(); Planning.Reset(); Safety.ClearSecrets(); NeedsUnlock = false; IsBusy = false;
        Status = "Choose a backup to inspect. No browser files will be changed.";
    }
    /// <summary>Releases callbacks and invalidates pending results; each request disposes its own cancellation source.</summary>
    public void Dispose() { if (_disposed) return; Leave(); _disposed = true; Planning.ReviewInputsChanged -= InvalidateReview; Safety.PropertyChanged -= OnSafetySettingsChanged; Planning.Dispose(); Safety.Dispose(); PickSource = null; UnlockCommand.NotifyCanExecuteChanged(); }
}
