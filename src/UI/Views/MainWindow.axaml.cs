using System;
using System.IO;
using System.Threading.Tasks;
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
            ConfirmBackupOverwrite = ConfirmBackupOverwriteAsync
        };
        Opened += OnOpened;
        Closed += OnClosed;
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
                new TextBlock { Text = "Replace this existing file after the new backup is verified?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new SelectableTextBlock { Text = destination, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Children = { cancel, replace } }
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