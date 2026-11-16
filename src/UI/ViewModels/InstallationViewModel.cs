using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>Holds one profile's presentation, independent backup selection and profile-navigation command.</summary>
public partial class ProfileSelectionViewModel : ViewModelBase, IDisposable
{
    /// <summary>Gets the existing avatar/color presentation, preserving established behavior.</summary>
    public ProfileItem Presentation { get; }

    /// <summary>Gets the discovered profile.</summary>
    public BraveProfileInfo ProfileInfo { get; }

    /// <summary>Gets the installation owning this profile.</summary>
    public InstallationViewModel Installation { get; }

    /// <summary>Gets the profile display name.</summary>
    public string DisplayName => ProfileInfo.ProfileName;

    /// <summary>Gets the on-disk directory's final component.</summary>
    public string DirectoryName => Path.GetFileName(Path.TrimEndingDirectorySeparator(ProfileInfo.ProfileDirectory));

    /// <summary>Gets whether the profile uses Chromium's Default directory, not whether it was most recently active.</summary>
    public bool IsDefaultDirectory => DirectoryName == "Default";

    /// <summary>Gets a URL-bookmark count or an explicit unavailable state.</summary>
    public string BookmarkCountText { get; }

    /// <summary>Gets a count of discovered extensions, not an enabled-extension count.</summary>
    public string ExtensionCountText { get; }

    /// <summary>Gets an unavailable state until a verified last-use metadata reader is implemented.</summary>
    public string LastUsedText => "Unavailable";

    /// <summary>Gets the command opening the Profiles tab without changing backup selection.</summary>
    public IRelayCommand OpenDetailsCommand { get; }

    /// <summary>Gets or sets this profile's backup checkbox.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Creates a profile row from read-only discovery results.</summary>
    public ProfileSelectionViewModel(InstallationViewModel installation, BraveProfileInfo info,
        BraveProfileSummary summary, Action<ProfileSelectionViewModel> openDetails)
    {
        Installation = installation;
        ProfileInfo = info;
        Presentation = new ProfileItem(info);
        BookmarkCountText = summary.BookmarkCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unavailable";
        ExtensionCountText = summary.ExtensionCount?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unavailable";
        OpenDetailsCommand = new RelayCommand(() => openDetails(this));
    }

    /// <summary>Releases a loaded profile avatar when discovery results are replaced.</summary>
    public void Dispose() => Presentation.AvatarImage?.Dispose();
}

/// <summary>Represents a sidebar installation with separate active, expanded and bulk-export states.</summary>
public partial class InstallationViewModel : ViewModelBase, IDisposable
{
    /// <summary>Gets immutable installation metadata.</summary>
    public BraveAppInfo AppInfo { get; }

    /// <summary>Gets the product/channel label without conflating version and identity.</summary>
    public string DisplayName =>
        $"Brave {(AppInfo.Product == BraveAppProduct.Origin ? "Origin " : string.Empty)}{AppInfo.Channel}";

    /// <summary>Gets the version presentation.</summary>
    public string VersionText => $"v{AppInfo.Version}";

    /// <summary>Gets the profiles belonging to this installation.</summary>
    public ObservableCollection<ProfileSelectionViewModel> Profiles { get; } = new();

    /// <summary>Gets the single-click activation command.</summary>
    public IRelayCommand ActivateCommand { get; }

    /// <summary>Gets the double-click/keyboard bulk selection command.</summary>
    public IRelayCommand ToggleBulkSelectionCommand { get; }

    /// <summary>Gets a discovery-specific empty or error message.</summary>
    public string DiscoveryMessage { get; }

    /// <summary>Gets or sets whether this installation drives the main workspace.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Gets or sets whether the sidebar reveals installation details/profiles.</summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Gets or sets whether this installation participates in Export All.</summary>
    [ObservableProperty] private bool _isChecked;

    /// <summary>Creates a sidebar entry with shell-owned activation and bulk-selection actions.</summary>
    public InstallationViewModel(BraveAppInfo app, string discoveryMessage,
        Action<InstallationViewModel> activate, Action<InstallationViewModel> toggleBulk)
    {
        AppInfo = app;
        DiscoveryMessage = discoveryMessage;
        ActivateCommand = new RelayCommand(() => activate(this));
        ToggleBulkSelectionCommand = new RelayCommand(() => toggleBulk(this));
    }

    /// <summary>Releases profile avatars when an installation snapshot is replaced.</summary>
    public void Dispose()
    {
        foreach (var profile in Profiles) profile.Dispose();
    }
}