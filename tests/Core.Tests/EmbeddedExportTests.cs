using System.Text.Json;
using Avalonia.Platform.Storage;
using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Exercises the embedded exporter with isolated installations and real bookmark fixtures.</summary>
public sealed class EmbeddedExportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "BraveEmbeddedExport_" + Guid.NewGuid().ToString("N"));

    private readonly List<BraveAppInfo> _apps;
    private bool _emptyProfiles;
    private bool _renameWork;

    public EmbeddedExportTests()
    {
        Directory.CreateDirectory(_root);
        _apps = new()
        {
            new(BraveAppChannel.Stable, "1.0", Path.Combine(_root, "stable", "brave"),
                Path.Combine(_root, "stable-data")),
            new(BraveAppChannel.Beta, "2.0", Path.Combine(_root, "beta", "brave"), Path.Combine(_root, "beta-data"))
        };
        foreach (var app in _apps)
        foreach (var directory in new[] { "Default", "Profile 1" })
        {
            var profile = Path.Combine(app.UserDataDirectory, directory);
            Directory.CreateDirectory(profile);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"),
                Path.Combine(profile, "Bookmarks"));
        }
    }

    private ShellViewModel CreateShell() => new(
        detect: () => _apps.ToArray(),
        findProfiles: app => _emptyProfiles
            ? Array.Empty<BraveProfileInfo>()
            : new[]
            {
                new BraveProfileInfo("Personal", Path.Combine(app.UserDataDirectory, "Default"),
                    Path.Combine(app.UserDataDirectory, "Default", "Bookmarks")),
                new BraveProfileInfo(_renameWork ? "Renamed Work" : "Work",
                    Path.Combine(app.UserDataDirectory, "Profile 1"),
                    Path.Combine(app.UserDataDirectory, "Profile 1", "Bookmarks"))
            },
        summarize: _ => new BraveProfileSummary(3, 0));

    [Fact]
    public async Task Export_WritesBookmarkJsonUsingTheActiveInstallationWithoutWritingAnArchive()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
        shell.SelectedTabIndex = ShellViewModel.ExportTabIndex;
        var exporter = shell.ExportViewModel;
        var source = exporter.SelectedProfile!.ProfileInfo!.BookmarksPath;
        var original = File.ReadAllText(source);
        var destination = Path.Combine(_root, "exports", "bookmarks.json");
        exporter.TargetFilePath = destination;
        shell.Options.BackupPath = Path.Combine(_root, "separate.bravebackup");
        shell.Options.AllCategoriesSelected = true;
        Assert.True(exporter.CanExportBookmarks());
        await exporter.ExportBookmarksAsync();
        Assert.False(exporter.IsStatusError);
        Assert.Contains("Successfully exported", exporter.StatusMessage);
        var model = JsonSerializer.Deserialize<BackupModel>(File.ReadAllText(destination))!;
        Assert.Equal(1, model.SchemaVersion);
        Assert.Equal("https://example.test/", model.Bookmarks[0].Children[0].Url);
        Assert.Equal(original, File.ReadAllText(source));
        Assert.False(File.Exists(shell.Options.BackupPath));
        Assert.Equal(ShellViewModel.ExportTabIndex, shell.SelectedTabIndex);
    }

    [Fact]
    public async Task Export_StillRejectsProfileDestinationsInTheEmbeddedWorkflow()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
        var exporter = shell.ExportViewModel;
        var source = exporter.SelectedProfile!.ProfileInfo!.BookmarksPath;
        var original = File.ReadAllText(source);
        exporter.TargetFilePath = source;
        await exporter.ExportBookmarksAsync();
        Assert.True(exporter.IsStatusError);
        Assert.Equal(original, File.ReadAllText(source));
    }

    [Fact]
    public async Task NavigationAndRefresh_KeepExporterScopedAndRetainSurvivingProfileAndCustomTarget()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
        var exporter = shell.ExportViewModel;
        exporter.SelectedProfile = exporter.Profiles[1];
        var selectedPath = exporter.SelectedProfile.ProfileInfo!.ProfileDirectory;
        var destination = Path.Combine(_root, "chosen.json");
        exporter.TargetFilePath = destination;
        _renameWork = true;
        _apps[0] = _apps[0] with { Version = "1.1" };
        await shell.RefreshAsync();
        Assert.Equal("1.1", exporter.SelectedApp!.AppInfo.Version);
        Assert.Equal(selectedPath, exporter.SelectedProfile!.ProfileInfo!.ProfileDirectory);
        Assert.Equal("Renamed Work", exporter.SelectedProfile.DisplayName);
        Assert.Equal(destination, exporter.TargetFilePath);
        shell.Activate(shell.Installations[1]);
        Assert.Equal(_apps[1], exporter.SelectedApp!.AppInfo);
        Assert.All(exporter.Profiles,
            profile => Assert.StartsWith(_apps[1].UserDataDirectory, profile.ProfileInfo!.ProfileDirectory));
        Assert.Equal(destination, exporter.TargetFilePath);
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task RemovedActiveInstallation_ClearsTheExporterSoItCannotUseAStaleProfile()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
        _apps.RemoveAt(0);
        await shell.RefreshAsync();
        Assert.False(shell.HasActiveInstallation);
        Assert.Null(shell.ExportViewModel.SelectedApp);
        Assert.Null(shell.ExportViewModel.SelectedProfile);
        Assert.Empty(shell.ExportViewModel.Profiles);
        Assert.False(shell.ExportViewModel.CanExportBookmarks());
    }

    [Fact]
    public async Task EmptyProfiles_RemainUnselectedAndExportStaysDisabledAcrossRefresh()
    {
        using var shell = CreateShell();
        _emptyProfiles = true;
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
        await shell.RefreshAsync();
        Assert.Null(shell.ExportViewModel.SelectedProfile);
        Assert.False(Assert.Single(shell.ExportViewModel.Profiles).IsEnabled);
        Assert.False(shell.ExportViewModel.CanExportBookmarks());
    }

    [Fact]
    public async Task Browse_UsesTheHostPickerAndCancellationPreservesTheExportPath()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
        var calls = 0;
        var original = shell.ExportViewModel.TargetFilePath;
        shell.ExportFilePicker = () =>
        {
            calls++;
            return Task.FromResult<IStorageFile?>(null);
        };
        await shell.ExportViewModel.BrowseExportPathAsync();
        Assert.Equal(1, calls);
        Assert.Equal(original, shell.ExportViewModel.TargetFilePath);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}