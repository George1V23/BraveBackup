using BraveBackup.Platform;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

public sealed class ShellCsvArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ShellCsvArchive_" + Guid.NewGuid().ToString("N"));
    private int _unlocks;
    private string Destination => Path.Combine(_root, "output", "backup.bravebackup");
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    private async Task<ShellViewModel> Create(string version = "1.102.13")
    {
        var data = Path.Combine(_root, "data"); var directory = Path.Combine(data, "Default");
        Directory.CreateDirectory(directory);
        var shell = new ShellViewModel(() => [new(BraveAppChannel.Stable, version, "synthetic", data)],
            _ => [new("Synthetic", directory, Path.Combine(directory, "Bookmarks"))], _ => new(0, 0));
        await shell.RefreshAsync(); shell.Activate(shell.Installations[0]);
        var source = Assert.Single(shell.Options.PasswordSources);
        var os = source.Provider == PasswordEncryptionProvider.WindowsDpapiV10 ? "windows" : "linux";
        var database = Path.Combine(directory, "Login Data");
        File.Copy(Path.Combine(Fixtures, "Passwords", $"LoginData.v43.{os}.sqlite"), database);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "UPDATE password_notes SET key=''"; command.ExecuteNonQuery(); }
        File.Copy(Path.Combine(Fixtures, "Affiliations", "Affiliation.v7.synthetic.sqlite"), Path.Combine(directory, BraveAffiliationSnapshot.DatabaseFileName));
        if (os == "windows") File.WriteAllText(Path.Combine(data, "Local State"), "{\"os_crypt\":{\"encrypted_key\":\"" + Convert.ToBase64String("DPAPIsynthetic"u8) + "\"}}");
        // The profile is selected only after its data exists, so the passwords category is listed and checkable.
        shell.Installations[0].Profiles[0].IsSelected = true;
        foreach (var category in shell.Options.Categories) category.IsSelected = category.Id == "passwords";
        shell.Options.SelectedPasswordSource = source;
        shell.Options.BackupPath = Destination; shell.Options.ProtectPasswords = false; shell.Options.EncryptBackup = false;
        shell.PasswordBrowserState = () => ProcessPresence.Stopped;
        shell.PasswordKeyUnlocker = (_, _, _) =>
        {
            _unlocks++; using var vector = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "Passwords", "Crypto.synthetic.json")));
            return new PasswordKeyMaterial(source.Provider, Convert.FromHexString(vector.RootElement.GetProperty(os + "KeyHex").GetString()!));
        };
        return shell;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitCsvBackupConfirmsEachSaveAndShowsSafeOutcome(bool encrypted)
    {
        using var shell = await Create(); var calls = 0;
        shell.Options.EncryptBackup = encrypted;
        shell.ConfirmBackupOverwrite = _ => Task.FromResult(true);
        shell.ConfirmPasswordCsvArchiveSave = (warning, _) =>
        {
            calls++; Assert.Equal(Destination, warning.Destination); Assert.Equal(encrypted, warning.ArchiveEncrypted);
            Assert.Equal(calls == 2, warning.ReplacesExistingFile); Assert.Equal(1, warning.ProfileCount);
            Assert.True(shell.IsBackupRunning); Assert.False(shell.IsWorkspaceEnabled);
            Assert.False(shell.CanUsePasswordCsv); Assert.False(shell.CreateBackupCommand.CanExecute(null));
            Assert.Equal(calls - 1, _unlocks); return Task.FromResult(true);
        };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            shell.Options.Password = "Synthetic-archive-4827"; shell.Options.PasswordConfirmation = shell.Options.Password;
            Assert.True(shell.CreateBackupCommand.CanExecute(null)); await shell.CreateBackupAsync();
            Assert.True(shell.BackupSucceeded); Assert.Contains("CSV rows: 1", shell.BackupMessage);
            Assert.DoesNotContain("fixture-user", shell.BackupReport); Assert.DoesNotContain("example.invalid", shell.BackupReport);
            Assert.Empty(shell.Options.Password); Assert.Empty(shell.Options.PasswordConfirmation);
            using var input = File.OpenRead(Destination);
            Assert.Equal(8, BraveSettingsBackup.ValidateArchive(input, encrypted ? "Synthetic-archive-4827" : null).FormatVersion);
        }
        Assert.Equal(2, calls); Assert.Equal(2, _unlocks);
    }

    [Fact]
    public async Task MissingHostConsentOrUnsupportedVersionBlocksBeforeUnlock()
    {
        using var shell = await Create("1.103.1");
        Assert.Contains("1.102.13", shell.BackupAvailability); Assert.False(shell.CreateBackupCommand.CanExecute(null));
        shell.ConfirmPasswordCsvArchiveSave = (_, _) => throw new InvalidOperationException("Must not confirm");
        await shell.CreateBackupAsync(); Assert.Equal(0, _unlocks); Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task HostCallbackAvailabilityUpdatesCommandAndDeclineWritesNothing()
    {
        using var shell = await Create();
        Assert.Contains("confirmation dialog", shell.BackupAvailability); Assert.False(shell.CreateBackupCommand.CanExecute(null));
        var changes = 0; shell.CreateBackupCommand.CanExecuteChanged += (_, _) => changes++;
        shell.ConfirmPasswordCsvArchiveSave = (_, _) => Task.FromResult(false);
        Assert.True(changes > 0); Assert.True(shell.CreateBackupCommand.CanExecute(null));
        await shell.CreateBackupAsync(); Assert.False(shell.BackupSucceeded); Assert.Contains("cancelled", shell.BackupMessage);
        Assert.Equal(0, _unlocks); Assert.False(Directory.Exists(Path.GetDirectoryName(Destination)));
        shell.ConfirmPasswordCsvArchiveSave = null; Assert.False(shell.CreateBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task PendingConsentCanBeCancelledAndFrozenRequestIgnoresFormEdits()
    {
        using var shell = await Create(); var entered = new TaskCompletionSource();
        shell.ConfirmPasswordCsvArchiveSave = async (warning, token) =>
        {
            Assert.Equal(Destination, warning.Destination); entered.SetResult();
            await Task.Delay(Timeout.Infinite, token); return true;
        };
        var operation = shell.CreateBackupAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        shell.Options.BackupPath = Path.Combine(_root, "changed.bravebackup"); shell.Options.ProtectPasswords = true;
        shell.CancelBackup(); await operation;
        Assert.False(shell.BackupSucceeded); Assert.Contains("cancelled", shell.BackupMessage);
        Assert.Equal(0, _unlocks); Assert.False(File.Exists(Destination)); Assert.True(shell.IsWorkspaceEnabled);
    }

    [Fact]
    public async Task ApprovedSaveUsesFrozenChoicesEvenIfFormChangesDuringConsent()
    {
        using var shell = await Create();
        shell.ConfirmPasswordCsvArchiveSave = (_, _) =>
        {
            shell.Options.BackupPath = Path.Combine(_root, "changed.bravebackup");
            shell.Options.ProtectPasswords = true; shell.Options.EncryptBackup = true;
            shell.Options.SelectedPasswordSource = null;
            foreach (var category in shell.Options.Categories) category.IsSelected = false;
            shell.ConfirmPasswordCsvArchiveSave = null;
            return Task.FromResult(true);
        };
        await shell.CreateBackupAsync();
        Assert.True(shell.BackupSucceeded); Assert.Equal(1, _unlocks);
        Assert.False(File.Exists(shell.Options.BackupPath));
        using var input = File.OpenRead(Destination);
        Assert.False(ArchiveEncryption.IsEncrypted(input));
        Assert.Equal(8, BraveSettingsBackup.ValidateArchive(input).FormatVersion);
    }

    [Fact]
    public async Task DisposingShellCancelsPendingConsentAndReleasesHostCallback()
    {
        var shell = await Create(); var entered = new TaskCompletionSource();
        shell.ConfirmPasswordCsvArchiveSave = async (_, token) =>
        { entered.SetResult(); await Task.Delay(Timeout.Infinite, token); return true; };
        var operation = shell.CreateBackupAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        shell.Dispose(); await operation;
        Assert.Null(shell.ConfirmPasswordCsvArchiveSave); Assert.Equal(0, _unlocks); Assert.False(File.Exists(Destination));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
