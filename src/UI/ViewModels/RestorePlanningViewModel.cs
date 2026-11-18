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
/// <summary>A category selection gated by backend and target compatibility.</summary>
public partial class RestoreCategoryChoice : ViewModelBase
{
    private readonly Action _changed;
    private bool _selected;
    public string Id { get; }
    public string Name { get; }
    [ObservableProperty] private bool _isSupported;
    [ObservableProperty] private string _supportReason = "This category can be inspected only.";

    public bool IsSelected
    {
        get => _selected;
        set
        {
            if (value && !IsSupported) return;
            if (SetProperty(ref _selected, value)) _changed();
        }
    }

    public RestoreCategoryChoice(ArchiveCategory category) : this(category, () => { }) { }

    internal RestoreCategoryChoice(ArchiveCategory category, Action changed)
    {
        Id = category.Id;
        Name = category.Id switch
        {
            "bookmarks" => "Bookmarks",
            "settings" => "Settings",
            "extensions" => "Extensions",
            _ => category.Id + " (v" + category.Version + ")"
        };
        if (Id == "extensions")
            SupportReason = "Use Recheck to find existing Local stores for matching registered extensions.";
        _changed = changed;
    }

    internal void SetCompatibility(bool supported, string reason)
    {
        IsSupported = supported;
        SupportReason = reason;
        if (!supported) IsSelected = false;
    }
}
/// <summary>One archived source profile with independent selection and explicit destination mapping.</summary>
public partial class RestoreProfileChoice : ViewModelBase
{
    private readonly Action _changed;
    private ArchiveInstallation? _sourceInstallation;
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

    /// <summary>Whether the selected destination matches the inspected source product and browser version.</summary>
    internal bool IsCompatibleDestination => _sourceInstallation is { } source &&
        SelectedInstallation?.Info is { } target && BraveProfileRestorePlan.IsCompatible(source, target);
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private RestoreTargetInstallation? _selectedInstallation;
    [ObservableProperty] private RestoreTargetProfile? _selectedProfile;
    [ObservableProperty] private string _checkResult = "Not checked. Choose a destination and use Recheck.";
    [ObservableProperty] private string _extensionCheckResult = string.Empty;

    internal IReadOnlyList<ExtensionLocalRestoreSelection> ExtensionTargets { get; private set; } = [];

    public RestoreProfileChoice(ArchiveProfile source, IReadOnlyList<RestoreTargetInstallation> targets, Action changed)
        : this(source, targets, changed, changed) { }

    internal RestoreProfileChoice(ArchiveProfile source, IReadOnlyList<RestoreTargetInstallation> targets,
        Action changed, Action categoriesChanged)
    {
        SourceId = source.Id; Name = source.DisplayName + " (" + source.DirectoryName + ")";
        Categories = source.Categories.Select(category => new RestoreCategoryChoice(category, categoriesChanged)).ToArray();
        TargetInstallations = targets; _changed = changed;
    }
    internal void SetSourceCompatibility(ArchiveInstallation installation)
    {
        _sourceInstallation = installation;
        UpdateCategories();
    }

    private void UpdateCategories()
    {
        if (_sourceInstallation is null) return;
        var supported = IsCompatibleDestination;
        const string incompatible = "Choose a Linux destination with the same Brave product and version as the archive.";
        Categories.SingleOrDefault(category => category.Id == "bookmarks")?.SetCompatibility(supported, supported
            ? "Replaces the destination Bookmarks file. The restore engine checks the native snapshot before writing."
            : incompatible);
        Categories.SingleOrDefault(category => category.Id == "settings")?.SetCompatibility(supported, supported
            ? "Applies the stored bookmark bar, Home and startup choices to the destination Preferences."
            : incompatible);
        OnPropertyChanged(nameof(HasSupportedCategories));
    }

    /// <summary>Applies evidence for the current mapping and clears any selection when rechecking or remapping.</summary>
    internal void ApplyExtensionCheck(string description, IReadOnlyList<ExtensionLocalRestoreSelection> targets)
    {
        ExtensionCheckResult = description;
        ExtensionTargets = targets;
        var category = Categories.SingleOrDefault(choice => choice.Id == "extensions");
        category?.SetCompatibility(targets.Count != 0, targets.Count != 0
            ? $"Restore the {targets.Count} compatible Local store(s) shown in Recheck. Review lists each directory."
            : "Use Recheck to find existing Local stores for matching registered extensions.");
        OnPropertyChanged(nameof(HasSupportedCategories));
    }

