using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using BraveBackup.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UI.ViewModels;

public sealed class AppChannelItem
{
    public BraveAppInfo AppInfo { get; }
    public string DisplayName { get; }

    public AppChannelItem(BraveAppInfo appInfo)
    {
        AppInfo = appInfo;
        DisplayName = $"{appInfo.Channel} (v{appInfo.Version})";
    }

    public override string ToString() => DisplayName;
}

public sealed class ProfileItem
{
    public BraveProfileInfo ProfileInfo { get; }
    public bool HasBookmarks { get; }
    public string DisplayName { get; }
    public string StatusDescription { get; }

    public ProfileItem(BraveProfileInfo profileInfo)
    {
        ProfileInfo = profileInfo;
        HasBookmarks = BraveProfileFinder.HasBookmarks(profileInfo);
        DisplayName = profileInfo.ProfileName;
        StatusDescription = HasBookmarks ? "Bookmarks available" : "No bookmarks found";
    }

    public override string ToString() => DisplayName;
}

public partial class MainViewModel : ViewModelBase
{
    private readonly Func<Task<IStorageFile?>>? _saveFileDialogPicker;

    [ObservableProperty]
    private ObservableCollection<AppChannelItem> _detectedApps = new();

    [ObservableProperty]
    private AppChannelItem? _selectedApp;

    [ObservableProperty]
    private ObservableCollection<ProfileItem> _profiles = new();

    [ObservableProperty]
    private ProfileItem? _selectedProfile;

    [ObservableProperty]
    private string _appSelectionPlaceholder = string.Empty;

    [ObservableProperty]
    private string _dataDirectoryText = string.Empty;

    [ObservableProperty]
    private string _targetFilePath = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isStatusError;

    [ObservableProperty]
    private bool _isBusy;

    public MainViewModel() : this(null)
    {
    }

    public MainViewModel(Func<Task<IStorageFile?>>? saveFileDialogPicker)
    {
        _saveFileDialogPicker = saveFileDialogPicker;
        LoadInstalledApps();
    }

    public void LoadInstalledApps()
    {
        StatusMessage = string.Empty;
        IsStatusError = false;
        DetectedApps.Clear();
        Profiles.Clear();
        SelectedApp = null;
        SelectedProfile = null;

        try
        {
            var apps = BraveAppDetector.DetectInstalledApps();
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
            Profiles.Clear();
            SelectedProfile = null;
            StatusMessage = string.Empty;
        }
    }

    partial void OnSelectedProfileChanged(ProfileItem? value)
    {
        if (value != null)
        {
            var safeProfileName = string.Join("_", value.ProfileInfo.ProfileName.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(TargetFilePath) || TargetFilePath.StartsWith("brave-bookmarks-", StringComparison.OrdinalIgnoreCase))
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
        Profiles.Clear();
        SelectedProfile = null;

        if (appInfo == null)
        {
            return;
        }

        try
        {
            var discoveredProfiles = BraveProfileFinder.FindProfiles(appInfo);
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
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error finding profiles: {ex.Message}";
            IsStatusError = true;
        }
    }

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

    public bool CanExportBookmarks()
    {
        return !IsBusy &&
               SelectedProfile != null &&
               SelectedProfile.HasBookmarks &&
               !string.IsNullOrWhiteSpace(TargetFilePath);
    }

    [RelayCommand(CanExecute = nameof(CanExportBookmarks))]
    public async Task ExportBookmarksAsync()
    {
        if (SelectedProfile == null || string.IsNullOrWhiteSpace(TargetFilePath))
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
            var bookmarksJson = await File.ReadAllTextAsync(SelectedProfile.ProfileInfo.BookmarksPath);
            var backupModel = BraveBookmarksExporter.ExportJson(bookmarksJson);
            await BraveBookmarksExporter.ExportToFileAsync(backupModel, TargetFilePath);

            StatusMessage = $"Successfully exported bookmarks to {TargetFilePath}!";
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

    [RelayCommand]
    public void Refresh()
    {
        LoadInstalledApps();
    }
}
