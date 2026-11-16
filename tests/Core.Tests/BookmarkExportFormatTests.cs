using System.Text.Json;
using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Verifies primary browser HTML and the retained JSON choice through the real export view model.</summary>
public sealed class BookmarkExportFormatTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BraveFormatChoice_" + Guid.NewGuid().ToString("N"));

    private readonly BraveAppInfo _app;
    private readonly BraveProfileInfo[] _profiles;

    public BookmarkExportFormatTests()
    {
        _app = new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"),
            Path.Combine(_root, "data"));
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

    private MainViewModel CreateModel()
    {
        var model = new MainViewModel(null, () => new[] { _app }, _ => _profiles, Path.Combine(_root, "exports"));
        model.SelectedApp = model.DetectedApps[0];
        return model;
    }

    [Fact]
    public async Task DefaultChoice_IsBrowserHtmlAndWritesImportableBookmarkMarkup()
    {
        using var model = CreateModel();
        Assert.Same(BookmarkExportFormatOption.BrowserHtml, model.ExportFormats[0]);
        Assert.Same(model.ExportFormats[0], model.SelectedExportFormat);
        Assert.EndsWith(".html", model.TargetFilePath);
        Assert.Equal("Export Bookmarks as HTML", model.ExportButtonText);
        var source = File.ReadAllText(_profiles[0].BookmarksPath);
        await model.ExportBookmarksCommand.ExecuteAsync(null);
        Assert.False(model.IsStatusError);
        var html = File.ReadAllText(model.FullDestinationPath);
        Assert.StartsWith("<!DOCTYPE NETSCAPE-Bookmark-file-1>", html);
        Assert.Contains("PERSONAL_TOOLBAR_FOLDER=\"true\"", html);
        Assert.Contains("https://example.test/cafe", html);
        Assert.Equal(source, File.ReadAllText(_profiles[0].BookmarksPath));
    }

    [Fact]
    public async Task SwitchingToJson_UpdatesPathAndPreservesTheExistingVersionOneFormat()
    {
        using var model = CreateModel();
        var directory = Path.GetDirectoryName(model.TargetFilePath);
        model.SelectedExportFormat = BookmarkExportFormatOption.Json;
        Assert.Equal(directory, Path.GetDirectoryName(model.TargetFilePath));
        Assert.EndsWith(".json", model.TargetFilePath);
        Assert.Equal("Export Bookmarks as JSON", model.ExportButtonText);
        await model.ExportBookmarksCommand.ExecuteAsync(null);
        var data = JsonSerializer.Deserialize<BackupModel>(File.ReadAllText(model.FullDestinationPath))!;
        Assert.Equal(1, data.SchemaVersion);
        Assert.Equal("https://example.test/", data.Bookmarks[0].Children[0].Url);
        Assert.False(model.IsStatusError);
    }

    [Fact]
    public void SwitchingFormats_PreservesCustomFolderAndBasenameAndProfileSelection()
    {
        using var model = CreateModel();
        model.TargetFilePath = Path.Combine(_root, "chosen", "my-bookmarks.html");
        model.SelectedProfile = model.Profiles[1];
        var selectedProfile = model.SelectedProfile;
        model.SelectedExportFormat = BookmarkExportFormatOption.Json;
        Assert.Equal(Path.Combine(_root, "chosen", "my-bookmarks.json"), model.TargetFilePath);
        Assert.Same(selectedProfile, model.SelectedProfile);
        model.SelectedExportFormat = BookmarkExportFormatOption.BrowserHtml;
        model.SelectedProfile = model.Profiles[0];
        Assert.Equal(Path.Combine(_root, "chosen", "my-bookmarks.html"), model.TargetFilePath);
        Assert.False(Directory.Exists(Path.Combine(_root, "chosen")));
    }

    [Fact]
    public async Task ConflictingKnownExtension_ShowsExplanationInsteadOfWritingTheWrongFormat()
    {
        using var model = CreateModel();
        model.TargetFilePath = Path.Combine(_root, "wrong.json");
        Assert.False(model.CanExportBookmarks());
        Assert.Contains("HTML", model.DestinationValidationMessage);
        await model.ExportBookmarksAsync();
        Assert.True(model.IsStatusError);
        Assert.False(File.Exists(model.FullDestinationPath));
        model.SelectedExportFormat = BookmarkExportFormatOption.Json;
        Assert.False(model.IsStatusError);
        Assert.Empty(model.StatusMessage);
        Assert.True(model.CanExportBookmarks());
        await model.ExportBookmarksAsync();
        Assert.False(model.IsStatusError);
        Assert.Equal(1,
            JsonSerializer.Deserialize<BackupModel>(File.ReadAllText(model.FullDestinationPath))!.SchemaVersion);
    }

    [Fact]
    public void FormatChanges_NotifyTheActionAndPickerMetadataWhileUpdatingSuggestedNames()
    {
        using var model = CreateModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        model.SelectedExportFormat = BookmarkExportFormatOption.Json;
        model.SelectedProfile = model.Profiles[1];
        Assert.EndsWith("brave-bookmarks-work.json", model.TargetFilePath);
        Assert.Contains(nameof(model.ExportButtonText), notifications);
        Assert.Contains(nameof(model.ExportFormatDescription), notifications);
        Assert.Contains(nameof(model.ExportPathPlaceholder), notifications);
        Assert.Equal("json", model.SelectedExportFormat.Extension);
        Assert.Equal("application/json", model.SelectedExportFormat.MimeType);
        model.SelectedExportFormat = BookmarkExportFormatOption.BrowserHtml;
        Assert.EndsWith("brave-bookmarks-work.html", model.TargetFilePath);
        Assert.Equal("text/html", model.SelectedExportFormat.MimeType);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}