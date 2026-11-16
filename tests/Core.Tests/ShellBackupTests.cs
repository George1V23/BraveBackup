using BraveBackup.Core;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ShellBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShellBackupTests_" + Guid.NewGuid().ToString("N"));
    private readonly BraveAppInfo _app;
    private readonly BraveProfileInfo _profile;

    public ShellBackupTests()
    {
        var data = Path.Combine(_root, "browser-data");
        var directory = Path.Combine(data, "Default"); Directory.CreateDirectory(directory);
        _app = new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), data);
        _profile = new BraveProfileInfo("Private fixture profile", directory, Path.Combine(directory, "Bookmarks"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), _profile.BookmarksPath);
    }

    private async Task<ShellViewModel> Create()
    {
        var shell = new ShellViewModel(() => new[] { _app }, _ => new[] { _profile }, _ => new BraveProfileSummary(3, 0));
        await shell.RefreshAsync(); shell.Activate(shell.Installations[0]);
        shell.Options.BackupPath = Path.Combine(_root, "result.bravebackup");
        return shell;
    }

    [Fact]
    public async Task CreateValidation_RequiresProfileSupportedCategoryAndMatchingPassword()
    {
        using var shell = await Create();
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.Installations[0].Profiles[0].IsSelected = true;
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.Options.Password = "Fixture-secret-4812";
        shell.Options.PasswordConfirmation = shell.Options.Password;
        Assert.True(shell.CreateBackupCommand.CanExecute(null));
        var validDestination = shell.Options.BackupPath;
        shell.Options.BackupPath = "bad.json";
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        Assert.Contains("ending in .bravebackup", shell.BackupAvailability);
        shell.Options.BackupPath = "\0.bravebackup";
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.Options.BackupPath = validDestination;
        shell.Options.Categories.Single(item => item.Id == "passwords").IsSelected = true;
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        Assert.Contains("Deselect other categories", shell.BackupAvailability);
        await shell.CreateBackupAsync();
        Assert.False(File.Exists(shell.Options.BackupPath));
    }

    [Fact]
    public async Task CreateEncryptedBackup_ShowsRealOutcomeAndClearsPasswordFields()
    {
        using var shell = await Create();
        shell.Installations[0].Profiles[0].IsSelected = true;
        const string password = "Fixture-secret-4812";
        shell.Options.Password = password; shell.Options.PasswordConfirmation = password;
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.False(shell.IsBackupRunning);
        Assert.True(shell.HasBackupFeedback); Assert.True(shell.HasBackupReport);
        Assert.Contains(Path.GetFullPath(shell.Options.BackupPath), shell.BackupMessage);
        Assert.DoesNotContain(password, shell.BackupReport);
        Assert.DoesNotContain(_profile.ProfileName, shell.BackupReport);
        Assert.DoesNotContain(_root, shell.BackupReport);
        Assert.Empty(shell.Options.Password); Assert.Empty(shell.Options.PasswordConfirmation);
        shell.ToggleBackupReport(); Assert.True(shell.ShowBackupReport);
        using var archive = File.OpenRead(shell.Options.BackupPath); BackupArchive.Validate(archive, password);
    }

    [Fact]
    public async Task PendingOverwrite_FreezesRequestAndCancelPreservesExistingFile()
    {
        using var shell = await Create();
        var target = shell.Options.BackupPath;
        await File.WriteAllTextAsync(target, "original");
        shell.Options.EncryptBackup = false;
        shell.Installations[0].Profiles[0].IsSelected = true;
        var choice = new TaskCompletionSource<bool>();
        shell.ConfirmBackupOverwrite = _ => choice.Task;
        var pending = shell.CreateBackupAsync();
        // Fingerprinting is asynchronous; yield until confirmation is pending.
        for (var i = 0; i < 100 && !shell.IsBackupRunning; i++) await Task.Delay(5);
        Assert.True(shell.IsBackupRunning); Assert.False(shell.IsWorkspaceEnabled);
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.ResetBackupForm();
        Assert.True(shell.Installations[0].Profiles[0].IsSelected);
        shell.CancelBackup(); choice.SetResult(true);
        await pending;
        Assert.Equal("original", File.ReadAllText(target));
        Assert.False(shell.BackupSucceeded); Assert.Contains("cancelled", shell.BackupMessage);
        Assert.Contains("Published: no", shell.BackupReport);
    }

    [Fact]
    public async Task DefaultOverwriteDecline_AndSourceFailuresHaveNoFalseSuccess()
    {
        using var shell = await Create();
        shell.Options.EncryptBackup = false;
        shell.Installations[0].Profiles[0].IsSelected = true;
        await File.WriteAllTextAsync(shell.Options.BackupPath, "original");
        await shell.CreateBackupAsync();
        Assert.False(shell.BackupSucceeded);
        Assert.Equal("original", File.ReadAllText(shell.Options.BackupPath));
        shell.ConfirmBackupOverwrite = _ => Task.FromResult(true);
        File.WriteAllText(_profile.BookmarksPath, "{broken}");
        await shell.CreateBackupAsync();
        Assert.False(shell.BackupSucceeded);
        Assert.Contains("Profile 1 of 1", shell.BackupMessage);
        Assert.Contains("source-unreadable", shell.BackupReport);
        Assert.Equal("original", File.ReadAllText(shell.Options.BackupPath));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
