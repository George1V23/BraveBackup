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
}
