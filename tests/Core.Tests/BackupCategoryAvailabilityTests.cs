using BraveBackup.Extensions.TabManagerPlus;
using BraveBackup.Platform;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Backup tab: steps 2+ appear only with a selected profile, and categories follow what the selected profiles contain.</summary>
public sealed class BackupCategoryAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CategoryAvailability_" + Guid.NewGuid().ToString("N"));
    private string Profile(string name, params string[] categories)
    {
        var directory = Path.Combine(_root, "data", name); Directory.CreateDirectory(directory);
        foreach (var category in categories)
            switch (category)
            {
                case "bookmarks": File.WriteAllText(Path.Combine(directory, "Bookmarks"), "{\"roots\":{},\"version\":1}"); break;
                case "settings": File.WriteAllText(Path.Combine(directory, "Preferences"), "{}"); break;
                case "passwords": File.WriteAllText(Path.Combine(directory, "Login Data For Account"), "x"); break;
                case "extensions": Directory.CreateDirectory(Path.Combine(directory, "Extensions", "id")); break;
                case "sessions": Directory.CreateDirectory(Path.Combine(directory, "Sessions")); File.WriteAllText(Path.Combine(directory, "Sessions", "Session_1"), "x"); break;
                case "tabmanagerplus":
                    var storage = Path.Combine(directory, "Local Extension Settings", SavedWindows.OfficialExtensionId);
                    Directory.CreateDirectory(storage); File.WriteAllText(Path.Combine(storage, "000003.log"), "x"); break;
            }
        return directory;
    }
    private async Task<ShellViewModel> Shell(params (string Name, string Directory)[] profiles)
    {
        var app = new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), Path.Combine(_root, "data"));
        var shell = new ShellViewModel(() => [app], _ => profiles.Select(p => new BraveProfileInfo(p.Name, p.Directory, Path.Combine(p.Directory, "Bookmarks"))).ToArray(),
            _ => new BraveProfileSummary(0, 0));
        await shell.RefreshAsync(); shell.Activate(shell.Installations[0]); return shell;
    }

    [Fact]
    public void DetectionReportsOnlyExistingDataAndTreatsEmptyOrMissingAsAbsent()
    {
        Assert.Empty(BraveProfileCategoryAvailability.Detect(Profile("empty")));
        Assert.Empty(BraveProfileCategoryAvailability.Detect(Path.Combine(_root, "missing")));
        Directory.CreateDirectory(Path.Combine(Profile("hollow"), "Extensions")); Directory.CreateDirectory(Path.Combine(_root, "data", "hollow", "Sessions"));
        File.WriteAllText(Path.Combine(_root, "data", "hollow", "Sessions", "unrelated"), "x");
        Assert.Empty(BraveProfileCategoryAvailability.Detect(Path.Combine(_root, "data", "hollow")));
        var all = new[] { "bookmarks", "settings", "passwords", "extensions", "sessions", "tabmanagerplus" };
        Assert.Equal(all.Order(), BraveProfileCategoryAvailability.Detect(Profile("full", all)).Order());
        Assert.Equal(["bookmarks", "passwords"], BraveProfileCategoryAvailability.Detect(Profile("some", "bookmarks", "passwords")).Order());
    }

    [Fact]
    public async Task LegacySessionFilesCountAsSessions()
    {
        var directory = Profile("legacy"); File.WriteAllText(Path.Combine(directory, "Last Session"), "x");
        Assert.Contains("sessions", BraveProfileCategoryAvailability.Detect(directory));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StepsTwoAndBelowAppearOnlyWhileAProfileIsSelected()
    {
        using var shell = await Shell(("One", Profile("One", "bookmarks")));
        var seen = new List<string?>(); shell.PropertyChanged += (_, e) => seen.Add(e.PropertyName);
        Assert.False(shell.HasSelectedBackupProfiles); Assert.False(shell.ShowBackupActions);
        shell.Installations[0].Profiles[0].IsSelected = true;
        Assert.True(shell.HasSelectedBackupProfiles); Assert.True(shell.ShowBackupActions);
        Assert.Contains(nameof(shell.HasSelectedBackupProfiles), seen);
        shell.Installations[0].Profiles[0].IsSelected = false;
        Assert.False(shell.HasSelectedBackupProfiles); Assert.False(shell.ShowBackupActions);
    }

    [Fact]
    public async Task FeedbackKeepsTheResultPanelVisibleEvenAfterDeselecting()
    {
        using var shell = await Shell(("One", Profile("One", "bookmarks")));
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.BackupPath = Path.Combine(_root, "out.bravebackup"); shell.Options.EncryptBackup = false;
        await shell.CreateBackupAsync(); Assert.True(shell.BackupSucceeded);
        shell.Installations[0].Profiles[0].IsSelected = false;
        Assert.False(shell.HasSelectedBackupProfiles); Assert.True(shell.ShowBackupActions);
    }

    [Fact]
    public async Task OnlyCategoriesFoundInSelectedProfilesAreListedAndCheckable()
    {
        using var shell = await Shell(("Has passwords", Profile("A", "bookmarks", "passwords")), ("Bookmarks only", Profile("B", "bookmarks")), ("Empty", Profile("C")));
        var profiles = shell.Installations[0].Profiles;
        profiles[0].IsSelected = true;
        Assert.Equal(["bookmarks", "passwords"], shell.Options.ShownCategories.Select(c => c.Id).Order());
        Assert.All(shell.Options.ShownCategories, c => Assert.True(c.IsAvailable));
        var passwords = shell.Options.Categories.Single(c => c.Id == "passwords"); passwords.IsSelected = true; Assert.True(passwords.IsSelected);
        var sessions = shell.Options.Categories.Single(c => c.Id == "sessions"); sessions.IsSelected = true; Assert.False(sessions.IsSelected);   // not in the profile
        profiles[1].IsSelected = true;                                         // passwords now missing from one selected profile
        Assert.Equal(["bookmarks", "passwords"], shell.Options.ShownCategories.Select(c => c.Id).Order());
        Assert.False(passwords.IsAvailable); Assert.False(passwords.IsSelected); Assert.Equal("Not found in: Bookmarks only", passwords.AvailabilityNote);
        passwords.IsSelected = true; Assert.False(passwords.IsSelected);
        Assert.True(shell.Options.Categories.Single(c => c.Id == "bookmarks").IsAvailable);
        profiles[0].IsSelected = false;                                       // only the bookmarks-only profile remains
        Assert.Equal(["bookmarks"], shell.Options.ShownCategories.Select(c => c.Id));
        profiles[0].IsSelected = true; profiles[1].IsSelected = false;
        passwords.IsSelected = true; Assert.True(passwords.IsSelected);
    }

    [Fact]
    public async Task SelectAllAndAvailabilityMessagesUseOnlyEligibleCategories()
    {
        using var shell = await Shell(("A", Profile("A", "bookmarks", "settings")), ("Empty", Profile("E")));
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.AllCategoriesSelected = false; shell.Options.AllCategoriesSelected = true;
        Assert.Equal(["bookmarks", "settings"], shell.Options.Categories.Where(c => c.IsSelected).Select(c => c.Id).Order());
        Assert.Equal(true, shell.Options.AllCategoriesSelected);
        shell.Installations[0].Profiles[1].IsSelected = true;                  // an empty profile makes every category unavailable
        Assert.Empty(shell.Options.Categories.Where(c => c.IsSelected)); Assert.Equal(false, shell.Options.AllCategoriesSelected);
        Assert.Equal(["bookmarks", "settings"], shell.Options.ShownCategories.Select(c => c.Id).Order());
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.Installations[0].Profiles[0].IsSelected = false;                // only the empty profile
        Assert.False(shell.Options.HasShownCategories); Assert.Empty(shell.Options.ShownCategories);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
