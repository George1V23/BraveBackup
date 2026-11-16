using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace Core.Tests;

public class MainViewModelTests
{
    [Fact]
    public void AppChannelItem_DistinguishesOriginFromBrowser()
    {
        var browser = new BraveAppInfo(BraveAppChannel.Beta, "1.0", "/browser", "/data");
        var origin = browser with { Product = BraveAppProduct.Origin };

        Assert.Equal("Brave Beta (v1.0)", new AppChannelItem(browser).DisplayName);
        Assert.Equal("Brave Origin Beta (v1.0)", new AppChannelItem(origin).DisplayName);
    }

    [Fact]
    public void ProfileItem_LoadsSelectedAvatarAndUsesProfileHighlightColor()
    {
        var profile = new BraveProfileInfo("Profile", "/profile", "/profile/Bookmarks")
        {
            AvatarIconIndex = 38,
            ProfileHighlightColor = 0xFF123456
        };

        var item = new ProfileItem(profile);

        Assert.NotNull(item.AvatarImage);
        Assert.False(item.IsPlaceholderAvatar);
        Assert.Equal(Color.FromUInt32(0xFF123456), Assert.IsType<SolidColorBrush>(item.ProfileNameForeground).Color);
    }

    [Theory]
    [InlineData(56, "avatar_edgy_brave.png")]
    [InlineData(89, "avatar_bonbon_ninja.png")]
    public void ProfileItem_LoadsMatchingBraveAvatar(int avatarIndex, string assetName)
    {
        var profile = new BraveProfileInfo("Profile", "/profile", "/profile/Bookmarks")
        {
            AvatarIconIndex = avatarIndex,
            ProfileHighlightColor = 0xFF123456
        };

        var item = new ProfileItem(profile);

        Assert.NotNull(item.AvatarImage);
        Assert.False(item.IsPlaceholderAvatar);
        Assert.Equal(Color.FromUInt32(0xFF123456), Assert.IsType<SolidColorBrush>(item.ProfileNameForeground).Color);

        using var expectedStream = AssetLoader.Open(new Uri($"avares://UI/Assets/BraveAvatars/{assetName}"));
        using var expected = new Bitmap(expectedStream);
        using var expectedPng = new MemoryStream();
        using var actualPng = new MemoryStream();
        expected.Save(expectedPng, PngBitmapEncoderOptions.Default);
        item.AvatarImage!.Save(actualPng, PngBitmapEncoderOptions.Default);

        Assert.Equal(expectedPng.ToArray(), actualPng.ToArray());
    }

    [Fact]
    public void ProfileAvatarLoader_LoadsAllBraveAvatarIndices()
    {
        for (var index = 56; index < 90; index++)
        {
            using var avatar = ProfileAvatarLoader.Load(new BraveProfileInfo("Profile", "/profile", "/profile/Bookmarks")
            {
                AvatarIconIndex = index
            });

            Assert.NotNull(avatar);
        }
    }

    [Fact]
    public void ProfileItem_UsesProfilePictureAndFallsBackToBundledAvatarWhenUnreadable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "profile_avatar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var picturePath = Path.Combine(tempDir, "Google Profile Picture.png");
            using (var source = AssetLoader.Open(new Uri("avares://UI/Assets/ProfileAvatars/avatar-00.png")))
            using (var destination = File.Create(picturePath))
            {
                source.CopyTo(destination);
            }

            var withPicture = new BraveProfileInfo("Profile", tempDir, Path.Combine(tempDir, "Bookmarks"))
            {
                AvatarIconIndex = 38,
                AvatarPicturePath = picturePath
            };

            var pictureItem = new ProfileItem(withPicture);
            Assert.NotNull(pictureItem.AvatarImage);
            Assert.Equal(38, pictureItem.AvatarImage!.PixelSize.Width);

