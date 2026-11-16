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
            ExportFilePicker = PickExportFileAsync
        };
        Opened += OnOpened;
        Closed += OnClosed;
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

    /// <summary>Chooses a JSON export file for the embedded Export tab without creating a backup archive.</summary>
    private async Task<IStorageFile?> PickExportFileAsync()
    {
        if (!StorageProvider.CanSave)
            throw new NotSupportedException("A save dialog is not available. Enter a destination path directly.");
        var path = (DataContext as ShellViewModel)?.ExportViewModel.FullDestinationPath;
        return await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Bookmarks as JSON",
            DefaultExtension = "json",
            SuggestedFileName =
                string.IsNullOrWhiteSpace(path) ? "brave-bookmarks-export.json" : Path.GetFileName(path),
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON files (*.json)")
                    { Patterns = new[] { "*.json" }, MimeTypes = new[] { "application/json" } }
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