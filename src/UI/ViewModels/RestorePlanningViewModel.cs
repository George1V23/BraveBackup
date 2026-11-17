using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BraveBackup.Core;
using BraveBackup.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>Discovered target metadata only. No destination is selected by name or by sidebar/backup selection.</summary>
public sealed record RestoreTargetProfile(BraveProfileInfo Info)
{
    public string Name => Info.ProfileName;
    public string FullPath => Path.TrimEndingDirectorySeparator(Path.GetFullPath(Info.ProfileDirectory));
}
/// <summary>One detected installation and its independently discovered target profiles.</summary>
public sealed record RestoreTargetInstallation(BraveAppInfo Info, RestoreTargetProfile[] Profiles)
{
    public string Name => $"Brave {Info.Product} {Info.Channel} — {Info.Version}";
}
/// <summary>Archived category selection; no destination writers are enabled in the planning phase.</summary>
public partial class RestoreCategoryChoice : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public bool IsSupported => false;
    public string SupportReason => "Restore writer and compatibility checks are not enabled yet. Included data can be inspected only.";
    public bool IsSelected { get => false; set { } }
    public RestoreCategoryChoice(ArchiveCategory category) { Id = category.Id; Name = category.Id + " (v" + category.Version + ")"; }
}
/// <summary>One archived source profile with independent selection and explicit destination mapping.</summary>
public partial class RestoreProfileChoice : ViewModelBase
{
    private readonly Action _changed;
    public Guid SourceId { get; }
    public string Name { get; }
    public RestoreCategoryChoice[] Categories { get; }
    public IReadOnlyList<RestoreTargetInstallation> TargetInstallations { get; }
    public RestoreTargetProfile[] TargetProfiles => SelectedInstallation?.Profiles ?? [];
    public string DestinationPath
    {
        get { try { return SelectedProfile?.FullPath ?? "No destination selected."; } catch { return "Invalid destination path."; } }
    }
    public bool HasSupportedCategories => Categories.Any(category => category.IsSupported);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private RestoreTargetInstallation? _selectedInstallation;
    [ObservableProperty] private RestoreTargetProfile? _selectedProfile;
    [ObservableProperty] private string _checkResult = "Not checked. Choose a destination and use Recheck.";

    public RestoreProfileChoice(ArchiveProfile source, IReadOnlyList<RestoreTargetInstallation> targets, Action changed)
    {
        SourceId = source.Id; Name = source.DisplayName + " (" + source.DirectoryName + ")";
        Categories = source.Categories.Select(category => new RestoreCategoryChoice(category)).ToArray();
        TargetInstallations = targets; _changed = changed;
    }
    partial void OnIsSelectedChanged(bool value) => _changed();
    partial void OnSelectedInstallationChanged(RestoreTargetInstallation? value)
    {
        SelectedProfile = null; OnPropertyChanged(nameof(TargetProfiles)); _changed();
    }
    partial void OnSelectedProfileChanged(RestoreTargetProfile? value)
    {
        OnPropertyChanged(nameof(DestinationPath)); _changed();
    }
    /// <summary>Leaves unsupported categories unselected; future writers must establish their capability before enabling them.</summary>
    [RelayCommand] private void SelectAllSupported() { }
    /// <summary>Clears category choices; all are already unselected while restore writers are disabled.</summary>
    [RelayCommand] private void ClearCategories() { }
}
/// <summary>Groups source profiles by archived installation, preserving archive-local identity.</summary>
public sealed record RestoreSourceGroup(string Name, RestoreProfileChoice[] Profiles);

/// <summary>Builds a write-free restore plan and rechecks target metadata/process state without enabling execution.</summary>
public partial class RestorePlanningViewModel : ViewModelBase, IDisposable
{
    private RestoreTargetInstallation[] _targets = [];
    private ArchiveManifest? _manifest;
    private string _sourcePath = string.Empty;
    private CancellationTokenSource? _checking;
    private long _revision;
    private bool _disposed;
    public ObservableCollection<RestoreSourceGroup> Groups { get; } = [];
    public Func<BraveAppInfo, ProcessPresence>? ObserveBrowserState { get; set; }
    public bool HasSource => _manifest is not null;
    public string PlanSummary => string.Join("\n", Profiles.Where(p => p.IsSelected).Select(p =>
        p.Name + " → " + p.DestinationPath + "\n" + p.CheckResult));
    public string Availability
    {
        get
        {
            var selected = Profiles.Where(p => p.IsSelected).ToArray();
            if (!HasSource) return "Validate a backup to choose sources and destinations.";
            if (selected.Length == 0) return "Select at least one backed-up profile. Nothing is selected automatically.";
            if (selected.Any(p => p.SelectedInstallation is null || p.SelectedProfile is null)) return "Choose a destination installation and profile for every selected source.";
            if (selected.Any(p => !p.TargetInstallations.Any(t => ReferenceEquals(t, p.SelectedInstallation)) ||
                                  !p.TargetProfiles.Any(t => ReferenceEquals(t, p.SelectedProfile)))) return "A destination is no longer part of the discovered profile inventory. Choose it again.";
            try
            {
                if (selected.Select(p => p.SelectedProfile!.FullPath).Distinct(PathComparer).Count() != selected.Length)
                    return "Two source profiles cannot use the same destination. Choose different profiles.";
            }
            catch { return "A destination path is invalid."; }
            return "Planning only. Category writers and the safety/rollback engine are not enabled; restoration is unavailable.";
        }
    }
    private IEnumerable<RestoreProfileChoice> Profiles => Groups.SelectMany(g => g.Profiles);
    private static StringComparer PathComparer => HostEnvironment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    [ObservableProperty] private bool _isChecking;

