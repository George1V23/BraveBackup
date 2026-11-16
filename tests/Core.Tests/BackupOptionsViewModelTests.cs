using UI.ViewModels;
using Xunit;

namespace BraveBackup.Core.Tests;

/// <summary>Verifies the option form's selection and password behavior without exporting any credentials.</summary>
public sealed class BackupOptionsViewModelTests
{
    [Fact]
    public void NativeSessions_RequireSessionsAndResetClearsTheChoice()
    {
        var options = new BackupOptionsViewModel();
        options.IncludeNativeSessionFiles = true;
        Assert.False(options.IncludeNativeSessionFiles);
        var sessions = options.Categories.Single(item => item.Id == "sessions");
        sessions.IsSelected = true;
        options.IncludeNativeSessionFiles = true;
        Assert.True(options.IncludeNativeSessionFiles);
        sessions.IsSelected = false;
        Assert.False(options.IncludeNativeSessionFiles);
        sessions.IsSelected = true;
        options.IncludeNativeSessionFiles = true;
        options.Reset();
        Assert.False(options.SessionsSelected);
        Assert.False(options.IncludeNativeSessionFiles);
    }

    [Fact]
    public void DestinationAndProtection_DescribeCurrentChoicesWithoutWritingFiles()
    {
        var options = new BackupOptionsViewModel();
        options.BackupPath = "fixture.bravebackup";
        Assert.Equal(Path.GetFullPath(options.BackupPath), options.FullBackupPath);
        Assert.Contains("will be encrypted", options.ProtectionDescription);
        options.EncryptBackup = false;
        Assert.Contains("Encryption is off", options.ProtectionDescription);
        options.BackupPath = "\0";
        Assert.Equal("Invalid destination path", options.FullBackupPath);
    }

    [Fact]
    public void CategorySelection_ExposesMixedStateAndNotifiesForIndividualChanges()
    {
        var options = new BackupOptionsViewModel();
        Assert.Null(options.AllCategoriesSelected);
        var notifications = new List<string?>();
        options.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        options.AllCategoriesSelected = true;
        Assert.All(options.Categories, item => Assert.True(item.IsSelected));
        options.Categories[0].IsSelected = false;
        Assert.Null(options.AllCategoriesSelected);
        Assert.Contains(nameof(options.AllCategoriesSelected), notifications);
        options.AllCategoriesSelected = false;
        Assert.Equal(false, options.AllCategoriesSelected);
        Assert.Contains(options.Categories, category => category.Id == "sessions");
    }

    [Fact]
    public void PlaintextWarning_RequiresPasswordsAndDisabledEncryptionAndClearsSecrets()
    {
        var options = new BackupOptionsViewModel();
        Assert.True(options.EncryptBackup);
        options.Password = "fixture-secret";
        options.PasswordConfirmation = "fixture-secret";
        options.RevealPassword = true;
        var passwords = options.Categories.Single(item => item.Id == "passwords");
        passwords.IsSelected = true;
        Assert.False(options.RequiresPlaintextConfirmation);
        options.EncryptBackup = false;
        Assert.True(options.RequiresPlaintextConfirmation);
        Assert.Empty(options.Password);
        Assert.Empty(options.PasswordConfirmation);
        Assert.False(options.RevealPassword);
        Assert.Empty(options.PasswordValidation);
        passwords.IsSelected = false;
        Assert.False(options.RequiresPlaintextConfirmation);
        options.EncryptBackup = true;
        Assert.False(options.RequiresPlaintextConfirmation);
    }

    [Fact]
    public void PasswordValidationAndStrength_UpdateWithoutExposingSecretInMessages()
    {
        var options = new BackupOptionsViewModel();
        Assert.Equal("Not entered", options.PasswordStrengthText);
        Assert.Contains("Enter", options.PasswordValidation);
        options.Password = "a";
        Assert.Equal("Weak (estimate)", options.PasswordStrengthText);
        Assert.Contains("do not match", options.PasswordValidation);
        const string sample = "Example-fixture-4827";
        options.Password = sample;
        options.PasswordConfirmation = sample;
        Assert.Equal("Strong (estimate)", options.PasswordStrengthText);
        Assert.Empty(options.PasswordValidation);
        Assert.DoesNotContain(sample, options.ToString()!);
        options.ClearSecrets();
        Assert.Equal(0, options.PasswordStrength);
        Assert.Empty(options.PasswordConfirmation);
    }
}