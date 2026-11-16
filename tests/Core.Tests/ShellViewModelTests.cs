using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Tests shell choices with injected discovery; no installed browser or real profile is required.</summary>
public sealed class ShellViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "BraveShellTests_" + Guid.NewGuid().ToString("N"));
    private readonly List<BraveAppInfo> _apps;
    private bool _failDetection;
    private bool _failBetaProfiles;
    private bool _includeNewProfile;

    public ShellViewModelTests()
    {
        Directory.CreateDirectory(_root);
        _apps = new()
        {
            new(BraveAppChannel.Stable, "1.0", Path.Combine(_root, "stable", "brave"),
                Path.Combine(_root, "stable-data")),
            new(BraveAppChannel.Beta, "2.0", Path.Combine(_root, "beta", "brave"), Path.Combine(_root, "beta-data"))
        };
    }

    private ShellViewModel CreateShell(Func<string, Task<string?>>? picker = null) => new(
        detect: () => _failDetection ? throw new IOException("fixture failure") : _apps.ToArray(),
        findProfiles: app =>
        {
            if (_failBetaProfiles && app.Channel == BraveAppChannel.Beta) throw new IOException("fixture failure");
            var profiles = new List<BraveProfileInfo>
            {
                new("Personal", Path.Combine(app.UserDataDirectory, "Default"),
                    Path.Combine(app.UserDataDirectory, "Default", "Bookmarks")),
                new("Work", Path.Combine(app.UserDataDirectory, "Profile 1"),
                    Path.Combine(app.UserDataDirectory, "Profile 1", "Bookmarks"))
            };
            if (_includeNewProfile)
                profiles.Add(new("New", Path.Combine(app.UserDataDirectory, "Profile 2"),
                    Path.Combine(app.UserDataDirectory, "Profile 2", "Bookmarks")));
            return profiles;
        },
        summarize: _ => new BraveProfileSummary(3, 2), pickBackupPath: picker,
        clock: () => new DateTime(2026, 11, 16, 10, 30, 0));

    [Fact]
    public async Task BackupSummary_FollowsScopeSelectionOptionsRefreshAndReset()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        var stable = shell.Installations[0];
        var beta = shell.Installations[1];
        shell.Activate(stable);
        Assert.Contains("No profiles selected", shell.BackupSelectionSummary);
        var changed = new List<string?>();
        shell.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        stable.Profiles[0].IsSelected = true;
        Assert.Contains("Personal", shell.BackupSelectionSummary);
        Assert.Contains(nameof(shell.BackupSelectionSummary), changed);
        Assert.DoesNotContain("Beta", shell.BackupSelectionSummary);
        stable.IsChecked = beta.IsChecked = true;
        beta.Profiles[1].IsSelected = true;
        shell.ExportAll();
        Assert.Contains("Beta: Work", shell.BackupSelectionSummary);
        shell.Options.Categories.Single(item => item.Id == "sessions").IsSelected = true;
        shell.Options.IncludeNativeSessionFiles = true;
        Assert.Contains("native session files", shell.BackupCategorySummary);
        await shell.RefreshAsync();
        Assert.Contains("Beta: Work", shell.BackupSelectionSummary);
        Assert.True(shell.Options.IncludeNativeSessionFiles);
        shell.ResetBackupForm();
        Assert.DoesNotContain("Personal", shell.BackupSelectionSummary);
        Assert.Equal("Bookmarks", shell.BackupCategorySummary);
        Assert.Contains("Unavailable", shell.BackupSizeEstimate);
        Assert.Contains("Select at least one profile", shell.BackupAvailability);
    }

    [Fact]
    public async Task Status_UsesRealDiscoveryExportAndActiveVersionState()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        Assert.Equal("Ready", shell.StatusText);
        shell.Activate(shell.Installations[1]);
        Assert.Equal("v2.0", shell.ActiveVersionText);
        Assert.False(string.IsNullOrWhiteSpace(shell.OperatingSystemText));
        shell.SelectedTabIndex = ShellViewModel.ExportTabIndex;
        shell.ExportViewModel.IsBusy = true;
        Assert.Contains("Exporting", shell.StatusText);
        shell.ExportViewModel.IsBusy = false;
        shell.ExportViewModel.IsStatusError = true;
        Assert.Contains("attention", shell.StatusText);
        shell.SelectedTabIndex = ShellViewModel.BackupTabIndex;
        Assert.Equal("Ready", shell.StatusText);
        _failDetection = true;
        await shell.RefreshAsync();
        Assert.Contains("Could not refresh", shell.StatusText);
    }

    [Fact]
    public async Task ActivationBulkSelectionAndExpansion_AreIndependent()
    {
        using var shell = CreateShell();
        Assert.Empty(shell.Installations);
        await shell.RefreshAsync();
        Assert.Equal(2, shell.Installations.Count);
        Assert.Null(shell.ActiveInstallation);
        Assert.False(shell.HasBackupScope);
        Assert.Empty(shell.ActiveDataDirectory);
        Assert.Empty(shell.ActiveProfiles);
        var stable = shell.Installations[0];
        var beta = shell.Installations[1];
        stable.ActivateCommand.Execute(null);
        Assert.Same(stable, shell.ActiveInstallation);
        Assert.True(stable.IsActive);
        Assert.Equal(stable.AppInfo.UserDataDirectory, shell.ActiveDataDirectory);
        Assert.Equal(stable.Profiles, shell.ActiveProfiles);
        Assert.False(stable.IsChecked);
        Assert.False(stable.IsExpanded);
        stable.IsExpanded = true;
        beta.ToggleBulkSelectionCommand.Execute(null);
        Assert.Same(beta, shell.ActiveInstallation);
        Assert.True(beta.IsChecked);
        Assert.False(stable.IsActive);
        Assert.True(stable.IsExpanded);
        Assert.True(shell.HasBulkSelection);
        Assert.Same(beta, Assert.Single(shell.BackupInstallations));
        beta.ToggleBulkSelectionCommand.Execute(null);
        Assert.False(shell.HasBulkSelection);
    }

    [Fact]
    public async Task ExportAll_GroupsCheckedInstallationsAndProfileSelectAllUsesOnlyCurrentScope()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        var stable = shell.Installations[0];
        var beta = shell.Installations[1];
        stable.ToggleBulkSelectionCommand.Execute(null);
        beta.ToggleBulkSelectionCommand.Execute(null);
        shell.ExportAllCommand.Execute(null);
        Assert.True(shell.IsBulkMode);
        Assert.Equal(0, shell.SelectedTabIndex);
        Assert.Equal(2, shell.BackupInstallations.Count);
        Assert.Equal(false, shell.AllProfilesSelected);
        stable.Profiles[0].IsSelected = true;
        Assert.Null(shell.AllProfilesSelected);
        shell.AllProfilesSelected = true;
        Assert.All(shell.BackupInstallations.SelectMany(app => app.Profiles),
            profile => Assert.True(profile.IsSelected));
        stable.ActivateCommand.Execute(null);
        Assert.False(shell.IsBulkMode);
        shell.AllProfilesSelected = false;
        Assert.All(stable.Profiles, profile => Assert.False(profile.IsSelected));
        Assert.All(beta.Profiles, profile => Assert.True(profile.IsSelected));
        Assert.All(shell.Installations, app => Assert.True(app.IsChecked));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public async Task SidebarProfile_OpensProfilesTabWithoutCheckingForBackup()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        var profile = shell.Installations[1].Profiles[1];
        profile.OpenDetailsCommand.Execute(null);
        Assert.Same(profile.Installation, shell.ActiveInstallation);
        Assert.Same(profile, shell.FocusedProfile);
        Assert.Equal(ShellViewModel.ProfilesTabIndex, shell.SelectedTabIndex);
        Assert.False(profile.IsSelected);
        Assert.False(profile.Installation.IsChecked);
        Assert.Equal("3", profile.BookmarkCountText);
        Assert.Equal("2", profile.ExtensionCountText);
        Assert.Equal("Unavailable", profile.LastUsedText);
        Assert.True(profile.Installation.Profiles[0].IsDefaultDirectory);
        Assert.False(profile.IsDefaultDirectory);
    }

    [Fact]
    public async Task Refresh_PreservesMatchingStateAndFormChoicesWhileAddingUncheckedProfiles()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        var stable = shell.Installations[0];
        stable.IsExpanded = true;
        stable.ToggleBulkSelectionCommand.Execute(null);
        stable.Profiles[1].OpenDetailsCommand.Execute(null);
        stable.Profiles[1].IsSelected = true;
        shell.Options.BackupPath = "chosen.bravebackup";
        shell.Options.Compression = "High";
        shell.ExportAllCommand.Execute(null);
        _apps[0] = _apps[0] with { Version = "1.1" };
        _includeNewProfile = true;
        await shell.RefreshAsync();
        var refreshed = shell.Installations[0];
        Assert.NotSame(stable, refreshed);
        Assert.Equal("1.1", refreshed.AppInfo.Version);
        Assert.Same(refreshed, shell.ActiveInstallation);
        Assert.True(refreshed.IsActive);
        Assert.True(refreshed.IsChecked);
        Assert.True(refreshed.IsExpanded);
        Assert.True(refreshed.Profiles[1].IsSelected);
        Assert.False(refreshed.Profiles[2].IsSelected);
        Assert.Same(refreshed.Profiles[1], shell.FocusedProfile);
        Assert.True(shell.IsBulkMode);
        Assert.Equal("chosen.bravebackup", shell.Options.BackupPath);
        Assert.Equal("High", shell.Options.Compression);
    }

    [Fact]
    public async Task Refresh_RemovesMissingActiveInstallationButRetainsRemainingBulkScope()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Installations[0].ToggleBulkSelectionCommand.Execute(null);
        shell.Installations[1].ToggleBulkSelectionCommand.Execute(null);
        shell.ExportAllCommand.Execute(null);
        _apps.RemoveAt(1);
        await shell.RefreshAsync();
        Assert.Null(shell.ActiveInstallation);
        Assert.Null(shell.FocusedProfile);
        Assert.True(shell.HasBackupScope);
        Assert.Single(shell.BackupInstallations);
        shell.Installations[0].IsChecked = false;
        Assert.False(shell.IsBulkMode);
        Assert.False(shell.HasBackupScope);
        Assert.False(shell.HasBulkSelection);
        shell.Installations[0].IsChecked = true;
        shell.ExportAllCommand.Execute(null);
        Assert.Same(shell.Installations[0], shell.ActiveInstallation);
        Assert.True(shell.HasActiveInstallation);
        Assert.True(shell.IsBulkMode);
    }

    [Fact]
    public async Task FailedRefresh_RetainsExistingSnapshotAndProfileReadErrorsAreIsolated()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        var stable = shell.Installations[0];
        stable.ActivateCommand.Execute(null);
        _failDetection = true;
        await shell.RefreshAsync();
        Assert.Same(stable, shell.Installations[0]);
        Assert.Same(stable, shell.ActiveInstallation);
        Assert.False(shell.IsRefreshing);
        Assert.Contains("retained", shell.DiscoveryMessage);
        _failDetection = false;
        _failBetaProfiles = true;
        await shell.RefreshAsync();
        Assert.Equal(2, shell.Installations[0].Profiles.Count);
        Assert.Empty(shell.Installations[1].Profiles);
        Assert.Contains("could not be read", shell.Installations[1].DiscoveryMessage);
    }

    [Fact]
    public async Task Reset_ClearsFormAndProfileChoicesWhilePreservingSidebarStateAndBulkMode()
    {
        using var shell = CreateShell();
        await shell.RefreshAsync();
        shell.Installations[0].ToggleBulkSelectionCommand.Execute(null);
        shell.Installations[1].ToggleBulkSelectionCommand.Execute(null);
        shell.ExportAllCommand.Execute(null);
        shell.AllProfilesSelected = true;
        shell.Options.AllCategoriesSelected = true;
        shell.Options.BackupPath = "custom.bravebackup";
        shell.Options.Compression = "None";
        shell.Options.Password = "fixture-password";
        shell.Options.PasswordConfirmation = "fixture-password";
        shell.Options.RevealPassword = true;
        var active = shell.ActiveInstallation;
        shell.ResetBackupFormCommand.Execute(null);
        Assert.Same(active, shell.ActiveInstallation);
        Assert.True(shell.IsBulkMode);
        Assert.All(shell.Installations, app => Assert.True(app.IsChecked));
        Assert.All(shell.Installations.SelectMany(app => app.Profiles), profile => Assert.False(profile.IsSelected));
        Assert.Equal("BraveBackup_2026-11-16_103000.bravebackup", shell.Options.BackupPath);
        Assert.True(shell.Options.EncryptBackup);
        Assert.Equal("Balanced", shell.Options.Compression);
        Assert.Equal(string.Empty, shell.Options.Password);
        Assert.Equal(string.Empty, shell.Options.PasswordConfirmation);
        Assert.False(shell.Options.RevealPassword);
        Assert.Equal("bookmarks", Assert.Single(shell.Options.Categories.Where(item => item.IsSelected)).Id);
    }

    [Fact]
    public async Task Browse_UsesSuggestedFilenameAndCancellationKeepsPreviousChoice()
    {
        string? response = null;
        string? suggestion = null;
        using var shell = CreateShell(path =>
        {
            suggestion = path;
            return Task.FromResult(response);
        });
        var original = shell.Options.BackupPath;
        await shell.BrowseBackupPathAsync();
        Assert.Equal(original, suggestion);
        Assert.Equal(original, shell.Options.BackupPath);
        response = Path.Combine(_root, "chosen.bravebackup");
        await shell.BrowseBackupPathAsync();
        Assert.Equal(response, shell.Options.BackupPath);
        Assert.False(File.Exists(response));
    }

    [Fact]
    public async Task DisposedShell_CannotRepopulateFromAnInFlightScan()
    {
        using var gate = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shell = new ShellViewModel(detect: () =>
        {
            entered.SetResult();
            gate.Wait();
            return _apps;
        });
        var pending = shell.RefreshAsync();
        await entered.Task;
        try
        {
            await shell.RefreshAsync();
            shell.Options.Password = "fixture-secret";
            shell.Dispose();
            Assert.Empty(shell.Options.Password);
        }
        finally
        {
            gate.Set();
        }

        await pending;
        Assert.Empty(shell.Installations);
        Assert.False(shell.IsRefreshing);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}