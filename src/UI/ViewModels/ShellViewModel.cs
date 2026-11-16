using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BraveBackup.Core;
using BraveBackup.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>Coordinates installation navigation, read-only discovery and the custom-backup selection form.</summary>
public partial class ShellViewModel : ViewModelBase, IDisposable
{
    private readonly Func<IReadOnlyList<BraveAppInfo>> _detect;
    private readonly Func<BraveAppInfo, IReadOnlyList<BraveProfileInfo>> _findProfiles;
    private readonly Func<BraveProfileInfo, BraveProfileSummary> _summarize;
    private readonly Func<string, Task<string?>>? _pickBackupPath;
    private bool _disposed;

    private static StringComparer PathComparer =>
        HostEnvironment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Gets the application's approved display name.</summary>
    public string ApplicationName => "Brave browsers Backup & Restore custom data";

    /// <summary>Gets the sidebar installations.</summary>
    public ObservableCollection<InstallationViewModel> Installations { get; } = new();

    /// <summary>Gets the installation groups currently shown in the backup form.</summary>
    public ObservableCollection<InstallationViewModel> BackupInstallations { get; } = new();

    /// <summary>Gets form choices, independent from the existing JSON exporter.</summary>
    public BackupOptionsViewModel Options { get; }

    /// <summary>Gets or sets the active installation; command handlers coordinate its presentation state.</summary>
    [ObservableProperty] private InstallationViewModel? _activeInstallation;

    /// <summary>Gets or sets the profile being inspected, separate from backup checkboxes.</summary>
    [ObservableProperty] private ProfileSelectionViewModel? _focusedProfile;

    /// <summary>Gets or sets the selected workspace tab.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    /// <summary>Gets whether discovery is running.</summary>
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Gets a discovery-only empty/error message, independent from the deferred status bar.</summary>
    [ObservableProperty] private string _discoveryMessage = "Searching for Brave installations…";

    /// <summary>Gets whether the backup form covers checked installations rather than only the active one.</summary>
    [ObservableProperty] private bool _isBulkMode;

    /// <summary>Gets whether at least one installation is checked for Export All.</summary>
    public bool HasBulkSelection => Installations.Any(item => item.IsChecked);

    /// <summary>Gets whether an installation can provide a workspace.</summary>
    public bool HasActiveInstallation => ActiveInstallation != null;

    /// <summary>Gets a safe empty value when no installation is active, avoiding null-path bindings.</summary>
    public string ActiveDataDirectory => ActiveInstallation?.AppInfo.UserDataDirectory ?? string.Empty;

    /// <summary>Gets the active profile list or an empty sequence for the initial/disappeared-installation state.</summary>
    public IEnumerable<ProfileSelectionViewModel> ActiveProfiles => ActiveInstallation?.Profiles.AsEnumerable() ??
                                                                    Enumerable.Empty<ProfileSelectionViewModel>();

    /// <summary>Gets whether the backup form has an individual or bulk installation scope.</summary>
    public bool HasBackupScope => IsBulkMode ? HasBulkSelection : HasActiveInstallation;

    /// <summary>Gets whether the currently scoped installations have any discovered profiles.</summary>
    public bool HasBackupProfiles => BackupInstallations.Any(item => item.Profiles.Count > 0);

    /// <summary>Gets a title showing the active installation or bulk scope.</summary>
    public string WorkspaceTitle => IsBulkMode && SelectedTabIndex == 0
        ? "Custom backup · checked installations"
        : ActiveInstallation?.DisplayName ?? "Choose a Brave installation";

    /// <summary>Gets an explicit explanation of the selection scope.</summary>
    public string ScopeDescription => IsBulkMode
        ? "Choose profiles from the checked installations below."
        : "Choose profiles from the active installation. Double-click sidebar headers to prepare Export All.";

    /// <summary>Gets or sets the combined checkbox for all profiles currently in scope.</summary>
    public bool? AllProfilesSelected
    {
        get
        {
            var profiles = BackupInstallations.SelectMany(item => item.Profiles).ToArray();
            return profiles.Length == 0 || profiles.All(item => !item.IsSelected) ? false :
                profiles.All(item => item.IsSelected) ? true : null;
        }
        set
        {
            if (!value.HasValue) return;
            foreach (var profile in BackupInstallations.SelectMany(item => item.Profiles))
                profile.IsSelected = value.Value;
        }
    }

