using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BraveBackup.Core;
using BraveBackup.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

public partial class RestorePreviewViewModel
{
    private RestartRollbackReview? _restartReview;
    public Func<Task<string?>>? PickRestartRecord { get; set; }
    public bool SupportsRestartRollback => HostEnvironment.IsLinux;
    [ObservableProperty] private string _restartRecordPath = string.Empty;
    [ObservableProperty] private string _restartSafetyPassword = string.Empty;
    [ObservableProperty] private string _restartReviewText = string.Empty;
    [ObservableProperty] private bool _hasRestartReview;

    partial void OnRestartRecordPathChanged(string value)
    {
        RestartSafetyPassword = string.Empty;
        InvalidateRestartReview();
    }

    partial void OnRestartSafetyPasswordChanged(string value) => InvalidateRestartReview();

    private void InvalidateRestartReview()
    {
        _restartReview = null;
        HasRestartReview = false;
        RestartReviewText = string.Empty;
        NotifyRestartCommands();
    }

    private void NotifyRestartCommands()
    {
        ReviewRestartRollbackCommand.NotifyCanExecuteChanged();
        ConfirmRestartRollbackCommand.NotifyCanExecuteChanged();
    }

    private void ClearRestartReview()
    {
        RestartSafetyPassword = string.Empty;
        InvalidateRestartReview();
    }

    [RelayCommand]
    private async Task BrowseRestartRecordAsync()
    {
        if (_disposed || IsRestoreActive || IsBusy || PickRestartRecord is null) return;
        var generation = _generation;
        try
        {
            var selected = await PickRestartRecord();
            if (!_disposed && generation == _generation && !IsRestoreActive && !string.IsNullOrWhiteSpace(selected))
                RestartRecordPath = selected;
        }
        catch { if (!_disposed && generation == _generation) RestartReviewText = "The restart record picker could not be opened."; }
    }

    private bool CanReviewRestartRollback() => !_disposed && SupportsRestartRollback && _restoreAvailable &&
        !IsRestoreActive && !RecoveryAvailable && !IsBusy && !Planning.IsChecking &&
        !string.IsNullOrWhiteSpace(RestartRecordPath) && RestartSafetyPassword.Length != 0;

    private bool CanConfirmRestartRollback() => CanReviewRestartRollback() && _restartReview is { } review &&
        review.Targets.Any(item => item.Decision is RestoreRollbackDecision.RestoreOriginal or RestoreRollbackDecision.AlreadyOriginal);

    /// <summary>Shows the exact affected paths and the observed state without modifying any profile or artifact.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewRestartRollback))]
    private Task ReviewRestartRollbackAsync() => RunRestartRollbackAsync(confirm: false);

    /// <summary>Revalidates the reviewed operation and rolls back its eligible targets.</summary>
    [RelayCommand(CanExecute = nameof(CanConfirmRestartRollback))]
    private Task ConfirmRestartRollbackAsync() => RunRestartRollbackAsync(confirm: true);

    [RelayCommand]
    private void CancelRestartReview() => ClearRestartReview();

    private async Task RunRestartRollbackAsync(bool confirm)
    {
        if (confirm ? !CanConfirmRestartRollback() : !CanReviewRestartRollback()) return;
        var confirmed = _restartReview;
        var password = RestartSafetyPassword;
        var path = RestartRecordPath;
        var profiles = Planning.DetectedRecoveryProfiles;
        var observer = Planning.ObserveBrowserState;
        ClearRestoreSession();
        IsRollbackRunning = true;
        IsExecutingRestore = true;
        if (confirm)
        {
            ClearRestartReview();
            RestoreStatus = "Verifying the persisted operation and rolling back…";
        }
        else
        {
            InvalidateRestartReview();
            RestoreStatus = "Inspecting encrypted recovery artifacts…";
        }
        try
        {
            var result = await Task.Run(() =>
            {
                ProcessPresence Observe()
                {
                    var states = profiles.Select(pair => pair.Installation).Distinct().Select(observer!).ToArray();
                    return states.Contains(ProcessPresence.Running) ? ProcessPresence.Running
                        : states.All(state => state == ProcessPresence.Stopped) ? ProcessPresence.Stopped : ProcessPresence.Unknown;
                }

                using var operation = LinuxRestartRollback.Open(path, password, profiles, observer is null ? null : Observe);
                var state = confirm ? operation.Rollback(confirmed!) : (RestoreTransactionState?)null;
                return (operation.Review, State: state, Targets: confirm ? operation.TargetResults() : []);
            });
            if (_disposed || _closingExecution) return;
            if (confirm)
            {
                RestoreStatus = result.State == RestoreTransactionState.RolledBack
                    ? "Rollback complete." : "Rollback incomplete. Conflicting or inaccessible targets were preserved.";
                PublishedSafetyPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!,
                    result.Review.Operation.ToString("N") + ".safety.encrypted");
                RestoreReport = string.Join("\n", result.Targets.Select(item =>
                    $"{item.Target.Category} · {item.Target.Path} · {(item.Resolved ? "Original state present" : "Unresolved")}"));
            }
            else
            {
                if (RestartRecordPath != path || RestartSafetyPassword != password) return;
                _restartReview = result.Review;
                HasRestartReview = true;
                RestartReviewText = string.Join("\n", result.Review.Targets.Select(item =>
                    $"{item.Category} · {item.Path} · {DescribeRestartTarget(item.Decision)}"));
            }
        }
        catch (Exception error) when (IsRestoreError(error))
        {
            if (_disposed || _closingExecution) return;
            ClearRestartReview();
            var message = error is System.Security.Cryptography.CryptographicException
                ? "The safety password is incorrect or the recovery artifacts are damaged."
                : "Recovery could not be verified. Close Brave, check the selected files and detected profiles, and review again.";
            if (confirm)
            {
                RestoreStatus = "Rollback stopped. Review the operation again to see the current target states.";
                RestoreErrorCode = RestoreFailureCode(error);
            }
            else RestartReviewText = message;
        }
        finally
        {
            if (!confirm) RestoreStatus = string.Empty;
            IsExecutingRestore = false;
            IsRollbackRunning = false;
            NotifyRestartCommands();
        }
    }

    private static string DescribeRestartTarget(RestoreRollbackDecision decision) => decision switch
    {
        RestoreRollbackDecision.Skip => "No change required",
        RestoreRollbackDecision.AlreadyOriginal => "Original already present",
        RestoreRollbackDecision.RestoreOriginal => "Recover original",
        _ => "Conflict · leave unchanged"
    };
}
