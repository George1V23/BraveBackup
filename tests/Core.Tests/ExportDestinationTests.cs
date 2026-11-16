using System.Text.Json;
using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Verifies that displayed destinations and actual JSON output agree independently of the launch directory.</summary>
public sealed class ExportDestinationTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BraveDestination_" + Guid.NewGuid().ToString("N"));

    private readonly BraveAppInfo _app;
    private readonly BraveProfileInfo[] _profiles;
    private string ExportDirectory => Path.Combine(_root, "exports");

    public ExportDestinationTests()
    {
        _app = new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"),
            Path.Combine(_root, "user-data"));
        _profiles = new[]
        {
            new BraveProfileInfo("Personal", Path.Combine(_app.UserDataDirectory, "Default"),
                Path.Combine(_app.UserDataDirectory, "Default", "Bookmarks")),
            new BraveProfileInfo("Work", Path.Combine(_app.UserDataDirectory, "Profile 1"),
                Path.Combine(_app.UserDataDirectory, "Profile 1", "Bookmarks"))
        };
        foreach (var profile in _profiles)
        {
            Directory.CreateDirectory(profile.ProfileDirectory);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"),
                profile.BookmarksPath);
        }
    }

    private MainViewModel CreateExporter(Func<Task<Avalonia.Platform.Storage.IStorageFile?>>? picker = null)
    {
        var model = new MainViewModel(picker, () => new[] { _app }, _ => _profiles, ExportDirectory);
        model.SelectedApp = model.DetectedApps[0];
        return model;
    }

    [Fact]
    public async Task DefaultDestination_IsAbsoluteVisibleAndOnlyCreatedByExport()
    {
        using var model = CreateExporter();
        var destination = model.FullDestinationPath;
        Assert.True(Path.IsPathFullyQualified(model.TargetFilePath));
        Assert.Equal(Path.Combine(ExportDirectory, "brave-bookmarks-personal.json"), destination);
        Assert.Equal(destination, model.TargetFilePath);
        Assert.False(Directory.Exists(ExportDirectory));
        await model.ExportBookmarksCommand.ExecuteAsync(null);
        Assert.False(model.IsStatusError);
        Assert.True(File.Exists(destination));
        Assert.Contains(destination, model.StatusMessage);
        Assert.Equal(1, JsonSerializer.Deserialize<BackupModel>(File.ReadAllText(destination))!.SchemaVersion);
    }

    [Fact]
    public async Task RelativeInput_IsResolvedAgainstTheExportBaseAndWrittenToTheDisplayedPath()
    {
        using var model = CreateExporter();
        model.TargetFilePath = Path.Combine("nested", "chosen.json");
        var shownDestination = model.FullDestinationPath;
        Assert.Equal(Path.Combine(ExportDirectory, "nested", "chosen.json"), shownDestination);
        Assert.True(model.CanExportBookmarks());
        await model.ExportBookmarksCommand.ExecuteAsync(null);
        Assert.False(model.IsStatusError);
        Assert.Equal(shownDestination, model.TargetFilePath);
        Assert.True(File.Exists(shownDestination));
        Assert.Contains(shownDestination, model.StatusMessage);
    }

    [Fact]
    public void ProfileChanges_UpdateTheSuggestedAbsoluteNameButPreserveCustomPrefixNames()
    {
        using var model = CreateExporter();
        model.SelectedProfile = model.Profiles[1];
        Assert.Equal(Path.Combine(ExportDirectory, "brave-bookmarks-work.json"), model.TargetFilePath);
        model.TargetFilePath = "brave-bookmarks-custom.json";
        model.SelectedProfile = model.Profiles[0];
        Assert.Equal("brave-bookmarks-custom.json", model.TargetFilePath);
        Assert.Equal(Path.Combine(ExportDirectory, "brave-bookmarks-custom.json"), model.FullDestinationPath);
        Assert.False(Directory.Exists(ExportDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid\0name.json")]
    public async Task InvalidDestination_ShowsValidationAndDoesNotSilentlyReturn(string destination)
    {
        using var model = CreateExporter();
        model.TargetFilePath = destination;
        Assert.Empty(model.FullDestinationPath);
        Assert.False(model.CanExportBookmarks());
        Assert.NotEmpty(model.DestinationValidationMessage);
        await model.ExportBookmarksAsync();
        Assert.True(model.IsStatusError);
        Assert.Equal(model.DestinationValidationMessage, model.StatusMessage);
        Assert.False(Directory.Exists(ExportDirectory));
    }

    [Fact]
    public async Task FolderAndIoFailures_ShowErrorsAndPreserveExistingData()
    {
        using var model = CreateExporter();
        model.TargetFilePath = _root;
        await model.ExportBookmarksAsync();
        Assert.True(model.IsStatusError);
        Assert.Contains("folder", model.StatusMessage);
        var blockingFile = Path.Combine(_root, "blocked");
        File.WriteAllText(blockingFile, "keep this file");
        model.TargetFilePath = Path.Combine(blockingFile, "export.json");
        await model.ExportBookmarksAsync();
        Assert.True(model.IsStatusError);
        Assert.Contains("Export failed", model.StatusMessage);
        Assert.False(model.IsBusy);
        Assert.Equal("keep this file", File.ReadAllText(blockingFile));
    }

    [Fact]
    public async Task PickerFailure_IsReportedAndKeepsTheDestinationUsable()
    {
        using var model = CreateExporter(() => throw new IOException("Fixture save dialog failure"));
        var original = model.TargetFilePath;
        await model.BrowseExportPathAsync();
        Assert.True(model.IsStatusError);
        Assert.Contains("Could not choose", model.StatusMessage);
        Assert.Equal(original, model.TargetFilePath);
        Assert.True(model.CanExportBookmarks());
    }

    [Fact]
    public void DestinationChanges_NotifyTheFullPathAndValidationBindings()
    {
        using var model = CreateExporter();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        model.TargetFilePath = "another.json";
        Assert.Contains(nameof(model.FullDestinationPath), notifications);
        Assert.Contains(nameof(model.DestinationValidationMessage), notifications);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}