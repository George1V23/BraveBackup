using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using BraveBackup.Core;
using BraveBackup.Platform;

namespace UI.ViewModels;

/// <summary>Represents a selectable category in the custom-backup form, not a claim of export support.</summary>
public partial class BackupCategoryItem : ViewModelBase
{
    /// <summary>Gets the stable category identifier.</summary>
    public string Id { get; }

    /// <summary>Gets the visible category name.</summary>
    public string Name { get; }

    /// <summary>Gets the category's intended contents.</summary>
    public string Description { get; }

    /// <summary>Gets or sets whether the category is selected across all profiles in scope.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Creates a selectable category with its initial selection.</summary>
    public BackupCategoryItem(string id, string name, string description, bool selected = false)
    {
        Id = id;
        Name = name;
        Description = description;
        _isSelected = selected;
    }
}

/// <summary>Holds approved backup options in memory. This form does not write archives or credential files.</summary>
public partial class BackupOptionsViewModel : ViewModelBase
{
    private readonly Func<DateTime> _clock;

    /// <summary>Gets the approved selectable categories; selection does not imply archive support.</summary>
    public ObservableCollection<BackupCategoryItem> Categories { get; } = new()
    {
        new("sessions", "Sessions", "Last persisted windows, tabs and supported groups; may lag live browser state"),
        new("bookmarks", "Bookmarks", "Bookmark folders and URLs", true),
        new("passwords", "Passwords", "Saved logins; protection enabled by default"),
        new("extensions", "Extensions",
            "All discovered version files and raw local/sync storage; Brave must be closed"),
        new("settings", "Settings",
            "Bookmark-bar visibility, Home button/homepage, startup mode and addresses (stored values only)"),
        new("tabmanagerplus", "TabManagerPlus", "Stored saved windows from supported 7.0.0; requires Brave closed")
    };

    /// <summary>Gets the approved compression choices.</summary>
    public IReadOnlyList<string> CompressionLevels { get; } = new[] { "None", "Fast", "Balanced", "High" };

    /// <summary>Gets or sets the future custom archive's destination, separate from the legacy JSON export.</summary>
    [ObservableProperty] private string _backupPath = string.Empty;

    /// <summary>Gets or sets whether the entire future archive will be encrypted.</summary>
    [ObservableProperty] private bool _encryptBackup = true;

    /// <summary>Gets or sets the encryption password in memory; never log or persist this property.</summary>
    [ObservableProperty] private string _password = string.Empty;

    /// <summary>Gets or sets the confirmation in memory; never log or persist this property.</summary>
    [ObservableProperty] private string _passwordConfirmation = string.Empty;

    /// <summary>Gets or sets whether password fields reveal their text.</summary>
    [ObservableProperty] private bool _revealPassword;

    /// <summary>Gets or sets the selected compression level.</summary>
    [ObservableProperty] private string _compression = "Balanced";

    /// <summary>Gets whether the password mode controls apply to the selected categories.</summary>
    public bool PasswordsSelected => Categories.Any(item => item.Id == "passwords" && item.IsSelected);

    /// <summary>Supported providers for this host. No source is selected or inspected automatically.</summary>
    public IReadOnlyList<PasswordSourceOption> PasswordSources { get; } = HostEnvironment.IsWindows
        ? new[] { new PasswordSourceOption("Windows DPAPI (v10)", "Uses the current Windows logon context; a fresh prompt is not guaranteed. App-bound protection is unsupported. Protected backup can offer raw preservation on capture failure.", PasswordEncryptionProvider.WindowsDpapiV10) }
        : HostEnvironment.IsLinux
            ? new[] { new PasswordSourceOption("Linux Secret Service (v11)", "Choose only if every selected browser uses Secret Service. The native keyring may prompt to unlock. Direct KWallet, basic and portal backends are unsupported; they are not tried automatically.", PasswordEncryptionProvider.LinuxKeyringV11) }
            : Array.Empty<PasswordSourceOption>();

    [ObservableProperty] private PasswordSourceOption? _selectedPasswordSource;
    public string PasswordSourceDescription => SelectedPasswordSource?.Description ?? "Choose the actual supported password source for all selected browsers. Selecting it does not access credentials.";
    partial void OnSelectedPasswordSourceChanged(PasswordSourceOption? value)
    {
        OnPropertyChanged(nameof(PasswordSourceDescription)); OnPropertyChanged(nameof(PasswordModeValidation));
    }

    /// <summary>Approved safe default. Changing whole-archive encryption never changes this explicit choice.</summary>
    [ObservableProperty] private bool _protectPasswords = true;

    /// <summary>Describes the explicit password representation and fresh-save consent requirement.</summary>
    public string PasswordModeDescription => ProtectPasswords
        ? "Protect passwords: portable credentials require an encrypted backup. Native unlock may be required when the operation starts."
        : "CSV chosen explicitly. Each selected profile gets a password CSV entry. A fresh destination-specific warning and confirmation is required before saving. Unsupported CSV data stops the backup; protected backup can offer raw-file preservation.";

    /// <summary>Blocks an incompatible protection/container choice without silently selecting CSV.</summary>
    public string PasswordModeValidation => !PasswordsSelected ? string.Empty : ProtectPasswords && !EncryptBackup ? "Protected passwords require archive encryption. Re-enable Encrypt backup; CSV is never selected automatically."
        : SelectedPasswordSource is null || !PasswordSources.Contains(SelectedPasswordSource) ? "Choose a supported Password source before starting password backup."
        : string.Empty;

    /// <summary>Accurately distinguishes CSV inside an encrypted container from an unencrypted local save.</summary>
    public string PasswordCsvWarning => EncryptBackup
        ? "CSV credentials become readable after archive decryption or extraction. A fresh destination-specific warning and confirmation is required before saving."
        : "CSV credentials would be readable by anyone with file access. A fresh destination-specific warning and confirmation is required before saving.";

