using System.Text.Json;
using BraveBackup.Core;
using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Exercises export policy on isolated fixtures, never a real browser profile.</summary>
public sealed class ExportSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BraveExportSafety_" + Guid.NewGuid().ToString("N"));
    private static BackupModel EmptyBackup => new(1, Array.Empty<BackupBookmark>());

    public ExportSafetyTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("Bookmarks")]
    [InlineData("Local State")]
    [InlineData("Preferences")]
    [InlineData("Secure Preferences")]
    [InlineData("Login Data")]
    public async Task Export_RejectsRecognizableProfileAndDescendantsWithoutChangingFiles(string marker)
    {
        var profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(profile);
        var original = Path.Combine(profile, marker);
        File.WriteAllText(original, "original browser data");

        Assert.Throws<InvalidOperationException>(() => BraveBookmarksExporter.ExportToFile(EmptyBackup, original));
        var nestedTarget = Path.Combine(profile, "nested", "backup.json");
        await Assert.ThrowsAsync<InvalidOperationException>(() => BraveBookmarksExporter.ExportToFileAsync(EmptyBackup, nestedTarget));

        Assert.Equal("original browser data", File.ReadAllText(original));
        Assert.False(Directory.Exists(Path.GetDirectoryName(nestedTarget)));
        Assert.Single(Directory.GetFiles(profile));
    }

    [Fact]
    public void Guard_ProtectsCustomRootAndNormalizesTraversalButAllowsSiblingPrefix()
    {
        var profile = Path.Combine(_root, "profile");
        Assert.Throws<InvalidOperationException>(() => ExportDestinationGuard.Validate(
            Path.Combine(profile, "missing", "..", "backup.json"), new[] { profile }));
        Assert.Throws<InvalidOperationException>(() => ExportDestinationGuard.Validate(profile, new[] { profile }));
        var sibling = Path.Combine(_root, "profile-backups", "backup.json");
        Assert.Equal(sibling, ExportDestinationGuard.Validate(sibling, new[] { profile }));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void Guard_ProtectsStandardRootsWithoutWritingToThem()
    {
        var config = HostEnvironment.IsWindows ? HostEnvironment.LocalApplicationData : HostEnvironment.ConfigurationDirectory;
        foreach (var product in new[] { "Brave-Browser", "Brave-Origin" })
        foreach (var channel in new[] { "", "-Beta", "-Nightly" })
        {
            var root = Path.Combine(config, "BraveSoftware", product + channel);
            if (HostEnvironment.IsWindows) root = Path.Combine(root, "User Data");
            Assert.Throws<InvalidOperationException>(() => ExportDestinationGuard.Validate(Path.Combine(root, "Default", "backup.json")));
        }
    }

    [Fact]
    public async Task Export_ReplacesAnExistingBackupOutsideProfilesAndRemovesTemporaryFiles()
    {
        var destination = Path.Combine(_root, "backup.json");
        File.WriteAllText(destination, "older backup");
        BraveBookmarksExporter.ExportToFile(EmptyBackup, destination);
        Assert.Equal(1, JsonSerializer.Deserialize<BackupModel>(File.ReadAllText(destination))!.SchemaVersion);
        await BraveBookmarksExporter.ExportToFileAsync(EmptyBackup, destination);
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Export_CancellationPreservesExistingBackupAndCreatesNoNewDirectory()
    {
        var destination = Path.Combine(_root, "backup.json");
        File.WriteAllText(destination, "older backup");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BraveBookmarksExporter.ExportToFileAsync(EmptyBackup, destination, cancellation.Token));
        var newDestination = Path.Combine(_root, "missing", "backup.json");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BraveBookmarksExporter.ExportToFileAsync(EmptyBackup, newDestination, cancellation.Token));
        Assert.Equal("older backup", File.ReadAllText(destination));
        Assert.False(Directory.Exists(Path.GetDirectoryName(newDestination)));
        Assert.Single(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task Export_FailedReplacementCleansUpTemporaryFile()
    {
        var destination = Path.Combine(_root, "directory-not-file");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
        await Assert.ThrowsAnyAsync<IOException>(() => BraveBookmarksExporter.ExportToFileAsync(EmptyBackup, destination));
        Assert.Empty(Directory.GetFiles(_root));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
    }

    [Fact]
    public async Task ViewModel_RefusesExportInsideSelectedProfileAndReportsError()
    {
        var source = Path.Combine(_root, "Bookmarks");
        var content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"));
        File.WriteAllText(source, content);
        var viewModel = new MainViewModel
        {
            SelectedExportFormat = BookmarkExportFormatOption.Json,
            SelectedProfile = new ProfileItem(new BraveProfileInfo("Test", _root, source)),
            TargetFilePath = source
        };
        await viewModel.ExportBookmarksAsync();
        Assert.True(viewModel.IsStatusError);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("outside browser", viewModel.StatusMessage);
        Assert.Equal(content, File.ReadAllText(source));
    }

    [Fact]
    public async Task ViewModel_ProtectsSelectedInstallationRootEvenWithoutMarkers()
    {
        var userData = Path.Combine(_root, "custom-user-data");
        var profileDirectory = Path.Combine(userData, "Default");
        Directory.CreateDirectory(profileDirectory);
        var source = Path.Combine(profileDirectory, "Bookmarks");
        File.WriteAllText(source, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json")));
        var viewModel = new MainViewModel
        {
            SelectedExportFormat = BookmarkExportFormatOption.Json,
            SelectedApp = new AppChannelItem(new BraveAppInfo(BraveAppChannel.Stable, "test", "unused", userData)),
            SelectedProfile = new ProfileItem(new BraveProfileInfo("Test", profileDirectory, source)),
            TargetFilePath = Path.Combine(userData, "unconfigured-profile", "backup.json")
        };
        await viewModel.ExportBookmarksAsync();
        Assert.True(viewModel.IsStatusError);
        Assert.False(Directory.Exists(Path.GetDirectoryName(viewModel.TargetFilePath)));
    }

    [Fact]
    public void LegacyFixture_RemainsReadableAndExportsTheSameVersionOneValues()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var legacyJson = File.ReadAllText(Path.Combine(fixtureDirectory, "Backup.v1.json"));
        var model = JsonSerializer.Deserialize<BackupModel>(legacyJson)!;
        Assert.Equal(1, model.SchemaVersion);
        Assert.Equal(BackupBookmarkType.Url, model.Bookmarks[0].Children[0].Type);
        Assert.Equal("Café", model.Bookmarks[2].Children[0].Name);

        var exported = BraveBookmarksExporter.ExportJson(File.ReadAllText(Path.Combine(fixtureDirectory, "Bookmarks.sample.json")));
        Assert.Equal(1, BraveBookmarksExporter.CurrentSchemaVersion);
        using var expected = JsonDocument.Parse(legacyJson);
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(exported, BraveBookmarksExporter.JsonOptions));
        Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    }

    [Fact]
    public void Assemblies_KeepCorePortableAndBrowserServicesOutsideUi()
    {
        var core = typeof(BackupModel).Assembly;
        var backup = typeof(BraveBookmarksExporter).Assembly;
        var platform = typeof(HostEnvironment).Assembly;
        Assert.Equal("Core", core.GetName().Name);
        Assert.Equal("Backup", backup.GetName().Name);
        Assert.Equal("Platform", platform.GetName().Name);
        Assert.DoesNotContain(core.GetReferencedAssemblies(), reference => reference.Name is "Backup" or "Platform" or "UI");
        Assert.DoesNotContain(backup.GetReferencedAssemblies(), reference => reference.Name is "UI" or "Avalonia");
        Assert.DoesNotContain(platform.GetReferencedAssemblies(), reference => reference.Name is "Core" or "Backup" or "UI");
        Assert.All(core.GetExportedTypes(), type => Assert.DoesNotContain("BraveProfile", type.Name));
    }
}