    partial void OnIsSelectedChanged(bool value) => _changed();
    partial void OnSelectedInstallationChanged(RestoreTargetInstallation? value)
    {
        SelectedProfile = null;
        UpdateCategories();
        OnPropertyChanged(nameof(TargetProfiles));
        _changed();
    }
    partial void OnSelectedProfileChanged(RestoreTargetProfile? value)
    {
        OnPropertyChanged(nameof(DestinationPath)); _changed();
    }
    /// <summary>Selects every category supported for the current destination.</summary>
    [RelayCommand] private void SelectAllSupported()
    {
        foreach (var category in Categories.Where(category => category.IsSupported)) category.IsSelected = true;
    }
    /// <summary>Clears this profile's category selections.</summary>
    [RelayCommand] private void ClearCategories()
    {
        foreach (var category in Categories) category.IsSelected = false;
    }
}
/// <summary>Groups source profiles by archived installation, preserving archive-local identity.</summary>
public sealed record RestoreSourceGroup(string Name, RestoreProfileChoice[] Profiles);

/// <summary>Builds a write-free restore plan and rechecks target metadata/process state without enabling execution.</summary>
public partial class RestorePlanningViewModel : ViewModelBase, IDisposable
{
    private RestoreTargetInstallation[] _targets = [];
    private ArchiveManifest? _manifest;
    private string _sourcePath = string.Empty;
    private string? _sourceHash;
    private IReadOnlyDictionary<Guid, ArchivedExtensionInventory> _extensions =
        new Dictionary<Guid, ArchivedExtensionInventory>();
    internal event Action? ReviewInputsChanged;

    internal (string Path, Guid Id, string Hash)? ReviewedSource =>
        _manifest is { } manifest && _sourceHash is { } hash ? (_sourcePath, manifest.ArchiveId, hash) : null;
    internal IEnumerable<string> KnownBrowserRoots => _targets.Select(target => target.Info.UserDataDirectory);
    internal RestoreProfileChoice[] SelectedProfiles => Profiles.Where(profile => profile.IsSelected).ToArray();
    internal bool ReadyForReview => ReviewedSource is not null && !IsChecking && MappingError is null &&
        SelectedProfiles.All(profile => profile.Categories.Any(category => category.IsSelected && category.IsSupported));
    private CancellationTokenSource? _checking;
    private long _revision;
    private bool _disposed;
    public ObservableCollection<RestoreSourceGroup> Groups { get; } = [];
    public Func<BraveAppInfo, ProcessPresence>? ObserveBrowserState { get; set; }
    public bool HasSource => _manifest is not null;
    public string PlanSummary => string.Join("\n", Profiles.Where(p => p.IsSelected).Select(p =>
        p.Name + " → " + p.DestinationPath + "\n" + p.CheckResult));
    public string Availability => MappingError ?? (_sourceHash is null
        ? "Planning only. Restoration is unavailable for this source."
        : !HostEnvironment.IsLinux ? "Restore review currently supports Linux."
        : ReadyForReview ? "Review the selected categories and destinations." : "Choose a supported category for every selected source profile.");