    partial void OnProtectPasswordsChanged(bool value)
    {
        OnPropertyChanged(nameof(PasswordModeDescription)); OnPropertyChanged(nameof(PasswordModeValidation));
        OnPropertyChanged(nameof(RequiresPlaintextConfirmation)); OnPropertyChanged(nameof(PasswordCsvWarning));
    }

    /// <summary>Gets whether the optional native session files can be included.</summary>
    public bool SessionsSelected => Categories.Any(item => item.Id == "sessions" && item.IsSelected);

    /// <summary>Gets or sets the best-effort native session choice, valid only when Sessions is selected.</summary>
    [ObservableProperty] private bool _includeNativeSessionFiles;

    partial void OnIncludeNativeSessionFilesChanged(bool value)
    {
        if (value && !SessionsSelected) IncludeNativeSessionFiles = false;
    }

    /// <summary>Gets the full proposed destination without creating a file or directory.</summary>
    public string FullBackupPath
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(BackupPath)
                    ? "Choose a destination"
                    : System.IO.Path.GetFullPath(BackupPath);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.IOException)
            {
                return "Invalid destination path";
            }
        }
    }

    partial void OnBackupPathChanged(string value) => OnPropertyChanged(nameof(FullBackupPath));

    /// <summary>Gets an accurate description of the proposed archive protection and current limits.</summary>
    public string ProtectionDescription => EncryptBackup
        ? "The entire archive, including profile names, will be encrypted. Keep the password safe; it cannot be recovered."
        : "Encryption is off. Anyone with file access can read this archive.";

    /// <summary>Gets the combined state, or sets all categories; assigning mixed state does nothing.</summary>
    public bool? AllCategoriesSelected
    {
        get => Categories.All(item => item.IsSelected) ? true : Categories.All(item => !item.IsSelected) ? false : null;
        set
        {
            if (!value.HasValue) return;
            foreach (var item in Categories) item.IsSelected = value.Value;
        }
    }

    /// <summary>Gets whether plaintext credentials would need a warning and confirmation before any save.</summary>
    public bool RequiresPlaintextConfirmation =>
        PasswordsSelected && !ProtectPasswords;

    /// <summary>Gets an approximate length/character-variety score, not an encryption guarantee.</summary>
    public int PasswordStrength => Password.Length == 0
        ? 0
        : Math.Min(100, Password.Length * 4 +
                        (Password.Any(char.IsUpper) ? 5 : 0) + (Password.Any(char.IsLower) ? 5 : 0) +
                        (Password.Any(char.IsDigit) ? 5 : 0) +
                        (Password.Any(character => !char.IsLetterOrDigit(character)) ? 5 : 0));

    /// <summary>Gets a clearly labelled heuristic strength estimate.</summary>
    public string PasswordStrengthText => Password.Length == 0 ? "Not entered" :
        Password.Length >= 16 && PasswordStrength >= 80 ? "Strong (estimate)" :
        Password.Length >= 12 && PasswordStrength >= 60 ? "Moderate (estimate)" : "Weak (estimate)";

    /// <summary>Gets actionable password validation for the option form.</summary>
    public string PasswordValidation => !EncryptBackup ? string.Empty :
        Password.Length == 0 ? "Enter an encryption password." :
        Password != PasswordConfirmation ? "Passwords do not match." : string.Empty;

    /// <summary>Creates defaults with an injectable local clock for deterministic filename tests.</summary>
    public BackupOptionsViewModel(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
        foreach (var category in Categories) category.PropertyChanged += OnCategoryChanged;
        Reset();
    }

    /// <summary>Restores filename, categories, encryption and compression defaults, clearing all secret text.</summary>
    public void Reset()
    {
        BackupPath = $"BraveBackup_{_clock():yyyy-MM-dd_HHmmss}.bravebackup";
        EncryptBackup = true;
        ProtectPasswords = true;
        SelectedPasswordSource = null;
        Password = string.Empty;
        PasswordConfirmation = string.Empty;
        RevealPassword = false;
        Compression = "Balanced";
        IncludeNativeSessionFiles = false;
        foreach (var item in Categories) item.IsSelected = item.Id == "bookmarks";
    }

    /// <summary>Clears password text and visibility when closing the shell.</summary>
    public void ClearSecrets()
    {
        Password = string.Empty;
        PasswordConfirmation = string.Empty;
        RevealPassword = false;
    }

    private void OnCategoryChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(BackupCategoryItem.IsSelected)) return;
        if (!SessionsSelected) IncludeNativeSessionFiles = false;
        OnPropertyChanged(nameof(PasswordsSelected));
        OnPropertyChanged(nameof(PasswordModeValidation));
        OnPropertyChanged(nameof(SessionsSelected));
        OnPropertyChanged(nameof(AllCategoriesSelected));
        OnPropertyChanged(nameof(RequiresPlaintextConfirmation));
    }

    partial void OnEncryptBackupChanged(bool value)
    {
        if (!value) ClearSecrets();
        OnPropertyChanged(nameof(PasswordModeValidation));
        OnPropertyChanged(nameof(PasswordCsvWarning));
        OnPropertyChanged(nameof(ProtectionDescription));
        OnPropertyChanged(nameof(RequiresPlaintextConfirmation));
        OnPropertyChanged(nameof(PasswordValidation));
    }

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(PasswordStrength));
        OnPropertyChanged(nameof(PasswordStrengthText));
        OnPropertyChanged(nameof(PasswordValidation));
    }

    partial void OnPasswordConfirmationChanged(string value) => OnPropertyChanged(nameof(PasswordValidation));
}