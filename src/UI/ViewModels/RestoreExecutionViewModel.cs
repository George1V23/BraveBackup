using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BraveBackup.Core;
using BraveBackup.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>Where the reviewed restore flow currently is. Only Running, Pausing, Preparing and RollingBack are mid-write.</summary>
public enum RestorePhase { Idle, Creating, Reviewing, Preparing, Running, Pausing, Paused, RollingBack, Completed, RolledBack, Failed, RecoveryIncomplete }

/// <summary>One restored component. Done means restored (or, while rolling back, recovered).</summary>
public sealed partial class RestoreChecklistItem : ObservableObject
{
    public string Installation { get; }
    public string Profile { get; }
    public string Category => "Bookmarks";
    public string Label => $"{Installation} › {Profile} › {Category}";
    [ObservableProperty] private bool _isDone;
    public RestoreChecklistItem(string installation, string profile) { Installation = installation; Profile = profile; }
}

/// <summary>
/// Review → confirm → progress → pause/rollback/resume → report for bookmarks restore (accepted controls 10–14).
/// Creating a review writes nothing; only <see cref="ConfirmAsync"/> starts the engine. The session and secrets stay in memory
/// until the result is dismissed so a completed or failed restore can still be rolled back.
/// </summary>
public sealed partial class RestoreExecutionViewModel : ViewModelBase, IDisposable
{
    public delegate BraveBookmarksRestoreSession SessionFactory(string archive, string? archivePassword,
        IReadOnlyList<BraveBookmarksRestoreItem> items, string recoveryDirectory, string safetyPassword, CancellationToken token);

    private readonly RestorePlanningViewModel _planning;
    private readonly RestoreSafetyOptionsViewModel _safety;
    private readonly Func<string?> _archivePassword;
    private readonly Action _clearArchivePassword;
    private BraveBookmarksRestoreSession? _session;
    private IReadOnlyList<RestorePlannedItem> _items = [];
    private string _archivePath = string.Empty;
    private bool _disposed;

    /// <summary>Creates the session; replaceable so tests can supply a stopped-browser observer.</summary>
    public SessionFactory CreateSession { get; set; } = (archive, password, items, folder, safety, token) =>
        BraveBookmarksRestoreSession.Create(archive, password, items, folder, safety, null, token);
    /// <summary>Marshals engine callbacks to the UI thread.</summary>
    public Action<Action> Post { get; set; } = action => Avalonia.Threading.Dispatcher.UIThread.Post(action);
    public Action<string> OpenFolder { get; set; } = BackupFileOperations.OpenFolder;
    /// <summary>Chooses a new report file; null cancels.</summary>
    public Func<Task<string?>>? PickReportFile { get; set; }

    public ObservableCollection<RestoreChecklistItem> Checklist { get; } = [];
    [ObservableProperty] private RestorePhase _phase;
    [ObservableProperty] private string _reviewText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _rollbackPromptVisible;
    [ObservableProperty] private string _report = string.Empty;
    [ObservableProperty] private string _closeNotice = string.Empty;

    public RestoreExecutionViewModel(RestorePlanningViewModel planning, RestoreSafetyOptionsViewModel safety, Func<string?> archivePassword, Action clearArchivePassword)
    { _planning = planning; _safety = safety; _archivePassword = archivePassword; _clearArchivePassword = clearArchivePassword;
      planning.PropertyChanged += (_, _) => ReviewCommand.NotifyCanExecuteChanged(); safety.PropertyChanged += (_, _) => ReviewCommand.NotifyCanExecuteChanged(); }

    public bool HasMessage => Message.Length != 0;
    public bool HasCloseNotice => CloseNotice.Length != 0;
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnCloseNoticeChanged(string value) => OnPropertyChanged(nameof(HasCloseNotice));
    public bool IsIdle => Phase == RestorePhase.Idle;
    public bool CanReviewNow => Phase == RestorePhase.Idle && _planning.IsReadyForReview && _safety.IsValid;
    public bool IsReviewing => Phase == RestorePhase.Reviewing;
    /// <summary>The bar stays visible with its final value after a result so the outcome can be read at a glance.</summary>
    public bool ShowProgress => IsOperationOpen;
    public bool ShowChecklist => Checklist.Count > 0 && Phase > RestorePhase.Reviewing;
    public bool IsPaused => Phase == RestorePhase.Paused;
    public bool CanRequestPause => Phase == RestorePhase.Running;
    public bool IsFinished => Phase is RestorePhase.Completed or RestorePhase.RolledBack or RestorePhase.Failed or RestorePhase.RecoveryIncomplete;
    /// <summary>A restore that finished or failed may still be rolled back from its safety backup while its session is kept.</summary>
    public bool CanOfferRollback => _session is not null && Phase is RestorePhase.Completed or RestorePhase.Failed or RestorePhase.RecoveryIncomplete && Touched;
    /// <summary>True while the page must keep its state: the session is writing, paused, or its result is still shown.</summary>
    public bool IsOperationOpen => Phase >= RestorePhase.Preparing;
    /// <summary>True while closing the application would interrupt a write.</summary>
    public bool IsCloseBlocked => Phase is RestorePhase.Preparing or RestorePhase.Running or RestorePhase.Pausing or RestorePhase.RollingBack;
    public string Title => Phase switch
    {
        RestorePhase.Preparing => "Preparing safety backup", RestorePhase.Running => "Restoring", RestorePhase.Pausing => "Pausing…",
        RestorePhase.Paused => "Restoration paused", RestorePhase.RollingBack => "Rolling back", RestorePhase.Completed => "Restored",
        RestorePhase.RolledBack => "Rolled back", RestorePhase.RecoveryIncomplete => "Recovery incomplete", RestorePhase.Failed => "Restore failed", _ => string.Empty
    };
    private bool Touched => _session is not null && _session.Transaction.EntryStates.Any(state => state != RestoreEntryState.Untouched);