    /// <summary>Creates a shell with injectable read-only discovery, clock and destination picker.</summary>
    /// <remarks>Construction does not scan the machine. The window invokes RefreshAsync when opened.</remarks>
    public ShellViewModel(Func<IReadOnlyList<BraveAppInfo>>? detect = null,
        Func<BraveAppInfo, IReadOnlyList<BraveProfileInfo>>? findProfiles = null,
        Func<BraveProfileInfo, BraveProfileSummary>? summarize = null,
        Func<string, Task<string?>>? pickBackupPath = null, Func<DateTime>? clock = null)
    {
        _detect = detect ?? BraveAppDetector.DetectInstalledApps;
        _findProfiles = findProfiles ?? BraveProfileFinder.FindProfiles;
        _summarize = summarize ?? BraveProfileSummaryReader.Read;
        _pickBackupPath = pickBackupPath;
        Options = new BackupOptionsViewModel(clock);
    }

    /// <summary>Rescans off the UI thread, preserving matching active, expanded, bulk and profile selections.</summary>
    /// <remarks>A failed scan leaves the existing snapshot available. A profile-read failure is isolated to its installation.</remarks>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || IsRefreshing) return;
        IsRefreshing = true;
        DiscoveryMessage = "Searching for Brave installations…";
        try
        {
            var snapshots = await Task.Run(ReadSnapshots);
            if (_disposed) return;
            var previousActiveKey = ActiveInstallation == null ? null : InstallationKey(ActiveInstallation.AppInfo);
            var previousProfilePath = FocusedProfile?.ProfileInfo.ProfileDirectory;
            var previous = Installations.ToDictionary(item => InstallationKey(item.AppInfo), item =>
                (item.IsChecked, item.IsExpanded, Selected: item.Profiles.Where(profile => profile.IsSelected)
                    .Select(profile => profile.ProfileInfo.ProfileDirectory).ToHashSet(PathComparer)), PathComparer);
            // Build the replacement before touching the currently displayed snapshot.
            var replacements = new List<InstallationViewModel>();
            InstallationViewModel? replacementActive = null;
            ProfileSelectionViewModel? replacementFocus = null;
            try
            {
                foreach (var snapshot in snapshots)
                {
                    var installation =
                        new InstallationViewModel(snapshot.App, snapshot.Message, Activate, ToggleBulkSelection);
                    replacements.Add(installation);
                    previous.TryGetValue(InstallationKey(snapshot.App), out var state);
                    installation.IsChecked = state.IsChecked;
                    installation.IsExpanded = state.IsExpanded;
                    foreach (var profile in snapshot.Profiles)
                    {
                        var item = new ProfileSelectionViewModel(installation, profile.Info, profile.Summary,
                            OpenProfile);
                        item.IsSelected = state.Selected?.Contains(profile.Info.ProfileDirectory) == true;
                        installation.Profiles.Add(item);
                    }

                    if (PathComparer.Equals(InstallationKey(snapshot.App), previousActiveKey))
                    {
                        installation.IsActive = true;
                        replacementActive = installation;
                        replacementFocus = installation.Profiles.FirstOrDefault(profile =>
                            PathComparer.Equals(profile.ProfileInfo.ProfileDirectory, previousProfilePath));
                    }
                }
            }
            catch
            {
                foreach (var replacement in replacements) replacement.Dispose();
                throw;
            }

            var oldInstallations = Installations.ToArray();
            foreach (var old in oldInstallations) Unsubscribe(old);
            Installations.Clear();
            foreach (var installation in replacements)
            {
                installation.PropertyChanged += OnInstallationChanged;
                foreach (var profile in installation.Profiles) profile.PropertyChanged += OnProfileChanged;
                Installations.Add(installation);
            }

            ActiveInstallation = replacementActive;
            FocusedProfile = replacementFocus;
            if (!HasBulkSelection) IsBulkMode = false;
            UpdateBackupScope();
            foreach (var old in oldInstallations) old.Dispose();
            DiscoveryMessage = Installations.Count == 0 ? "No supported Brave installations found." : string.Empty;
            OnPropertyChanged(nameof(HasBulkSelection));
        }
        catch (Exception)
        {
            DiscoveryMessage =
                "Could not refresh Brave installations. Existing results have been retained; try Refresh again.";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Activates an installation and returns from bulk to its individual workspace.</summary>
    public void Activate(InstallationViewModel installation)
    {
        if (!Installations.Contains(installation)) return;
        foreach (var item in Installations) item.IsActive = ReferenceEquals(item, installation);
        if (!ReferenceEquals(ActiveInstallation, installation)) FocusedProfile = installation.Profiles.FirstOrDefault();
        ActiveInstallation = installation;
        IsBulkMode = false;
        UpdateBackupScope();
    }

    /// <summary>Activates the double-clicked installation and toggles only its bulk-export checkmark.</summary>
    public void ToggleBulkSelection(InstallationViewModel installation)
    {
        Activate(installation);
        installation.IsChecked = !installation.IsChecked;
    }

    /// <summary>Opens a profile in the Profiles tab without checking it for backup.</summary>
    public void OpenProfile(ProfileSelectionViewModel profile)
    {
        Activate(profile.Installation);
        FocusedProfile = profile;
        SelectedTabIndex = 2;
    }

    /// <summary>Shows the approved bulk profile/category choices in the main workspace, without creating files.</summary>
    [RelayCommand]
    public void ExportAll()
    {
        if (!HasBulkSelection) return;
        if (ActiveInstallation == null) Activate(Installations.First(item => item.IsChecked));
        IsBulkMode = true;
        SelectedTabIndex = 0;
        UpdateBackupScope();
    }

    /// <summary>Restores form defaults and clears profile checkboxes, preserving activation, bulk checkmarks and scope.</summary>
    [RelayCommand]
    public void ResetBackupForm()
    {
        Options.Reset();
        foreach (var profile in Installations.SelectMany(item => item.Profiles)) profile.IsSelected = false;
    }

    /// <summary>Chooses the future archive destination without writing data.</summary>
    [RelayCommand]
    public async Task BrowseBackupPathAsync()
    {
        if (_pickBackupPath == null) return;
        var path = await _pickBackupPath(Options.BackupPath);
        if (!string.IsNullOrWhiteSpace(path)) Options.BackupPath = path;
    }

    /// <summary>Releases owned avatars and clears secret form text; an in-flight scan cannot repopulate a closed shell.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Options.ClearSecrets();
        foreach (var item in Installations)
        {
            Unsubscribe(item);
            item.Dispose();
        }
    }

