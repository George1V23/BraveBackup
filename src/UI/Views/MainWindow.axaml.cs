using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using Avalonia.Threading;
using BraveBackup.Core;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using UI.ViewModels;

namespace UI.Views;

/// <summary>Hosts approved shell controls; platform dialogs and window/input routing stay in the view.</summary>
public partial class MainWindow : Window
{
    /// <summary>Creates the shell, starting read-only discovery after the window opens.</summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new ShellViewModel(pickBackupPath: PickBackupPathAsync)
        {
            ExportFilePicker = PickExportFileAsync,
            ConfirmBackupOverwrite = ConfirmBackupOverwriteAsync,
            ConfirmUnencryptedExtensionData = ConfirmUnencryptedExtensionDataAsync,
            ConfirmUnencryptedNativeSessions = ConfirmUnencryptedNativeSessionsAsync,
            ConfirmUnencryptedTabManagerPlus = ConfirmUnencryptedTabManagerPlusAsync,
            ConfirmRawPasswordPreservation = ConfirmRawPasswordPreservationAsync,
            ConfirmPasswordCsvArchiveSave = ConfirmPasswordCsvArchiveSaveAsync
        };
        ((ShellViewModel)DataContext).RestorePreview.PickSource = PickRestoreSourceAsync;
        var csv = ((ShellViewModel)DataContext).PasswordCsvExport;
        csv.PickDestination = PickPasswordCsvPathAsync;
        csv.ConfirmPlaintextSave = ConfirmPasswordCsvSaveAsync;
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>Selects an existing local archive without creating files or touching browser profiles.</summary>
    private async Task<string?> PickRestoreSourceAsync()
    {
        if (!StorageProvider.CanOpen) throw new InvalidOperationException("A local open picker is unavailable.");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose backup to inspect", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("BraveBackup archive (*.bravebackup)") { Patterns = new[] { "*.bravebackup" } } }
        });
        return files.Count == 1 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>Shows fresh standalone plaintext-save/replacement consent, closing without approval on cancellation.</summary>
    private async Task<bool> ConfirmPasswordCsvSaveAsync(PasswordCsvExportWarning warning, CancellationToken token)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ConfirmPasswordCsvSaveAsync(warning, token));
        token.ThrowIfCancellationRequested();
        var dialog = new Window { Title = "Save readable passwords?", Width = 600, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel" }; var save = new Button { Content = "Save plaintext CSV" };
        cancel.Click += (_, _) => dialog.Close(false); save.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16, Children =
        {
            new TextBlock { Text = warning.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, save } }
        } };
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Obtains fresh CSV archive consent on the UI thread; cancellation or closing declines.</summary>
    private async Task<bool> ConfirmPasswordCsvArchiveSaveAsync(PasswordCsvArchiveWarning warning, CancellationToken token)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ConfirmPasswordCsvArchiveSaveAsync(warning, token));
        token.ThrowIfCancellationRequested();
        var dialog = new Window { Title = "Save password CSV inside backup?", Width = 600, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var cancel = new Button { Content = "Cancel backup" }; var save = new Button { Content = "Save backup with CSV" };
        cancel.Click += (_, _) => dialog.Close(false); save.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16, Children =
        {
            new TextBlock { Text = warning.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, save } }
        } };
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Selects a standalone CSV destination without creating the file or accessing credentials.</summary>
    private async Task<string?> PickPasswordCsvPathAsync(string currentPath)
    {
        if (!StorageProvider.CanSave) throw new NotSupportedException("A local save picker is unavailable.");
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose password CSV destination", DefaultExtension = "csv",
            SuggestedFileName = string.IsNullOrWhiteSpace(currentPath) ? "brave-passwords.csv" : Path.GetFileName(currentPath),
            FileTypeChoices = new[] { new FilePickerFileType("Password CSV (*.csv)") { Patterns = new[] { "*.csv" }, MimeTypes = new[] { "text/csv" } } }
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Dispatches the approved raw-or-cancel choice to the UI; cancellation closes it without approval.</summary>
    private async Task<bool> ConfirmRawPasswordPreservationAsync(PasswordRawFallbackWarning warning, CancellationToken token)
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return await Dispatcher.UIThread.InvokeAsync(() => ConfirmRawPasswordPreservationAsync(warning, token));
        token.ThrowIfCancellationRequested();
        var dialog = new Window
        {
            Title = "Password capture could not complete", Width = 600, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel backup" };
        var preserve = new Button { Content = "Preserve raw files" };
        cancel.Click += (_, _) => dialog.Close(false); preserve.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = warning.Message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new SelectableTextBlock { Text = warning.ProfileDirectory, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = "The backup remains encrypted. Raw files may not be recoverable on another system.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, preserve } }
            }
        };
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close(false)));
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Obtains explicit approval to replace the particular existing destination.</summary>
    private async Task<bool> ConfirmBackupOverwriteAsync(string destination)
    {
        var dialog = new Window
        {
            Title = "Replace existing backup?", Width = 560, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel" };
        var replace = new Button { Content = "Replace backup" };
        cancel.Click += (_, _) => dialog.Close(false);
        replace.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Replace this existing file after the new backup is verified?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new SelectableTextBlock { Text = destination, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, replace }
                }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Confirms the readable private-data risk for this specific unencrypted extension backup.</summary>
    private async Task<bool> ConfirmUnencryptedExtensionDataAsync(string destination)
    {
        var dialog = new Window
        {
            Title = "Save extension data without encryption?", Width = 580, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel" };
        var save = new Button { Content = "Save unencrypted" };
        cancel.Click += (_, _) => dialog.Close(false);
        save.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "Copied extension data may contain readable private information or authentication material. Anyone with access to this file can read its contents. Save this backup without encryption?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new SelectableTextBlock { Text = destination, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                    { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, save } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Confirms readable private navigation metadata before this native-session file save.</summary>
    private async Task<bool> ConfirmUnencryptedNativeSessionsAsync(string destination)
    {
        var dialog = new Window
        {
            Title = "Save native session files without encryption?", Width = 580, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel" };
        var save = new Button { Content = "Save unencrypted" };
        cancel.Click += (_, _) => dialog.Close(false);
        save.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "Native session files contain browsing data beyond portable URLs and titles. Anyone with file access can read this unencrypted backup. Save these raw files without encryption?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new SelectableTextBlock { Text = destination, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                    { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, save } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Confirms readable saved URLs, titles and private-window records for this specific unencrypted save.</summary>
    private async Task<bool> ConfirmUnencryptedTabManagerPlusAsync(string destination)
    {
        var dialog = new Window
        {
            Title = "Save saved windows without encryption?", Width = 580, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var cancel = new Button { Content = "Cancel" };
        var save = new Button { Content = "Save unencrypted" };
        cancel.Click += (_, _) => dialog.Close(false);
        save.Click += (_, _) => dialog.Close(true);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text =
                        "Saved windows contain readable URLs, titles, window names and any stored private-window records. Anyone with file access can read this unencrypted backup. Save without encryption?",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                },
                new SelectableTextBlock { Text = destination, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                    { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, save } }
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    /// <summary>Starts discovery without blocking construction or the dispatcher.</summary>
    private async void OnOpened(object? sender, EventArgs args)
    {
        if (DataContext is ShellViewModel viewModel) await viewModel.RefreshAsync();
    }

    /// <summary>Releases shell-owned avatars and clears password text when closing.</summary>
    private void OnClosed(object? sender, EventArgs args) => (DataContext as ShellViewModel)?.Dispose();

    /// <summary>Routes a header double-click to its independent bulk selection.</summary>
    private void OnInstallationDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (sender is Control { DataContext: InstallationViewModel installation })
        {
            installation.ToggleBulkSelectionCommand.Execute(null);
            args.Handled = true;
        }
    }

    /// <summary>Provides Shift+Space as a keyboard equivalent for double-click bulk selection.</summary>
    private void OnInstallationKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key != Key.Space || !args.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;
        if (sender is Control { DataContext: InstallationViewModel installation })
        {
            installation.ToggleBulkSelectionCommand.Execute(null);
            args.Handled = true;
        }
    }

    /// <summary>Chooses an HTML or JSON file matching the selected bookmark format, without creating a backup archive.</summary>
    private async Task<IStorageFile?> PickExportFileAsync()
    {
        if (!StorageProvider.CanSave)
            throw new NotSupportedException("A save dialog is not available. Enter a destination path directly.");
        var model = (DataContext as ShellViewModel)?.ExportViewModel;
        var format = model?.SelectedExportFormat ?? BookmarkExportFormatOption.BrowserHtml;
        var path = model?.FullDestinationPath;
        return await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = model?.ExportButtonText ?? "Export Bookmarks as HTML",
            DefaultExtension = format.Extension,
            SuggestedFileName =
                string.IsNullOrWhiteSpace(path) ? $"brave-bookmarks-export.{format.Extension}" : Path.GetFileName(path),
            FileTypeChoices = new[]
            {
                new FilePickerFileType(format.DisplayName)
                    { Patterns = new[] { $"*.{format.Extension}" }, MimeTypes = new[] { format.MimeType } }
            }
        });
    }

    /// <summary>Chooses an archive destination without creating an archive.</summary>
    private async Task<string?> PickBackupPathAsync(string currentPath)
    {
        if (!StorageProvider.CanSave) return null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose backup destination",
            DefaultExtension = "bravebackup",
            SuggestedFileName = Path.GetFileName(currentPath),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Brave backup (*.bravebackup)") { Patterns = new[] { "*.bravebackup" } }
            }
        });
        return file?.TryGetLocalPath();
    }
}