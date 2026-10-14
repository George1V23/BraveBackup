using System;
using System.IO;
using System.Threading.Tasks;
using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace Core.Tests;

public class MainViewModelTests
{
    [Fact]
    public void CanExportBookmarks_ReturnsFalseWhenNoProfileSelected()
    {
        var vm = new MainViewModel();
        vm.SelectedProfile = null;
        vm.TargetFilePath = "test.json";

        Assert.False(vm.CanExportBookmarks());
    }

    [Fact]
    public void CanExportBookmarks_ReturnsFalseWhenTargetFileIsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var bookmarksFile = Path.Combine(tempDir, "Bookmarks");
            File.WriteAllText(bookmarksFile, "{}");

            var profile = new BraveProfileInfo("Default", tempDir, bookmarksFile);
            var vm = new MainViewModel();
            vm.SelectedProfile = new ProfileItem(profile);
            vm.TargetFilePath = "";

            Assert.False(vm.CanExportBookmarks());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void CanExportBookmarks_ReturnsFalseWhenProfileHasNoBookmarks()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var bookmarksFile = Path.Combine(tempDir, "NonExistentBookmarks");
            var profile = new BraveProfileInfo("Default", tempDir, bookmarksFile);
            var vm = new MainViewModel();
            vm.SelectedProfile = new ProfileItem(profile);
            vm.TargetFilePath = "output.json";

            Assert.False(vm.CanExportBookmarks());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task ExportBookmarksAsync_ExportsBookmarksSuccessfully()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var sampleFixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json");
            var sampleJson = File.ReadAllText(sampleFixture);

            var bookmarksFile = Path.Combine(tempDir, "Bookmarks");
            File.WriteAllText(bookmarksFile, sampleJson);

            var targetExportPath = Path.Combine(tempDir, "exported_bookmarks.json");

            var profile = new BraveProfileInfo("Person 1", tempDir, bookmarksFile);
            var vm = new MainViewModel();
            vm.SelectedProfile = new ProfileItem(profile);
            vm.TargetFilePath = targetExportPath;

            Assert.True(vm.CanExportBookmarks());

            await vm.ExportBookmarksAsync();

            Assert.False(vm.IsStatusError);
            Assert.Contains("Successfully exported", vm.StatusMessage);
            Assert.True(File.Exists(targetExportPath));

            var exportedContent = await File.ReadAllTextAsync(targetExportPath);
            Assert.Contains("Bookmarks bar", exportedContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void LoadInstalledApps_SetsInitialPlaceholderAndDataDirectoryText()
    {
        var vm = new MainViewModel();

        Assert.Null(vm.SelectedApp);
        Assert.Equal($"[{vm.DetectedApps.Count} applications]", vm.AppSelectionPlaceholder);
        Assert.Equal($"Data directory: [installed on {vm.DetectedApps.Count} paths]", vm.DataDirectoryText);
    }

    [Fact]
    public void Refresh_ResetsSelectionAndInitialTexts()
    {
        var vm = new MainViewModel();
        if (vm.DetectedApps.Count > 0)
        {
            vm.SelectedApp = vm.DetectedApps[0];
            Assert.Equal($"Data directory: {vm.SelectedApp.AppInfo.UserDataDirectory}", vm.DataDirectoryText);
        }

        vm.Refresh();

        Assert.Null(vm.SelectedApp);
        Assert.Equal($"[{vm.DetectedApps.Count} applications]", vm.AppSelectionPlaceholder);
        Assert.Equal($"Data directory: [installed on {vm.DetectedApps.Count} paths]", vm.DataDirectoryText);
    }

    [Fact]
    public void ProfileItem_PlaceholderConstructor_InitializesUnselectableItem()
    {
        var item = new ProfileItem("No profiles to export", isEnabled: false);

        Assert.Equal("No profiles to export", item.DisplayName);
        Assert.Null(item.ProfileInfo);
        Assert.False(item.HasBookmarks);
        Assert.False(item.IsEnabled);
        Assert.Equal(string.Empty, item.StatusDescription);
    }

    [Fact]
    public void CanExportBookmarks_ReturnsFalseWhenProfileIsUnselectablePlaceholder()
    {
        var vm = new MainViewModel();
        vm.SelectedProfile = new ProfileItem("No profiles to export", isEnabled: false);
        vm.TargetFilePath = "output.json";

        Assert.False(vm.CanExportBookmarks());
    }

    [Fact]
    public void OnSelectedAppChanged_WhenAppHasNoProfiles_AddsUnselectablePlaceholderToProfiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vm_empty_app_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var appInfo = new BraveAppInfo(BraveAppChannel.Stable, "1.0.0", "/bin/brave", tempDir);
            var appItem = new AppChannelItem(appInfo);

            var vm = new MainViewModel();
            vm.SelectedApp = appItem;

            Assert.Single(vm.Profiles);
            Assert.Equal("No profiles to export", vm.Profiles[0].DisplayName);
            Assert.False(vm.Profiles[0].IsEnabled);
            Assert.Null(vm.SelectedProfile);
            Assert.False(vm.CanExportBookmarks());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }
}