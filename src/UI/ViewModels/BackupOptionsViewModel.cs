using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

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
        new("sessions", "Sessions", "Open windows, tabs and tab groups"),
        new("bookmarks", "Bookmarks", "Bookmark folders and URLs", true),
        new("passwords", "Passwords", "Saved logins; protection enabled by default"),
        new("extensions", "Extensions", "Installed extensions and supported settings/data"),
        new("settings", "Settings", "Supported Brave profile preferences"),
        new("tabmanagerplus", "TabManagerPlus", "Supported saved windows, groups and extension data")
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
        ? "Encryption is selected. No archive has been created. Archive encryption and restore are not available yet."
        : "Encryption is off. A future archive would be readable by anyone with file access. Archive creation and restore are not available yet.";

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
        !EncryptBackup && Categories.Any(item => item.Id == "passwords" && item.IsSelected);

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
        OnPropertyChanged(nameof(SessionsSelected));
        OnPropertyChanged(nameof(AllCategoriesSelected));
        OnPropertyChanged(nameof(RequiresPlaintextConfirmation));
    }

    partial void OnEncryptBackupChanged(bool value)
    {
        if (!value) ClearSecrets();
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
