using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

/// <summary>
/// Represents a selectable Brave Browser application channel in the UI.
/// </summary>
public sealed class AppChannelItem
{
    /// <summary>
    /// Gets the underlying detected Brave application information.
    /// </summary>
    public BraveAppInfo AppInfo { get; }

    /// <summary>
    /// Gets the formatted display name of the application channel and version.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="AppChannelItem"/> class.
    /// </summary>
    /// <param name="appInfo">The detected application metadata.</param>
    public AppChannelItem(BraveAppInfo appInfo)
    {
        AppInfo = appInfo;
        DisplayName =
            $"Brave {(appInfo.Product == BraveAppProduct.Origin ? "Origin " : "")}{appInfo.Channel} (v{appInfo.Version})";
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>
/// Represents a selectable Brave browser profile or unselectable placeholder item in the UI.
/// </summary>
public sealed class ProfileItem
{
    /// <summary>
    /// Gets the underlying profile information, or <c>null</c> if this item is a placeholder.
    /// </summary>
    public BraveProfileInfo? ProfileInfo { get; }

    /// <summary>Gets the profile directory or an empty string for an unselectable placeholder.</summary>
    public string ProfileDirectoryText => ProfileInfo?.ProfileDirectory ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether bookmarks are present for this profile.
    /// </summary>
    public bool HasBookmarks { get; }

    /// <summary>
    /// Gets the display name of the profile or placeholder text.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets a descriptive text for the profile bookmark availability.
    /// </summary>
    public string StatusDescription { get; }

    /// <summary>
    /// Gets a value indicating whether this item can be selected in the list.
    /// </summary>
    public bool IsEnabled { get; }

    /// <summary>Gets the selected account picture or built-in avatar image, or <see langword="null"/> for a generated placeholder.</summary>
    public Bitmap? AvatarImage { get; }

    /// <summary>Gets whether a discovered profile has no loaded avatar image and should display the generated placeholder.</summary>
    public bool IsPlaceholderAvatar { get; }

    /// <summary>Gets a brush for the stored profile highlight color, or <see langword="null"/> to retain the UI's default text color.</summary>
    public IBrush? ProfileNameForeground { get; }

    /// <summary>Gets the generated placeholder fill brush, preferring its stored color, then the profile highlight, then the UI default.</summary>
    public IBrush AvatarFillBrush { get; }

    /// <summary>Gets the generated placeholder stroke brush, preferring its stored color, then the profile highlight, then the UI default.</summary>
    public IBrush AvatarStrokeBrush { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileItem"/> class for a discovered profile.
    /// </summary>
    /// <param name="profileInfo">The discovered profile metadata.</param>
    public ProfileItem(BraveProfileInfo profileInfo)
    {
        ProfileInfo = profileInfo;
        HasBookmarks = BraveProfileFinder.HasBookmarks(profileInfo);
        DisplayName = profileInfo.ProfileName;
        StatusDescription = HasBookmarks ? "Bookmarks available" : "No bookmarks found";
        IsEnabled = true;
        AvatarImage = ProfileAvatarLoader.Load(profileInfo);
        IsPlaceholderAvatar = AvatarImage == null;
        ProfileNameForeground = CreateBrush(profileInfo.ProfileHighlightColor);
        AvatarFillBrush =
            CreateBrush(profileInfo.DefaultAvatarFillColor ?? profileInfo.ProfileHighlightColor ?? 0xFFE8EAEDu)!;
        AvatarStrokeBrush =
            CreateBrush(profileInfo.DefaultAvatarStrokeColor ?? profileInfo.ProfileHighlightColor ?? 0xFF5F6368u)!;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileItem"/> class for a placeholder message.
    /// </summary>
    /// <param name="message">The placeholder text to display.</param>
    /// <param name="isEnabled">Whether the item is enabled/selectable (defaults to <c>false</c>).</param>
    public ProfileItem(string message, bool isEnabled = false)
    {
        ProfileInfo = null;
        HasBookmarks = false;
        DisplayName = message;
        StatusDescription = string.Empty;
        IsEnabled = isEnabled;
        AvatarImage = null;
        IsPlaceholderAvatar = false;
        ProfileNameForeground = null;
        AvatarFillBrush = CreateBrush(0xFFE8EAEDu)!;
        AvatarStrokeBrush = CreateBrush(0xFF5F6368u)!;
    }

    /// <summary>Creates a solid brush for an ARGB color when one is available.</summary>
    /// <param name="color">The optional unsigned ARGB color.</param>
    /// <returns>A solid color brush, or <see langword="null"/> when no color was stored.</returns>
    private static IBrush? CreateBrush(uint? color)
    {
        return color is { } value ? new SolidColorBrush(Color.FromUInt32(value)) : null;
    }

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>
/// Main view model managing application detection, profile selection, and bookmark export operations.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly Func<Task<IStorageFile?>>? _saveFileDialogPicker;
    private readonly Func<IReadOnlyList<BraveAppInfo>> _detectInstalledApps;
    private readonly Func<BraveAppInfo, IReadOnlyList<BraveProfileInfo>> _findProfiles;

    /// <summary>
    /// Collection of detected Brave installations.
    /// </summary>
    [ObservableProperty] private ObservableCollection<AppChannelItem> _detectedApps = new();

    /// <summary>
    /// Currently selected Brave installation.
    /// </summary>
    [ObservableProperty] private AppChannelItem? _selectedApp;

    /// <summary>
    /// Discovered profiles for the selected Brave installation.
    /// </summary>
    [ObservableProperty] private ObservableCollection<ProfileItem> _profiles = new();

    /// <summary>
    /// Currently selected profile.
    /// </summary>
    [ObservableProperty] private ProfileItem? _selectedProfile;

    /// <summary>
    /// Placeholder text for the application selection dropdown.
    /// </summary>
    [ObservableProperty] private string _appSelectionPlaceholder = string.Empty;

    /// <summary>
    /// Formatted status text showing the data directory or detected installations count.
    /// </summary>
    [ObservableProperty] private string _dataDirectoryText = string.Empty;

    /// <summary>
    /// Target file path where the backup will be saved.
    /// </summary>
    [ObservableProperty] private string _targetFilePath = string.Empty;

    /// <summary>
    /// User-facing status message.
    /// </summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>
    /// Indicates whether the current status message represents an error.
    /// </summary>
    [ObservableProperty] private bool _isStatusError;

    /// <summary>
    /// Indicates whether an asynchronous operation (such as export) is in progress.
    /// </summary>
    [ObservableProperty] private bool _isBusy;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class.
    /// </summary>
    public MainViewModel() : this(null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class with an optional file picker callback.
    /// </summary>
    /// <param name="saveFileDialogPicker">Optional delegate to invoke file save dialog.</param>
    public MainViewModel(Func<Task<IStorageFile?>>? saveFileDialogPicker)
        : this(saveFileDialogPicker, BraveAppDetector.DetectInstalledApps, BraveProfileFinder.FindProfiles)
    {
    }

    /// <summary>Creates an exporter using injected discovery, allowing an embedded view to share shell results.</summary>
    /// <param name="saveFileDialogPicker">The view's optional native JSON save picker.</param>
    /// <param name="detectInstalledApps">Returns the current read-only installation snapshot.</param>
    /// <param name="findProfiles">Returns profiles for the chosen installation without changing browser data.</param>
    public MainViewModel(Func<Task<IStorageFile?>>? saveFileDialogPicker,
        Func<IReadOnlyList<BraveAppInfo>> detectInstalledApps,
        Func<BraveAppInfo, IReadOnlyList<BraveProfileInfo>> findProfiles)
    {
        _saveFileDialogPicker = saveFileDialogPicker;
        _detectInstalledApps = detectInstalledApps ?? throw new ArgumentNullException(nameof(detectInstalledApps));
        _findProfiles = findProfiles ?? throw new ArgumentNullException(nameof(findProfiles));
        LoadInstalledApps();
    }

    /// <summary>
    /// Scans the system for installed Brave applications and initializes view model state.
    /// </summary>
    public void LoadInstalledApps()
    {
        StatusMessage = string.Empty;
        IsStatusError = false;
        DetectedApps.Clear();
        ClearProfiles();
        SelectedApp = null;
        SelectedProfile = null;

        try
        {
            var apps = _detectInstalledApps();
            foreach (var app in apps)
            {
                DetectedApps.Add(new AppChannelItem(app));
            }

            AppSelectionPlaceholder = $"[{DetectedApps.Count} applications]";
            DataDirectoryText = $"Data directory: [installed on {DetectedApps.Count} paths]";

            if (DetectedApps.Count == 0)
            {
                StatusMessage = "No Brave Browser installations detected.";
                IsStatusError = false;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error detecting installations: {ex.Message}";
            IsStatusError = true;
        }
    }

    partial void OnSelectedAppChanged(AppChannelItem? value)
    {
        if (value != null)
        {
            DataDirectoryText = $"Data directory: {value.AppInfo.UserDataDirectory}";
            LoadProfilesForApp(value.AppInfo);
        }
        else
        {
            DataDirectoryText = $"Data directory: [installed on {DetectedApps.Count} paths]";
            ClearProfiles();
            SelectedProfile = null;
            StatusMessage = string.Empty;
        }
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value != null && value.ProfileInfo != null)
        {
            var safeProfileName = string.Join("_", value.ProfileInfo.ProfileName.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(TargetFilePath) ||
                TargetFilePath.StartsWith("brave-bookmarks-", StringComparison.OrdinalIgnoreCase))
            {
                TargetFilePath = $"brave-bookmarks-{safeProfileName.ToLowerInvariant()}.json";
            }
        }

        ExportBookmarksCommand.NotifyCanExecuteChanged();
    }

    partial void OnTargetFilePathChanged(string value)
    {
        ExportBookmarksCommand.NotifyCanExecuteChanged();
    }

    private void LoadProfilesForApp(BraveAppInfo? appInfo)
    {
        ClearProfiles();
        SelectedProfile = null;

        if (appInfo == null)
        {
            return;
        }

        try
        {
            var discoveredProfiles = _findProfiles(appInfo);
            foreach (var profile in discoveredProfiles)
            {
                Profiles.Add(new ProfileItem(profile));
            }

            if (Profiles.Count > 0)
            {
                SelectedProfile = Profiles[0];
            }
            else
            {
                StatusMessage = $"No profiles found in {appInfo.UserDataDirectory}";
                IsStatusError = false;
                Profiles.Add(new ProfileItem("-- No profiles to export --", isEnabled: false));
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error finding profiles: {ex.Message}";
            IsStatusError = true;
            Profiles.Add(new ProfileItem($"Error finding profiles: {ex.Message}", isEnabled: false));
        }
    }

    /// <summary>
    /// Opens the file picker dialog to choose a destination file path for export.
    /// </summary>
    [RelayCommand]
    public async Task BrowseExportPathAsync()
    {
        if (_saveFileDialogPicker != null)
        {
            var file = await _saveFileDialogPicker();
            if (file != null)
            {
                TargetFilePath = file.Path.LocalPath;
            }
        }
    }

    /// <summary>
    /// Determines whether the export bookmarks action can currently execute.
    /// </summary>
    /// <returns><c>true</c> if valid export parameters and profile are selected; otherwise <c>false</c>.</returns>
    public bool CanExportBookmarks()
    {
        return !IsBusy &&
               SelectedProfile != null &&
               SelectedProfile.IsEnabled &&
               SelectedProfile.ProfileInfo != null &&
               SelectedProfile.HasBookmarks &&
               !string.IsNullOrWhiteSpace(TargetFilePath);
    }

    /// <summary>
    /// Exports bookmarks for the selected profile to the chosen destination path.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanExportBookmarks))]
    public async Task ExportBookmarksAsync()
    {
        if (SelectedProfile == null || SelectedProfile.ProfileInfo == null || string.IsNullOrWhiteSpace(TargetFilePath))
        {
            return;
        }

        if (!File.Exists(SelectedProfile.ProfileInfo.BookmarksPath))
        {
            StatusMessage = "Bookmarks file not found for selected profile.";
            IsStatusError = true;
            return;
        }

        IsBusy = true;
        ExportBookmarksCommand.NotifyCanExecuteChanged();
        StatusMessage = "Exporting bookmarks...";
        IsStatusError = false;

        try
        {
            var profile = SelectedProfile.ProfileInfo;
            var destination = TargetFilePath;
            var protectedDirectories = new List<string> { profile.ProfileDirectory };
            foreach (var app in DetectedApps)
                protectedDirectories.Add(app.AppInfo.UserDataDirectory);
            if (SelectedApp != null) protectedDirectories.Add(SelectedApp.AppInfo.UserDataDirectory);
            ExportDestinationGuard.Validate(destination, protectedDirectories);
            var bookmarksJson = await File.ReadAllTextAsync(profile.BookmarksPath);
            var backupModel = BraveBookmarksExporter.ExportJson(bookmarksJson);
            await BraveBookmarksExporter.ExportToFileAsync(backupModel, destination);

            StatusMessage = $"Successfully exported bookmarks to {destination}!";
            IsStatusError = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export failed: {ex.Message}";
            IsStatusError = true;
        }
        finally
        {
            IsBusy = false;
            ExportBookmarksCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// Reloads installed Brave applications and refreshes the view model state.
    /// </summary>
    [RelayCommand]
    public void Refresh()
    {
        LoadInstalledApps();
    }

    /// <summary>Releases this exporter's owned avatar images and clears stale profile selections.</summary>
    private void ClearProfiles()
    {
        SelectedProfile = null;
        foreach (var profile in Profiles) profile.AvatarImage?.Dispose();
        Profiles.Clear();
    }

    /// <summary>Releases profile presentation resources when the embedded exporter or compatibility window closes.</summary>
    public void Dispose() => ClearProfiles();
}