    /// <summary>Reads browser metadata only; creates no Avalonia objects on the worker thread.</summary>
    private List<InstallationSnapshot> ReadSnapshots()
    {
        var result = new List<InstallationSnapshot>();
        foreach (var app in _detect())
        {
            try
            {
                var profiles = _findProfiles(app).Select(profile => new ProfileSnapshot(profile, _summarize(profile)))
                    .ToArray();
                result.Add(new InstallationSnapshot(app, profiles,
                    profiles.Length == 0 ? "No profiles found." : string.Empty));
            }
            catch (Exception)
            {
                result.Add(new InstallationSnapshot(app, Array.Empty<ProfileSnapshot>(),
                    "Profiles could not be read. Try Refresh again."));
            }
        }

        return result;
    }

    /// <summary>Updates displayed groups without altering stored profile selections outside the current scope.</summary>
    private void UpdateBackupScope()
    {
        BackupInstallations.Clear();
        if (IsBulkMode)
        {
            foreach (var item in Installations.Where(item => item.IsChecked)) BackupInstallations.Add(item);
        }
        else if (ActiveInstallation != null) BackupInstallations.Add(ActiveInstallation);

        OnPropertyChanged(nameof(HasActiveInstallation));
        OnPropertyChanged(nameof(ActiveDataDirectory));
        OnPropertyChanged(nameof(ActiveProfiles));
        OnPropertyChanged(nameof(HasBackupScope));
        OnPropertyChanged(nameof(HasBackupProfiles));
        OnPropertyChanged(nameof(AllProfilesSelected));
        OnPropertyChanged(nameof(WorkspaceTitle));
        OnPropertyChanged(nameof(ScopeDescription));
    }

    partial void OnSelectedTabIndexChanged(int value) => OnPropertyChanged(nameof(WorkspaceTitle));

    private void OnInstallationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(InstallationViewModel.IsChecked)) return;
        OnPropertyChanged(nameof(HasBulkSelection));
        if (!HasBulkSelection) IsBulkMode = false;
        UpdateBackupScope();
    }

    private void OnProfileChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ProfileSelectionViewModel.IsSelected))
            OnPropertyChanged(nameof(AllProfilesSelected));
    }

    private void Unsubscribe(InstallationViewModel installation)
    {
        installation.PropertyChanged -= OnInstallationChanged;
        foreach (var profile in installation.Profiles) profile.PropertyChanged -= OnProfileChanged;
    }

    private static string InstallationKey(BraveAppInfo app) =>
        $"{app.Product}\0{app.Channel}\0{Path.GetFullPath(app.ProgramPath)}\0{Path.GetFullPath(app.UserDataDirectory)}";

    private sealed record ProfileSnapshot(BraveProfileInfo Info, BraveProfileSummary Summary);

    private sealed record InstallationSnapshot(
        BraveAppInfo App,
        IReadOnlyList<ProfileSnapshot> Profiles,
        string Message);
}