    private string? MappingError
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
            return null;
        }
    }
    private IEnumerable<RestoreProfileChoice> Profiles => Groups.SelectMany(g => g.Profiles);
    private static StringComparer PathComparer => HostEnvironment.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    [ObservableProperty] private bool _isChecking;

    /// <summary>Replaces discovered target metadata and clears mappings rather than silently remapping across rediscovery.</summary>
    public void SetTargets(IEnumerable<RestoreTargetInstallation> targets)
    {
        _targets = targets.ToArray();
        if (_manifest is { } source) LoadCore(source, _sourcePath, _sourceHash, _extensions);
    }
    /// <summary>Loads independent source choices; archive names never select a destination.</summary>
    public void Load(ArchiveManifest manifest, string sourcePath) => LoadCore(manifest, sourcePath, null);

    internal void LoadInspected(BraveArchiveInspectionResult inspection)
    {
        if (inspection.Manifest is { } manifest) LoadCore(manifest, inspection.SourcePath, inspection.SourceSha256, inspection.ExtensionInventories);
    }

    private void LoadCore(ArchiveManifest manifest, string sourcePath, string? sourceHash,
        IReadOnlyDictionary<Guid, ArchivedExtensionInventory>? extensions = null)
    {
        Reset();
        _manifest = manifest;
        _sourcePath = sourcePath;
        _sourceHash = sourceHash;
        if (extensions is not null) _extensions = extensions;
        foreach (var installation in manifest.Installations)
        {
            var profiles = installation.Profiles.Select(profile =>
            {
                var choice = new RestoreProfileChoice(profile, _targets, Changed, Notify);
                if (sourceHash is not null) choice.SetSourceCompatibility(installation);
                return choice;
            }).ToArray();
            Groups.Add(new($"{installation.Product} {installation.Channel} — {installation.BrowserVersion}", profiles));
        }
        Notify();
    }
    private void Changed()
    {
        ++_revision; _checking?.Cancel(); IsChecking = false;
        foreach (var profile in Profiles)
        {
            profile.CheckResult = "Not checked. Use Recheck after changing the plan.";
            profile.ApplyExtensionCheck(string.Empty, []);
        }
        Notify();
    }
    private void Notify()
    {
        OnPropertyChanged(nameof(HasSource)); OnPropertyChanged(nameof(Availability)); OnPropertyChanged(nameof(PlanSummary));
        RecheckCommand.NotifyCanExecuteChanged();
        ReviewInputsChanged?.Invoke();
    }
    partial void OnIsCheckingChanged(bool value)
    {
        RecheckCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(Availability));
        ReviewInputsChanged?.Invoke();
    }
    private bool CanRecheck() => !_disposed && !IsChecking && Profiles.Any(p => p.IsSelected && p.SelectedProfile is not null);
    /// <summary>Checks frozen mapping identities, directory access and conservative host-wide Brave status off the UI thread.</summary>
    [RelayCommand(CanExecute = nameof(CanRecheck))]
    public async Task RecheckAsync()
    {
        if (!CanRecheck()) return;
        _checking?.Cancel(); using var cancellation = new CancellationTokenSource(); _checking = cancellation;
        var revision = ++_revision; var source = _sourcePath; var observer = ObserveBrowserState;
        var targets = _targets;
        var requests = Profiles.Where(profile => profile.IsSelected).Select(profile =>
            (Row: profile, App: profile.SelectedInstallation, Profile: profile.SelectedProfile,
                Extensions: _extensions.GetValueOrDefault(profile.SourceId), Compatible: profile.IsCompatibleDestination)).ToArray();
        IsChecking = true;
        foreach (var request in requests) request.Row.ApplyExtensionCheck(string.Empty, []);
        try
        {
            var results = await Task.Run(() => requests.Select(request =>
            {
                var token = cancellation.Token;
                token.ThrowIfCancellationRequested();
                if (request.App is null || request.Profile is null) return ("No destination selected.", ExtensionCheck.Empty);
                if (!targets.Any(target => ReferenceEquals(target, request.App)) ||
                    !request.App.Profiles.Any(profile => ReferenceEquals(profile, request.Profile)))
                    return ("Invalid destination mapping.", ExtensionCheck.Empty);
                try
                {
                    var path = BraveRestoreTargetGuard.Validate(request.App.Info, request.Profile.Info, source, token);
                    ProcessPresence Observe() => observer?.Invoke(request.App.Info) ?? ProcessPresenceProbe.Observe(["brave"], token);
                    var presence = Observe();
                    if (presence == ProcessPresence.Running)
                        return ("Running. Close Brave manually before restoration.", ExtensionCheck.Empty);
                    if (presence != ProcessPresence.Stopped)
                        return ("Unable to verify. Restoration must stay blocked.", ExtensionCheck.Empty);

                    var extensionResult = CompareExtensions(path, request.Extensions, request.Compatible, token);
                    if (request.Extensions is not null && request.Compatible && Observe() != ProcessPresence.Stopped)
                        return ("Unable to verify that Brave stayed closed. Run Recheck again.", ExtensionCheck.Empty);
                    return ("Closed (no Brave process observed). Target directory is readable; write compatibility is not established.",
                        extensionResult);
                }
                catch (OperationCanceledException) { throw; }
                catch { return ("Unable to verify target path, access or browser status. Restoration must stay blocked.", ExtensionCheck.Empty); }
            }).ToArray(), cancellation.Token);
            if (_disposed || revision != _revision) return;
            for (var index = 0; index < requests.Length; index++)
            {
                var (result, extensions) = results[index];
                requests[index].Row.CheckResult = result;
                requests[index].Row.ApplyExtensionCheck(extensions.Description, extensions.Targets);
            }
            Notify();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_checking, cancellation)) _checking = null;
            if (!_disposed && revision == _revision) IsChecking = false;
        }
    }
    private sealed record ExtensionCheck(string Description, IReadOnlyList<ExtensionLocalRestoreSelection> Targets)
    {
        internal static ExtensionCheck Empty { get; } = new(string.Empty, []);
    }

    private static ExtensionCheck CompareExtensions(string profilePath, ArchivedExtensionInventory? archived, bool compatible,
        CancellationToken token)
    {
        if (archived is null) return ExtensionCheck.Empty;
        if (archived.Count == 0) return new("The archive contains no extension identities for this profile.", []);
        if (!compatible) return new("Extension comparison requires a Linux destination with the same recorded Brave product and version.", []);
        if (archived.Entries is null) return new("This archive exceeds the limit for extension comparison details.", []);
        try
        {
            var checks = BraveExtensionRestoreAssessment.Check(profilePath, archived.Entries, token);
            var eligible = new Dictionary<string, ExtensionLocalRestoreSelection>(StringComparer.Ordinal);
            long remainingBytes = RestoreSafetyFormat.MaximumBytes;
            foreach (var check in checks)
                if (BraveExtensionLocalRestore.Review(check, profilePath, ref remainingBytes, token) is { } target)
                    eligible.Add(target.Id, target);
            const int displayLimit = 50;
            var matches = checks.Count(check => check.Match == ExtensionRegistrationMatch.PackageMatches);
            var lines = checks.Take(displayLimit).Select(check =>
                $"{check.Archive.Id}  ·  {DescribeRegistration(check)}  ·  Local {check.Archive.LocalStorage}, Sync {check.Archive.SyncStorage}" +
                DescribeLocal(check, eligible.ContainsKey(check.Archive.Id)));
            var summary = $"Archived extensions with matching registered packages: {matches} of {checks.Length}. Local and Sync show archive contents. " +
                          $"Local stores eligible for restore: {eligible.Count}.";
            if (checks.Length > displayLimit) summary += $" Showing the first {displayLimit} identities.";
            return new(summary + "\n" + string.Join("\n", lines), eligible.Values.ToArray());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        {
            return new("Unable to read the destination's extension registration.", []);
        }
    }

    private static string DescribeLocal(ExtensionRegistrationCheck check, bool eligible)
    {
        if (eligible) return "  ·  Local restore available";
        if (check.Archive.LocalStorage != "present" || check.Match != ExtensionRegistrationMatch.PackageMatches) return string.Empty;
        if (check.Archive.Packages.Length != 1) return "  ·  Archive contains multiple package versions";
        return check.Archive.LocalValidated
            ? "  ·  Destination Local store is missing, unsafe, or exceeds the limit"
            : "  ·  Archived Local store could not be validated";
    }

    private static string DescribeRegistration(ExtensionRegistrationCheck check) => check.Match switch
    {
        ExtensionRegistrationMatch.ArchiveHasNoPackage => "Archive contains storage only",
        ExtensionRegistrationMatch.RegistrationMissing => "No destination registration",
        ExtensionRegistrationMatch.UnsupportedRegistration => "Registration uses an unsupported location or format",
        ExtensionRegistrationMatch.VersionNotArchived => $"Registered version {check.RegisteredVersion} is absent from the archive",
        ExtensionRegistrationMatch.PackageDiffers => $"Registered version {check.RegisteredVersion} differs from the archived package",
        ExtensionRegistrationMatch.PackageMatches => $"Registered version {check.RegisteredVersion} matches the archived package",
        _ => "Registration or package could not be verified"
    };

    /// <summary>Selects only archived source profiles, independently of all live backup selections.</summary>
    [RelayCommand] private void SelectAllProfiles() { foreach (var profile in Profiles) profile.IsSelected = true; }
    /// <summary>Clears archived source selection without modifying browser data.</summary>
    [RelayCommand] private void ClearProfiles() { foreach (var profile in Profiles) profile.IsSelected = false; }
    /// <summary>Invalidates pending checks and releases the inspected source and all mappings.</summary>
    public void Reset()
    {
        ++_revision; _checking?.Cancel(); _checking = null; IsChecking = false;
        Groups.Clear(); _manifest = null; _sourcePath = string.Empty; _sourceHash = null;
        _extensions = new Dictionary<Guid, ArchivedExtensionInventory>();
        Notify();
    }
    public void Dispose() { if (_disposed) return; Reset(); _disposed = true; ObserveBrowserState = null; _targets = []; }
}