            File.WriteAllText(picturePath, "not an image");
            var fallbackItem = new ProfileItem(withPicture);
            Assert.NotNull(fallbackItem.AvatarImage);
            Assert.False(fallbackItem.IsPlaceholderAvatar);
            Assert.True(fallbackItem.AvatarImage!.PixelSize.Width > 1);
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
    public void ProfileItem_UsesColoredPlaceholderWhenNoAvatarImageIsAvailable()
    {
        var profile = new BraveProfileInfo("Profile", "/profile", "/profile/Bookmarks")
        {
            AvatarIconIndex = 26,
            ProfileHighlightColor = 0xFF123456,
            DefaultAvatarFillColor = 0xFFABCDEF,
            DefaultAvatarStrokeColor = 0xFF654321
        };

        var item = new ProfileItem(profile);

        Assert.Null(item.AvatarImage);
        Assert.True(item.IsPlaceholderAvatar);
        Assert.Equal(Color.FromUInt32(0xFFABCDEF), Assert.IsType<SolidColorBrush>(item.AvatarFillBrush).Color);
        Assert.Equal(Color.FromUInt32(0xFF654321), Assert.IsType<SolidColorBrush>(item.AvatarStrokeBrush).Color);
        Assert.Equal(Color.FromUInt32(0xFF123456), Assert.IsType<SolidColorBrush>(item.ProfileNameForeground).Color);
    }

    [Fact]
    public void ProfileItem_WithoutHighlightColorLeavesDefaultForegroundAvailable()
    {
        var item = new ProfileItem(new BraveProfileInfo("Profile", "/profile", "/profile/Bookmarks"));

        Assert.Null(item.ProfileNameForeground);
    }

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

            var profileDirectory = Path.Combine(tempDir, "profile");
            Directory.CreateDirectory(profileDirectory);
            var bookmarksFile = Path.Combine(profileDirectory, "Bookmarks");
            File.WriteAllText(bookmarksFile, sampleJson);

            var targetExportPath = Path.Combine(tempDir, "exported_bookmarks.json");

            var profile = new BraveProfileInfo("Person 1", Path.GetDirectoryName(bookmarksFile)!, bookmarksFile);
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
        Assert.Empty(item.ProfileDirectoryText);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnSelectedBetaAppChanged_WhenAppHasNoProfiles_AddsUnselectablePlaceholderToProfiles(
        bool createDataDirectory)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vm_empty_app_" + Guid.NewGuid().ToString("N"));
        if (createDataDirectory)
        {
            Directory.CreateDirectory(tempDir);
        }

        try
        {
            var appInfo = new BraveAppInfo(BraveAppChannel.Beta, "1.0.0", "/bin/brave-beta", tempDir);
            var appItem = new AppChannelItem(appInfo);

            var vm = new MainViewModel();
            vm.SelectedApp = appItem;

            Assert.Single(vm.Profiles);
            Assert.Equal("-- No profiles to export --", vm.Profiles[0].DisplayName);
            Assert.False(vm.Profiles[0].IsEnabled);
            Assert.Null(vm.Profiles[0].ProfileNameForeground);
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

    [Fact]
    public void OnSelectedAppChanged_SwitchingBetweenPopulatedAndUnconfiguredBetaShowsPlaceholder()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "vm_profiles_" + Guid.NewGuid().ToString("N"));
        var profileDirectory = Path.Combine(tempDir, "Default");
        Directory.CreateDirectory(profileDirectory);
        try
        {
            var bookmarksPath = Path.Combine(profileDirectory, "Bookmarks");
            File.WriteAllText(bookmarksPath, "{}");
            var vm = new MainViewModel();
            vm.SelectedApp = new AppChannelItem(new BraveAppInfo(
                BraveAppChannel.Stable, "1.0.0", "/bin/brave", tempDir));

            Assert.Single(vm.Profiles);
            Assert.Equal("Default", vm.Profiles[0].DisplayName);

            var betaDataDirectory = Path.Combine(tempDir, "beta-data");
            vm.SelectedApp = new AppChannelItem(new BraveAppInfo(
                BraveAppChannel.Beta, "1.0.0", "/bin/brave-beta", betaDataDirectory));

            Assert.Single(vm.Profiles);
            Assert.Equal("-- No profiles to export --", vm.Profiles[0].DisplayName);
            Assert.False(vm.Profiles[0].IsEnabled);
            Assert.Null(vm.SelectedProfile);

            vm.SelectedApp = new AppChannelItem(new BraveAppInfo(
                BraveAppChannel.Stable, "1.0.0", "/bin/brave", tempDir));

            Assert.Single(vm.Profiles);
            Assert.Equal("Default", vm.Profiles[0].DisplayName);
            Assert.Same(vm.Profiles[0], vm.SelectedProfile);
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