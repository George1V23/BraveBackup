using BraveBackup.Core;
using UI.ViewModels;
using BraveBackup.Platform;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ShellBackupTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ShellBackupTests_" + Guid.NewGuid().ToString("N"));

    private readonly BraveAppInfo _app;
    private readonly BraveProfileInfo _profile;

    public ShellBackupTests()
    {
        var data = Path.Combine(_root, "browser-data");
        var directory = Path.Combine(data, "Default");
        Directory.CreateDirectory(directory);
        _app = new BraveAppInfo(BraveAppChannel.Stable, "fixture", Path.Combine(_root, "brave"), data);
        _profile = new BraveProfileInfo("Private fixture profile", directory, Path.Combine(directory, "Bookmarks"));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"), _profile.BookmarksPath);
    }

    private async Task<ShellViewModel> Create()
    {
        var shell = new ShellViewModel(() => new[] { _app }, _ => new[] { _profile },
            _ => new BraveProfileSummary(3, 0));
        await shell.RefreshAsync();
        shell.Activate(shell.Installations[0]);
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
        Assert.Contains("Password source", shell.BackupAvailability);
        await shell.CreateBackupAsync();
        Assert.False(File.Exists(shell.Options.BackupPath));
    }

    [Fact]
    public async Task CreateEncryptedBackup_ShowsRealOutcomeAndClearsPasswordFields()
    {
        using var shell = await Create();
        shell.Installations[0].Profiles[0].IsSelected = true;
        const string password = "Fixture-secret-4812";
        shell.Options.Password = password;
        shell.Options.PasswordConfirmation = password;
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.False(shell.IsBackupRunning);
        Assert.True(shell.HasBackupFeedback);
        Assert.True(shell.HasBackupReport);
        Assert.Contains(Path.GetFullPath(shell.Options.BackupPath), shell.BackupMessage);
        Assert.DoesNotContain(password, shell.BackupReport);
        Assert.DoesNotContain(_profile.ProfileName, shell.BackupReport);
        Assert.DoesNotContain(_root, shell.BackupReport);
        Assert.Empty(shell.Options.Password);
        Assert.Empty(shell.Options.PasswordConfirmation);
        shell.ToggleBackupReport();
        Assert.True(shell.ShowBackupReport);
        using var archive = File.OpenRead(shell.Options.BackupPath);
        BackupArchive.Validate(archive, password);
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
        Assert.True(shell.IsBackupRunning);
        Assert.False(shell.IsWorkspaceEnabled);
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.ResetBackupForm();
        Assert.True(shell.Installations[0].Profiles[0].IsSelected);
        shell.CancelBackup();
        choice.SetResult(true);
        await pending;
        Assert.Equal("original", File.ReadAllText(target));
        Assert.False(shell.BackupSucceeded);
        Assert.Contains("cancelled", shell.BackupMessage);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsSelection_CreatesRequestedCategoriesAndShowsStoredCounts(bool bookmarks)
    {
        using var shell = await Create();
        File.Delete(_profile.BookmarksPath);
        if (bookmarks)
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Bookmarks.sample.json"),
                _profile.BookmarksPath);
        File.WriteAllText(Path.Combine(_profile.ProfileDirectory, "Preferences"), "{}");
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.EncryptBackup = false;
        shell.Options.Categories.Single(item => item.Id == "bookmarks").IsSelected = bookmarks;
        shell.Options.Categories.Single(item => item.Id == "settings").IsSelected = true;
        Assert.True(shell.CreateBackupCommand.CanExecute(null));
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.Contains("Stored supported settings: 0; absent keys: 7", shell.BackupMessage);
        Assert.Contains("settings", shell.BackupReport);
        Assert.DoesNotContain("Private fixture profile", shell.BackupReport);
        using var file = File.OpenRead(shell.Options.BackupPath);
        Assert.Equal(2, BraveSettingsBackup.ValidateArchive(file).FormatVersion);
    }

    [Fact]
    public async Task ExtensionUi_RequiresConsentForPlaintextAndShowsActualOutcome()
    {
        using var shell = await Create();
        shell.ExtensionBrowserState = () => BraveBackup.Platform.ProcessPresence.Stopped;
        shell.Options.EncryptBackup = false;
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.Categories.Single(item => item.Id == "bookmarks").IsSelected = false;
        shell.Options.Categories.Single(item => item.Id == "extensions").IsSelected = true;
        Assert.True(shell.CreateBackupCommand.CanExecute(null));
        await shell.CreateBackupAsync();
        Assert.False(shell.BackupSucceeded);
        Assert.False(File.Exists(shell.Options.BackupPath));
        var confirmations = 0;
        shell.ConfirmUnencryptedExtensionData = path =>
        {
            confirmations++;
            Assert.Equal(shell.Options.FullBackupPath, path);
            return Task.FromResult(true);
        };
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.Equal(1, confirmations);
        Assert.Contains("Extension packages: 0; versions: 0", shell.BackupMessage);
        Assert.Contains("extensions (completed)", shell.BackupReport);
        using var source = File.OpenRead(shell.Options.BackupPath);
        Assert.Equal(3, BraveSettingsBackup.ValidateArchive(source).FormatVersion);
    }

    [Theory]
    [InlineData(BraveBackup.Platform.ProcessPresence.Running)]
    [InlineData(BraveBackup.Platform.ProcessPresence.Unknown)]
    public async Task ExtensionUi_BlocksLiveOrIndeterminateBrowserWithSafeFeedback(
        BraveBackup.Platform.ProcessPresence state)
    {
        using var shell = await Create();
        shell.ExtensionBrowserState = () => state;
        shell.Options.EncryptBackup = false;
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.Categories.Single(item => item.Id == "bookmarks").IsSelected = false;
        shell.Options.Categories.Single(item => item.Id == "extensions").IsSelected = true;
        shell.ConfirmUnencryptedExtensionData =
            _ => throw new InvalidOperationException("Must not prompt before process check.");
        await shell.CreateBackupAsync();
        Assert.False(shell.BackupSucceeded);
        Assert.Contains("Brave", shell.BackupMessage);
        Assert.Contains("extensions-browser-", shell.BackupReport);
        Assert.DoesNotContain(_root, shell.BackupReport);
        Assert.False(File.Exists(shell.Options.BackupPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionsUi_FreezesNativeChoiceAndDisplaysPersistedCounts(bool native)
    {
        using var shell = await Create();
        var directory = Path.Combine(_profile.ProfileDirectory, "Sessions");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "Session_10"), BraveSessionDecoderTests.Fixture());
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.EncryptBackup = false;
        shell.Options.Categories.Single(item => item.Id == "bookmarks").IsSelected = false;
        shell.Options.Categories.Single(item => item.Id == "sessions").IsSelected = true;
        shell.Options.IncludeNativeSessionFiles = native;
        if (native)
        {
            await shell.CreateBackupAsync();
            Assert.False(shell.BackupSucceeded);
            Assert.False(File.Exists(shell.Options.BackupPath));
            shell.ConfirmUnencryptedNativeSessions = _ => Task.FromResult(true);
        }

        Assert.True(shell.CreateBackupCommand.CanExecute(null));
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.Contains("windows: 2; tabs: 3; groups: 1", shell.BackupMessage);
        Assert.Contains("persisted", shell.BackupMessage);
        Assert.DoesNotContain("selected.example", shell.BackupReport);
        using var source = File.OpenRead(shell.Options.BackupPath);
        Assert.Equal(4, BraveSettingsBackup.ValidateArchive(source).FormatVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TabManagerPlusUi_UsesConsentAndReportsActualStoredCounts(bool encrypted)
    {
        TabManagerPlusBackupTests.PrepareProfile(_profile.ProfileDirectory);
        using var shell = await Create();
        shell.ExtensionBrowserState = () => BraveBackup.Platform.ProcessPresence.Stopped;
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.Categories.Single(item => item.Id == "bookmarks").IsSelected = false;
        shell.Options.Categories.Single(item => item.Id == "tabmanagerplus").IsSelected = true;
        shell.Options.EncryptBackup = encrypted;
        if (encrypted)
        {
            shell.Options.Password = "fixture";
            shell.Options.PasswordConfirmation = "fixture";
        }

        Assert.True(shell.CreateBackupCommand.CanExecute(null));
        Assert.DoesNotContain("Deselect other", shell.BackupAvailability);
        if (!encrypted)
        {
            await shell.CreateBackupAsync();
            Assert.False(shell.BackupSucceeded);
            Assert.False(File.Exists(shell.Options.BackupPath));
        }

        var prompts = 0;
        shell.ConfirmUnencryptedTabManagerPlus = path =>
        {
            prompts++;
            Assert.Equal(shell.Options.FullBackupPath, path);
            Assert.True(shell.IsBackupRunning);
            Assert.False(shell.IsWorkspaceEnabled);
            return Task.FromResult(true);
        };
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.Equal(encrypted ? 0 : 1, prompts);
        Assert.Contains("profiles: 1; windows: 1; tabs: 1", shell.BackupMessage);
        Assert.DoesNotContain("Synthetic work", shell.BackupReport);
        Assert.DoesNotContain("example.invalid", shell.BackupReport);
        Assert.Empty(shell.Options.Password);
        using var source = File.OpenRead(shell.Options.BackupPath);
        Assert.Equal(5, BraveSettingsBackup.ValidateArchive(source, encrypted ? "fixture" : null).FormatVersion);
    }

    [Fact]
    public async Task TabManagerPlusUi_FreezesRequestAndDoesNotReuseConsent()
    {
        TabManagerPlusBackupTests.PrepareProfile(_profile.ProfileDirectory);
        using var shell = await Create();
        shell.ExtensionBrowserState = () => BraveBackup.Platform.ProcessPresence.Stopped;
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.EncryptBackup = false;
        var category = shell.Options.Categories.Single(item => item.Id == "tabmanagerplus");
        shell.Options.Categories.Single(item => item.Id == "bookmarks").IsSelected = false;
        category.IsSelected = true;
        shell.ConfirmUnencryptedTabManagerPlus = _ =>
        {
            category.IsSelected = false;
            return Task.FromResult(true);
        };
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded);
        Assert.Contains("tabmanagerplus (completed)", shell.BackupReport);
        var original = File.ReadAllBytes(shell.Options.BackupPath);
        category.IsSelected = true;
        shell.ConfirmUnencryptedTabManagerPlus = _ => Task.FromResult(false);
        await shell.CreateBackupAsync();
        Assert.False(shell.BackupSucceeded);
        Assert.Equal(original, File.ReadAllBytes(shell.Options.BackupPath));
    }

    public void Dispose() => Directory.Delete(_root, true);
    [Fact]
    public async Task PasswordProtectionValidationExplainsTheActualChoiceWithoutCreatingFiles()
    {
        using var shell = await Create();
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.Categories.Single(item => item.Id == "passwords").IsSelected = true;
        shell.Options.EncryptBackup = false;
        Assert.True(shell.Options.ProtectPasswords);
        Assert.Contains("require archive encryption", shell.BackupAvailability);
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.Options.ProtectPasswords = false;
        Assert.Contains("Password source", shell.BackupAvailability);
        Assert.False(shell.CreateBackupCommand.CanExecute(null));
        Assert.False(File.Exists(shell.Options.BackupPath));
    }

    [Fact]
    public async Task ExplicitPasswordSourceEnablesProtectedArchiveAndUsesHostAdapterOnlyOnStart()
    {
        using var shell = await Create();
        shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.Categories.Single(item => item.Id == "passwords").IsSelected = true;
        shell.Options.Password = "Synthetic-archive-4827"; shell.Options.PasswordConfirmation = shell.Options.Password;
        Assert.Null(shell.Options.SelectedPasswordSource); Assert.False(shell.CreateBackupCommand.CanExecute(null));
        var source = Assert.Single(shell.Options.PasswordSources); shell.Options.SelectedPasswordSource = source;
        var fixtureOs = source.Provider == PasswordEncryptionProvider.WindowsDpapiV10 ? "windows" : "linux";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords", "LoginData.v43." + fixtureOs + ".sqlite"), Path.Combine(_profile.ProfileDirectory, "Login Data"));
        if (fixtureOs == "windows") File.WriteAllText(Path.Combine(_app.UserDataDirectory, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
        var calls = 0;
        shell.PasswordBrowserState = () => ProcessPresence.Stopped;
        shell.PasswordKeyUnlocker = (_, _, _) =>
        {
            calls++; using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Passwords", "Crypto.synthetic.json")));
            return new PasswordKeyMaterial(source.Provider, Convert.FromHexString(json.RootElement.GetProperty(fixtureOs + "KeyHex").GetString()!));
        };
        Assert.Equal(0, calls); Assert.True(shell.CreateBackupCommand.CanExecute(null));
        await shell.CreateBackupAsync(); Assert.Equal(1, calls); Assert.True(shell.BackupSucceeded);
        Assert.Contains("Protected password profiles: 1", shell.BackupReport); Assert.DoesNotContain("fixture-user", shell.BackupReport);
        using var file = File.OpenRead(shell.Options.BackupPath);
        Assert.Equal(6, BackupArchive.Validate(file, "Synthetic-archive-4827").FormatVersion);
    }

    [Fact]
    public async Task PasswordRawFallbackIsExplicitThroughHostCallback()
    {
        using var shell = await Create(); shell.Installations[0].Profiles[0].IsSelected = true;
        shell.Options.Categories.Single(item => item.Id == "passwords").IsSelected = true;
        shell.Options.SelectedPasswordSource = Assert.Single(shell.Options.PasswordSources);
        shell.Options.Password = "Synthetic-archive-4827"; shell.Options.PasswordConfirmation = shell.Options.Password;
        shell.PasswordBrowserState = () => ProcessPresence.Stopped;
        File.WriteAllText(Path.Combine(_profile.ProfileDirectory, "Login Data"), "synthetic corrupt database");
        var choices = 0;
        shell.ConfirmRawPasswordPreservation = (warning, _) =>
        { choices++; Assert.Contains("corrupt", warning.ReasonDescription); return Task.FromResult(true); };
        await shell.CreateBackupAsync(); Assert.Equal(1, choices); Assert.True(shell.BackupSucceeded);
        Assert.Contains("Raw credential counts are unknown", shell.BackupReport);
        using var file = File.OpenRead(shell.Options.BackupPath);
        Assert.Equal(7, BackupArchive.Validate(file, "Synthetic-archive-4827").FormatVersion);
    }

    [Fact]
    public async Task PasswordSourceSurvivesSameScopeButIsClearedByRediscovery()
    {
        using var shell = await Create();
        var source = Assert.Single(shell.Options.PasswordSources); shell.Options.SelectedPasswordSource = source;
        shell.Activate(shell.Installations[0]); Assert.Same(source, shell.Options.SelectedPasswordSource);
        await shell.RefreshAsync(); Assert.Null(shell.Options.SelectedPasswordSource);
    }

}