    partial void OnPhaseChanged(RestorePhase value)
    {
        foreach (var name in new[] { nameof(IsIdle), nameof(CanReviewNow), nameof(IsReviewing), nameof(ShowProgress), nameof(ShowChecklist), nameof(IsPaused),
                     nameof(CanRequestPause), nameof(IsFinished), nameof(CanOfferRollback), nameof(IsOperationOpen), nameof(IsCloseBlocked), nameof(Title) })
            OnPropertyChanged(name);
        ReviewCommand.NotifyCanExecuteChanged(); CancelRestoreCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Creates and validates the frozen restore; nothing is written. The result is shown for confirmation.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewNow))]
    private async Task ReviewAsync()
    {
        if (!CanReviewNow || _disposed) return;
        var items = _planning.FreezeSelection(); var archive = _planning.SourcePath;
        var password = _archivePassword(); var folder = _safety.FullFolder; var safety = _safety.Password;
        if (items.Count == 0) return;
        Message = string.Empty; Phase = RestorePhase.Creating;
        try
        {
            var mapped = items.Select(i => new BraveBookmarksRestoreItem(i.SourceId, i.Installation, i.Profile)).ToArray();
            var session = await Task.Run(() => CreateSession(archive, password, mapped, folder, safety, CancellationToken.None));
            if (_disposed) { session.Dispose(); return; }
            _session = session; _items = items; _archivePath = archive;
            Checklist.Clear(); foreach (var item in items) Checklist.Add(new(item.InstallationName, item.ProfileName));
            ReviewText = BuildReview(items, archive, session);
            Phase = RestorePhase.Reviewing;
        }
        catch (Exception exception)
        {
            Message = Describe(exception, "The restore could not be reviewed. Nothing was changed.");
            Phase = RestorePhase.Idle;
        }
    }

    private static string BuildReview(IReadOnlyList<RestorePlannedItem> items, string archive, BraveBookmarksRestoreSession session)
    {
        var text = new StringBuilder("Source backup: ").AppendLine(archive).AppendLine();
        foreach (var group in items.GroupBy(i => i.InstallationName))
        {
            text.AppendLine(group.Key);
            foreach (var item in group)
            {
                text.Append("  \"").Append(item.SourceName).Append("\"  →  ").Append(item.ProfileName).Append("  ").AppendLine(Path.GetFullPath(item.Profile.ProfileDirectory));
                text.AppendLine("      Bookmarks: REPLACES the destination's current Bookmarks (created if the profile has none).");
            }
        }
        return text.AppendLine().AppendLine("Not changed: all other categories and profiles. Nothing is merged.")
            .AppendLine().AppendLine("Encrypted safety backup:").AppendLine("  " + session.SafetyPath)
            .AppendLine("Keep Brave closed. A lost safety-backup password cannot be recovered by the application.").ToString();
    }

    /// <summary>Discards the review and any retained targets; nothing was written.</summary>
    [RelayCommand]
    private void CancelReview() { if (Phase != RestorePhase.Reviewing) return; DisposeSession(); ReviewText = string.Empty; Checklist.Clear(); Phase = RestorePhase.Idle; }

    /// <summary>Explicit approval: creates and verifies the safety backup, then restores.</summary>
    [RelayCommand]
    private async Task ConfirmAsync()
    {
        if (Phase != RestorePhase.Reviewing || _session is not { } session) return;
        session.Transaction.Progress = OnProgress;
        Progress = 0; IsIndeterminate = true; Message = string.Empty; Phase = RestorePhase.Preparing;
        try { await Task.Run(() => session.Transaction.Prepare()); }
        catch (Exception exception) { Finish(RestorePhase.Failed, Describe(exception, "The safety backup could not be created or verified. Nothing was restored.")); return; }
        if (_disposed) return;
        await RunAsync();
    }

    private async Task RunAsync()
    {
        if (_session is not { } session) return;
        IsIndeterminate = false; Phase = RestorePhase.Running; RefreshChecklist(false);
        try { await Task.Run(() => session.Transaction.Run()); }
        catch (Exception exception)
        {
            RefreshChecklist(false);
            var state = session.Transaction.State;
            // A refused preflight leaves a paused operation paused, or a prepared one untouched; neither wrote anything new.
            if (state == RestoreTransactionState.Paused) { Phase = RestorePhase.Paused; Message = Describe(exception, "Resuming is not possible right now."); RollbackPromptVisible = false; return; }
            Finish(RestorePhase.Failed, Describe(exception, Touched ? "Restoration stopped. Changes made so far can be rolled back." : "Restoration was stopped before any change was made."));
            RollbackPromptVisible = false; return;
        }
        if (_disposed) return;
        RefreshChecklist(false);
        if (session.Transaction.State == RestoreTransactionState.Paused) { Phase = RestorePhase.Paused; RollbackPromptVisible = true; Message = "Restoration paused at a safe point."; }
        else Finish(RestorePhase.Completed, "Restoration finished.");
    }

    /// <summary>Requests a pause at the next safe write boundary; it does not roll back or end the restore.</summary>
    [RelayCommand(CanExecute = nameof(CanRequestPause))]
    private void CancelRestore() { if (_session is null || Phase != RestorePhase.Running) return; _session.Transaction.RequestPause(); Phase = RestorePhase.Pausing; }

    /// <summary>Leaves the paused restore paused: no rollback, no resume.</summary>
    [RelayCommand]
    private void NotNow() => RollbackPromptVisible = false;

    /// <summary>Offers rollback of a finished or failed restore, using the same confirmation as after a pause.</summary>
    [RelayCommand]
    private void OfferRollback() { if (CanOfferRollback) RollbackPromptVisible = true; }

    /// <summary>Continues only after the engine revalidates source, destinations and stopped-browser state.</summary>
    [RelayCommand(CanExecute = nameof(IsPaused))]
    private async Task ResumeAsync()
    {
        if (Phase != RestorePhase.Paused) return;
        RollbackPromptVisible = false; Message = string.Empty; await RunAsync();
    }

    /// <summary>Explicitly approved rollback from the verified safety backup.</summary>
    [RelayCommand]
    private async Task RollBackAsync()
    {
        if (_session is not { } session || !(Phase == RestorePhase.Paused || CanOfferRollback)) return;
        RollbackPromptVisible = false; Message = string.Empty; Progress = 0; IsIndeterminate = false; Phase = RestorePhase.RollingBack;
        foreach (var item in Checklist) item.IsDone = false;
        try { await Task.Run(() => session.Transaction.Rollback(true)); }
        catch (Exception exception) { RefreshChecklist(true); Finish(RestorePhase.RecoveryIncomplete, Describe(exception, "Rollback could not complete. Your safety backup is kept; see its path below.")); return; }
        if (_disposed) return;
        RefreshChecklist(true);
        if (session.Transaction.State == RestoreTransactionState.RolledBack) Finish(RestorePhase.RolledBack, "All changes made by this restore were rolled back.");
        else Finish(RestorePhase.RecoveryIncomplete, "Some changes could not be rolled back. Your safety backup is kept; see its path below.");
    }

    private void OnProgress(RestoreTransactionProgress update)
    {
        var session = _session; if (session is null) return;
        // Called by the engine thread while it holds its lock; snapshot the states and update on the UI thread.
        var states = session.Transaction.EntryStates.ToArray(); var rolling = update.State == RestoreTransactionState.RollingBack;
        Post(() => { if (!_disposed) Apply(states, rolling); });
    }

    private void RefreshChecklist(bool rolling) { if (_session is { } session) Apply(session.Transaction.EntryStates.ToArray(), rolling); }

    private void Apply(RestoreEntryState[] states, bool rolling)
    {
        var done = 0; var inFlight = 0.0;
        for (var i = 0; i < states.Length && i < Checklist.Count; i++)
        {
            var state = states[i];
            // While restoring, Applied is done; while rolling back, Recovered or never-touched work counts as resolved.
            var resolved = rolling ? state is RestoreEntryState.Recovered or RestoreEntryState.Untouched : state == RestoreEntryState.Applied;
            Checklist[i].IsDone = rolling ? state == RestoreEntryState.Recovered : state == RestoreEntryState.Applied;
            if (resolved) done++; else if (!rolling && state == RestoreEntryState.Intent) inFlight = 0.5;
        }
        Progress = states.Length == 0 ? 0 : Math.Min(100, (done + inFlight) * 100.0 / states.Length);
    }

    private void Finish(RestorePhase phase, string message)
    {
        Message = message; Report = BuildReport(phase); Phase = phase;
        if (phase == RestorePhase.Completed) Progress = 100;
        _safety.ClearSecrets(); _clearArchivePassword();
        // Completed/Failed keep the session so rollback stays possible; Rolled back and recovery-incomplete runs are final here.
        if (phase == RestorePhase.RolledBack) DisposeSession();
        OnPropertyChanged(nameof(CanOfferRollback));
    }

    private string BuildReport(RestorePhase phase)
    {
        var text = new StringBuilder();
        text.AppendLine("BraveBackup restore report").AppendLine("Outcome: " + phase).AppendLine("Source backup: " + _archivePath);
        if (_session is { } session)
        {
            text.AppendLine("Engine state: " + session.Transaction.State).AppendLine("Safety backup: " + session.SafetyPath).AppendLine("Journal: " + session.JournalPath);
            var states = session.Transaction.EntryStates;
            text.AppendLine("Files restored: " + states.Count(s => s == RestoreEntryState.Applied) + " of " + states.Count + "; recovered: " + states.Count(s => s == RestoreEntryState.Recovered));
            foreach (var group in _items.Select((item, index) => (item, state: index < states.Count ? states[index] : RestoreEntryState.Untouched)).GroupBy(p => p.item.InstallationName))
            {
                text.AppendLine(group.Key);
                foreach (var (item, state) in group) text.AppendLine($"  {item.ProfileName} › Bookmarks: {state}");
            }
        }
        return text.ToString();
    }

    /// <summary>Folder that holds the retained encrypted safety backup and journal.</summary>
    public string? SafetyFolder => _session is null ? null : Path.GetDirectoryName(_session.SafetyPath);
    private string? _lastSafetyFolder;
    [RelayCommand]
    private void OpenSafetyFolder()
    {
        var folder = SafetyFolder ?? _lastSafetyFolder; if (folder is null) return;
        try { OpenFolder(folder); } catch { Message = "The safety-backup folder could not be opened."; }
    }

    /// <summary>Saves the value-free report to a new file chosen by the user; never overwrites.</summary>
    [RelayCommand]
    private async Task SaveReportAsync()
    {
        if (PickReportFile is null || Report.Length == 0) return;
        try
        {
            var path = await PickReportFile(); if (string.IsNullOrWhiteSpace(path)) return;
            var full = Path.GetFullPath(path); ExportDestinationGuard.Validate(full);
            await using var output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await output.WriteAsync(new UTF8Encoding(false).GetBytes(Report)); Message = "Report saved.";
        }
        catch { Message = "The report could not be saved. Choose a new file outside browser data."; }
    }

    /// <summary>Dismisses a finished result: releases the session, clears secrets and returns to the idle page.</summary>
    [RelayCommand]
    private void Done()
    {
        if (!IsFinished && Phase != RestorePhase.Paused) return;
        _lastSafetyFolder = SafetyFolder; DisposeSession(); _safety.ClearSecrets(); _clearArchivePassword();
        Checklist.Clear(); ReviewText = string.Empty; Report = string.Empty; Message = string.Empty; RollbackPromptVisible = false; Phase = RestorePhase.Idle;
    }

    /// <summary>Called by the window when it is asked to close; tells the user why closing is blocked while writing.</summary>
    public bool TryClose()
    {
        if (!IsCloseBlocked) { CloseNotice = string.Empty; return true; }
        CloseNotice = "Restoration is in progress. Pause or let it finish before closing BraveBackup.";
        return false;
    }

    private void DisposeSession() { _lastSafetyFolder ??= SafetyFolder; var session = _session; _session = null; session?.Dispose(); OnPropertyChanged(nameof(CanOfferRollback)); }

    private static string Describe(Exception exception, string fallback) => exception switch
    {
        IOException io when io.Message.StartsWith("Close Brave", StringComparison.Ordinal) || io.Message.Contains("browser state", StringComparison.Ordinal) =>
            "Brave appears to be running or cannot be verified. Close it and try again.",
        InvalidDataException invalid when invalid.Message.Contains("open it in Brave", StringComparison.Ordinal) => invalid.Message,
        IOException => fallback + " A file, folder or the source backup changed or is not accessible.",
        _ => fallback
    };

    /// <summary>Requests a pause and releases the session; recovery artifacts stay on disk. A running write finishes first.</summary>
    public void Dispose() { if (_disposed) return; _disposed = true; _session?.Transaction.RequestPause(); DisposeSession(); _safety.ClearSecrets(); _clearArchivePassword(); }
}