    /// <summary>Replaces discovered target metadata and clears mappings rather than silently remapping across rediscovery.</summary>
    public void SetTargets(IEnumerable<RestoreTargetInstallation> targets)
    {
        _targets = targets.ToArray();
        if (_manifest is { } source) Load(source, _sourcePath);
    }
    /// <summary>Loads independent source choices; archive names never select a destination.</summary>
    public void Load(ArchiveManifest manifest, string sourcePath)
    {
        Reset(); _manifest = manifest; _sourcePath = sourcePath;
        foreach (var installation in manifest.Installations)
            Groups.Add(new($"{installation.Product} {installation.Channel} — {installation.BrowserVersion}",
                installation.Profiles.Select(p => new RestoreProfileChoice(p, _targets, Changed)).ToArray()));
        Notify();
    }
    private void Changed()
    {
        ++_revision; _checking?.Cancel(); IsChecking = false;
        foreach (var profile in Profiles) profile.CheckResult = "Not checked. Use Recheck after changing the plan.";
        Notify();
    }
    private void Notify()
    {
        OnPropertyChanged(nameof(HasSource)); OnPropertyChanged(nameof(Availability)); OnPropertyChanged(nameof(PlanSummary));
        RecheckCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsCheckingChanged(bool value) => RecheckCommand.NotifyCanExecuteChanged();
    private bool CanRecheck() => !_disposed && !IsChecking && Profiles.Any(p => p.IsSelected && p.SelectedProfile is not null);
    /// <summary>Checks frozen mapping identities, directory access and conservative host-wide Brave status off the UI thread.</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    public async Task RecheckAsync()
    {
        if (!CanRecheck()) return;
        _checking?.Cancel(); using var cancellation = new CancellationTokenSource(); _checking = cancellation;
        var revision = ++_revision; var source = _sourcePath; var observer = ObserveBrowserState;
        var requests = Profiles.Where(p => p.IsSelected).Select(p => (Row: p, App: p.SelectedInstallation, Profile: p.SelectedProfile)).ToArray();
        IsChecking = true;
        try
        {
            var results = await Task.Run(() => requests.Select(request =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (request.App is null || request.Profile is null) return "No destination selected.";
                if (!_targets.Any(t => ReferenceEquals(t, request.App)) || !request.App.Profiles.Any(p => ReferenceEquals(p, request.Profile)))
                    return "Invalid destination mapping.";
                try
                {
                    BraveRestoreTargetGuard.Validate(request.App.Info, request.Profile.Info, source, cancellation.Token);
                    var presence = observer?.Invoke(request.App.Info) ?? ProcessPresenceProbe.Observe(["brave"], cancellation.Token);
                    return presence switch
                    {
                        ProcessPresence.Stopped => "Closed (no Brave process observed). Target directory is readable; write compatibility is not established.",
                        ProcessPresence.Running => "Running. Close Brave manually before restoration.",
                        _ => "Unable to verify. Restoration must stay blocked."
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch { return "Unable to verify target path, access or browser status. Restoration must stay blocked."; }
            }).ToArray(), cancellation.Token);
            if (_disposed || revision != _revision) return;
            for (var index = 0; index < requests.Length; index++) requests[index].Row.CheckResult = results[index];
            Notify();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_checking, cancellation)) _checking = null;
            if (!_disposed && revision == _revision) IsChecking = false;
        }
    }
    /// <summary>Selects only archived source profiles, independently of all live backup selections.</summary>
    [RelayCommand] private void SelectAllProfiles() { foreach (var profile in Profiles) profile.IsSelected = true; }
    /// <summary>Clears archived source selection without modifying browser data.</summary>
    [RelayCommand] private void ClearProfiles() { foreach (var profile in Profiles) profile.IsSelected = false; }
    /// <summary>Invalidates pending checks and releases the inspected source and all mappings.</summary>
    public void Reset()
    {
        ++_revision; _checking?.Cancel(); _checking = null; IsChecking = false;
        Groups.Clear(); _manifest = null; _sourcePath = string.Empty; Notify();
    }
    public void Dispose() { if (_disposed) return; Reset(); _disposed = true; ObserveBrowserState = null; _targets = []; }
}
