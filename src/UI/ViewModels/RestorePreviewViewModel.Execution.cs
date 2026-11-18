using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BraveBackup.Core;
using BraveBackup.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

public partial class RestorePreviewViewModel
{
    private string? _sourceSecret;
    private LinuxRestoreSession? _restoreSession;
    private RestoreReview? _executionReview;
    private CancellationTokenSource? _pauseSignal;
    private bool _closingExecution;
    private bool _restoreAvailable = true;
    private int _progressRevision;

    [ObservableProperty] private bool _isExecutingRestore;
    [ObservableProperty] private bool _isRestorePaused;
    [ObservableProperty] private bool _isRollbackRunning;
    [ObservableProperty] private bool _isRollbackPromptVisible;
    [ObservableProperty] private bool _recoveryAvailable;
    [ObservableProperty] private string _restoreStatus = string.Empty;
    [ObservableProperty] private string _restoreReport = string.Empty;
    [ObservableProperty] private string _restoreCounts = string.Empty;
    [ObservableProperty] private string _restoreErrorCode = string.Empty;
    [ObservableProperty] private string _publishedSafetyPath = string.Empty;
    [ObservableProperty] private double _restoreProgress;
    [ObservableProperty] private bool _hasProgressTotal;

    public bool IsRestoreActive => IsExecutingRestore || IsRestorePaused;
    public bool ShowCancelRestore => IsExecutingRestore && !IsRollbackRunning;
    public bool CanEditRestore => !IsRestoreActive;
    public bool HasRestoreOutcome => !string.IsNullOrEmpty(RestoreStatus);
    public bool HasPublishedSafety => !string.IsNullOrEmpty(PublishedSafetyPath);

    internal void SetRestoreAvailability(bool available)
    {
        _restoreAvailable = available;
        ConfirmRestoreCommand.NotifyCanExecuteChanged();
    }

    partial void OnRestoreStatusChanged(string value) => OnPropertyChanged(nameof(HasRestoreOutcome));
    partial void OnPublishedSafetyPathChanged(string value)
    {
        OnPropertyChanged(nameof(HasPublishedSafety));
        OpenSafetyFolderCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsExecutingRestoreChanged(bool value) => NotifyExecution();
    partial void OnIsRestorePausedChanged(bool value) => NotifyExecution();
    partial void OnIsRollbackRunningChanged(bool value) => NotifyExecution();
    partial void OnIsRollbackPromptVisibleChanged(bool value) => NotifyExecution();
    partial void OnRecoveryAvailableChanged(bool value) => NotifyExecution();

    private void NotifyExecution()
    {
        OnPropertyChanged(nameof(IsRestoreActive));
        OnPropertyChanged(nameof(CanEditRestore));
        OnPropertyChanged(nameof(ShowCancelRestore));
        ReviewRestoreCommand.NotifyCanExecuteChanged();
        ConfirmRestoreCommand.NotifyCanExecuteChanged();
        CancelRestoreCommand.NotifyCanExecuteChanged();
        ResumeRestoreCommand.NotifyCanExecuteChanged();
        OfferRollbackCommand.NotifyCanExecuteChanged();
        ConfirmRollbackCommand.NotifyCanExecuteChanged();
    }

    private bool CanConfirmRestore() => !_disposed && _restoreAvailable && !IsRestoreActive && !IsBusy &&
        Review is not null && Planning.ReadyForReview && Safety.ValidationError is null;

    /// <summary>Starts the frozen plan only after explicit confirmation. Safety data is verified before the first write.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmRestore))]
    private async Task ConfirmRestoreAsync()
    {
        if (!CanConfirmRestore() || Review is not { } review) return;
        var password = Safety.Password;
        var sourcePassword = _sourceSecret;
        var roots = Planning.KnownBrowserRoots.ToArray();
        var observer = Planning.ObserveBrowserState;
        ClearRestoreSession();
        _executionReview = review;
        IsExecutingRestore = true;
        RestoreStatus = "Checking the archive and preparing the encrypted safety backup…";
        Safety.ClearSecrets();
        using var pause = new CancellationTokenSource();
        _pauseSignal = pause;
        CancelRestoreCommand.NotifyCanExecuteChanged();
        var progress = CreateProgress(review);
        LinuxRestoreSession? opened = null;
        try
        {
            await Task.Run(() =>
            {
                Func<ProcessPresence>? observe = observer is null ? null : () =>
                {
                    var states = review.Targets.Select(mapping => mapping.Installation).Distinct().Select(observer).ToArray();
                    if (states.Contains(ProcessPresence.Running)) return ProcessPresence.Running;
                    return states.All(state => state == ProcessPresence.Stopped) ? ProcessPresence.Stopped : ProcessPresence.Unknown;
                };
                opened = BraveProfileRestorePlan.Open(review.ArchivePath, review.ArchiveId, sourcePassword,
                    Path.GetDirectoryName(review.SafetyPath)!, password,
                    review.Targets.Select(mapping => new BraveRestoreMapping(mapping.SourceProfileId,
                        mapping.Installation, mapping.Profile, mapping.Category) { Extension = mapping.Extension }).ToArray(),
                    roots, review.OperationId, review.SourceSha256, observe, pause.Token);
                opened.Transaction.Progress = progress.Report;
                opened.Transaction.Prepare(pause.Token);
                opened.Transaction.Run(pause.Token);
            });
            _restoreSession = opened;
            ReadOutcome();
        }
        catch (OperationCanceledException)
        {
            _restoreSession = opened;
            ReadOutcome();
            RestoreStatus = "Restore cancelled during preparation.";
        }
        catch (Exception error) when (IsRestoreError(error))
        {
            _restoreSession = opened;
            ReadOutcome();
            RestoreStatus = RestoreFailureMessage(error);
            RestoreErrorCode = RestoreFailureCode(error);
        }
        finally
        {
            _pauseSignal = null;
            IsExecutingRestore = false;
            if (_closingExecution)
            {
                IsRestorePaused = false;
                ClearRestoreSession();
            }
        }
    }

    private bool CanCancelRestore() => IsExecutingRestore && !IsRollbackRunning && _pauseSignal is { IsCancellationRequested: false };

    /// <summary>Requests a pause at the next safe write boundary or cancels preparation.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelRestore))]
    private void CancelRestore()
    {
        if (!CanCancelRestore()) return;
        _pauseSignal!.Cancel();
        RestoreStatus = "Waiting for a safe pause…";
        CancelRestoreCommand.NotifyCanExecuteChanged();
    }

    private bool CanResumeRestore() => IsRestorePaused && !IsExecutingRestore && !IsRollbackPromptVisible && _restoreSession is not null;

    /// <summary>Resumes the retained transaction after another source and destination check.</summary>
    [RelayCommand(CanExecute = nameof(CanResumeRestore))]
    private Task ResumeRestoreAsync() => CanResumeRestore() ? RunRetainedAsync(rollback: false) : Task.CompletedTask;

    private bool CanOfferRollback() => RecoveryAvailable && !IsExecutingRestore && !IsRollbackPromptVisible;

    /// <summary>Shows the explicit recovery decision for a paused or failed restore.</summary>
    [RelayCommand(CanExecute = nameof(CanOfferRollback))]
    private void OfferRollback()
    {
        if (CanOfferRollback()) IsRollbackPromptVisible = true;
    }

    /// <summary>Closes the recovery prompt. A paused restore remains paused.</summary>
    [RelayCommand]
    private void DeclineRollback() => IsRollbackPromptVisible = false;

    private bool CanConfirmRollback() => IsRollbackPromptVisible && RecoveryAvailable && !IsExecutingRestore;

    /// <summary>Recovers affected files from the original verified safety image.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmRollback))]
    private Task ConfirmRollbackAsync() => CanConfirmRollback() ? RunRetainedAsync(rollback: true) : Task.CompletedTask;

    private async Task RunRetainedAsync(bool rollback)
    {
        if (_restoreSession is not { } session || _executionReview is not { } review) return;
        IsExecutingRestore = true;
        IsRollbackRunning = rollback;
        IsRollbackPromptVisible = false;
        HasProgressTotal = false;
        RestoreErrorCode = string.Empty;
        RestoreStatus = rollback ? "Verifying safety backup and rolling back…" : "Rechecking the paused restore…";
        using var pause = new CancellationTokenSource();
        _pauseSignal = rollback ? null : pause;
        CancelRestoreCommand.NotifyCanExecuteChanged();
        var progress = CreateProgress(review);
        try
        {
            await Task.Run(() =>
            {
                session.Transaction.Progress = progress.Report;
                if (rollback) session.Transaction.Rollback(true);
                else
                {
                    session.VerifyApplyRegistrations(CancellationToken.None);
                    session.Transaction.Run(pause.Token);
                }
            });
            ReadOutcome();
        }
        catch (Exception error) when (IsRestoreError(error))
        {
            ReadOutcome();
            RestoreStatus = rollback
                ? "Rollback stopped. Check that Brave is closed and the targets and safety backup are available."
                : error is BackupOperationException { Code: "restore-extension-registration" }
                    ? "The reviewed extension registration changed. Restore the previous package registration or roll back."
                    : "Restore could not resume. Check the original archive, the destinations, and Brave's status.";
            RestoreErrorCode = RestoreFailureCode(error);
        }
        finally
        {
            _pauseSignal = null;
            IsRollbackRunning = false;
            IsExecutingRestore = false;
            if (_closingExecution)
            {
                IsRestorePaused = false;
                ClearRestoreSession();
            }
        }
    }

    private IProgress<RestoreTransactionProgress> CreateProgress(RestoreReview review)
    {
        var revision = ++_progressRevision;
        return new Progress<RestoreTransactionProgress>(progress =>
        {
            if (!IsExecutingRestore || revision != _progressRevision || _closingExecution) return;
            PublishedSafetyPath = review.SafetyPath;
            HasProgressTotal = progress.TotalFiles != 0;
            RestoreProgress = progress.TotalFiles == 0 ? 0 : 100.0 * progress.CompletedFiles / progress.TotalFiles;
            RestoreCounts = $"{progress.CompletedFiles} of {progress.TotalFiles} targets checked and complete for this stage";
            if (progress.State == RestoreTransactionState.Running && _pauseSignal?.IsCancellationRequested == false)
                RestoreStatus = "Restoring selected data…";
            if (progress.CategoryCompleted && review.Targets.FirstOrDefault(row => row.SourceProfileId == progress.ProfileId && row.Category == progress.Category) is { } row)
            {
                var action = progress.State == RestoreTransactionState.RollingBack ? "Rollback complete" : "Restored";
                RestoreReport += $"{TargetLabel(row)} / {row.CategoryName} — {action}\n";
            }
        });
    }

    private void ReadOutcome()
    {
        ++_progressRevision;
        var transaction = _restoreSession?.Transaction;
        var state = transaction?.State;
        IsRestorePaused = state == RestoreTransactionState.Paused;
        RecoveryAvailable = IsRestorePaused ||
            ((state is RestoreTransactionState.Failed or RestoreTransactionState.RecoveryIncomplete) &&
             transaction!.EntryStates.Any(entry => entry is RestoreEntryState.Applied or RestoreEntryState.Intent));
        IsRollbackPromptVisible = IsRestorePaused && RecoveryAvailable;
        RestoreStatus = state switch
        {
            RestoreTransactionState.Completed => string.Join(" and ",
                _executionReview!.Targets.Select(target => target.CategoryName).Distinct()) + " restored.",
            RestoreTransactionState.Paused => "Restore paused.",
            RestoreTransactionState.RolledBack => "Rollback complete.",
            RestoreTransactionState.RecoveryIncomplete => "Rollback incomplete. Review the remaining targets and keep the safety backup.",
            _ => "Restore stopped before completion."
        };
        if (state == RestoreTransactionState.RecoveryIncomplete) RestoreErrorCode = "RESTORE_RECOVERY_INCOMPLETE";
        if (_executionReview is not { } review || transaction is null) return;
        var applied = transaction.EntryStates.Count(entry => entry == RestoreEntryState.Applied);
        var recovered = transaction.EntryStates.Count(entry => entry == RestoreEntryState.Recovered);
        RestoreCounts = $"{transaction.EntryStates.Count} targets · {applied} restored · {recovered} recovered";
        if (transaction.HasPublishedSafety) PublishedSafetyPath = review.SafetyPath;
        if (state == RestoreTransactionState.Paused)
        {
            RestoreProgress = 100.0 * applied / transaction.EntryStates.Count;
            HasProgressTotal = true;
        }
        if (state is RestoreTransactionState.Completed or RestoreTransactionState.RolledBack)
        {
            RestoreProgress = 100;
            HasProgressTotal = true;
        }
        RestoreReport = string.Join("\n", review.Targets.Zip(transaction.EntryStates, (row, entry) =>
            $"{TargetLabel(row)} / {row.CategoryName}{(row.Extension is { } extension ? $" ({extension.Id})" : string.Empty)} — {DescribeEntry(entry, state)}"));
    }

    private static string TargetLabel(RestoreReviewedTarget target) =>
        $"{target.Installation.Product} {target.Installation.Channel} {target.Installation.Version} / {target.Profile.ProfileName} ({target.Profile.ProfileDirectory})";

    private static string DescribeEntry(RestoreEntryState entry, RestoreTransactionState? state) => entry switch
    {
        RestoreEntryState.Applied when state == RestoreTransactionState.RecoveryIncomplete => "Restore applied. Recovery unresolved.",
        RestoreEntryState.Applied => "Restored",
        RestoreEntryState.Untouched or RestoreEntryState.Intent when state == RestoreTransactionState.Paused => "Pending",
        RestoreEntryState.Intent => "Write outcome requires verification",
        RestoreEntryState.Recovered => "Original state recovered",
        _ => "Untouched"
    };

    private static bool IsRestoreError(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or System.Security.Cryptography.CryptographicException
            or System.Text.Json.JsonException or NotSupportedException;

    private static string RestoreFailureMessage(Exception error) => error switch
    {
        BackupOperationException { Code: "restore-settings-empty" } =>
            "A selected profile has no stored Settings. Clear Settings for that profile and review again.",
        BackupOperationException { Code: "restore-settings-protected" } =>
            "A selected Settings value is stored in the destination's Secure Preferences. Choose another destination or clear Settings.",
        BackupOperationException { Code: "restore-settings-missing" } =>
            "A destination profile needs an existing Preferences file. Choose an initialized Brave profile or clear Settings.",
        BackupOperationException { Code: "restore-settings-parent" or "restore-settings-value" } =>
            "A destination Preferences path has an incompatible structure. Choose another profile or clear Settings.",
        BackupOperationException { Code: "restore-settings-size" } =>
            "The updated Preferences file exceeds the size limit. Choose another profile or clear Settings.",
        BackupOperationException { Code: "restore-extension-registration" or "restore-extension-target" } =>
            "The registered extension or its Local store changed after Recheck. Recheck the destination and review again.",
        BackupOperationException { Code: "restore-extension-storage" } =>
            "An archived Local store could not be validated for restore. Clear Extensions and review again.",
        System.Security.Cryptography.CryptographicException => "Could not authenticate the source archive or safety backup.",
        _ => "Restore could not finish. Check the archive, destination compatibility, safety folder and Brave's status."
    };

    private static string RestoreFailureCode(Exception error) => error switch
    {
        System.Security.Cryptography.CryptographicException => "RESTORE_AUTHENTICATION_FAILED",
        UnauthorizedAccessException => "RESTORE_ACCESS_DENIED",
        BackupOperationException { Code: "restore-settings-empty" or "restore-settings-protected"
            or "restore-settings-missing" or "restore-settings-parent" or "restore-settings-value" or "restore-settings-size" } settings => settings.Code,
        BackupOperationException { Code: "restore-extension-registration" or "restore-extension-target"
            or "restore-extension-storage" } extension => extension.Code,
        InvalidDataException or System.Text.Json.JsonException or ArgumentException or NotSupportedException => "RESTORE_DATA_REJECTED",
        IOException => "RESTORE_CONTEXT_OR_IO_FAILED",
        _ => "RESTORE_OPERATION_FAILED"
    };

    private bool CanOpenSafetyFolder() => HasPublishedSafety;

    /// <summary>Opens the folder containing the published recovery artifact.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenSafetyFolder))]
    private void OpenSafetyFolder()
    {
        if (!CanOpenSafetyFolder()) return;
        try { BackupFileOperations.OpenFolder(Path.GetDirectoryName(PublishedSafetyPath)!); }
        catch { RestoreStatus = "Could not open the safety-backup folder."; }
    }

    private void ClearRestoreSession()
    {
        ++_progressRevision;
        _restoreSession?.Dispose();
        _restoreSession = null;
        _executionReview = null;
        IsRollbackPromptVisible = false;
        RecoveryAvailable = false;
        RestoreStatus = string.Empty;
        RestoreReport = string.Empty;
        RestoreCounts = string.Empty;
        RestoreErrorCode = string.Empty;
        PublishedSafetyPath = string.Empty;
        HasProgressTotal = false;
        RestoreProgress = 0;
    }

    private void CloseExecution()
    {
        _closingExecution = true;
        _pauseSignal?.Cancel();
        if (IsExecutingRestore)
        {
            _sourceSecret = null;
            Password = string.Empty;
            Safety.ClearSecrets();
            return;
        }
        IsRestorePaused = false;
        ClearRestoreSession();
    }
}
