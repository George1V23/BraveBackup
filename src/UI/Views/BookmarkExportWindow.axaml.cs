using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UI.ViewModels;

namespace UI.Views;

/// <summary>
/// Compatibility host for the reusable exporter; the shell now embeds the same controls in its Export tab.
/// </summary>
public partial class BookmarkExportWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BookmarkExportWindow"/> class.
    /// </summary>
    public BookmarkExportWindow() : this(null)
    {
    }

    /// <summary>Opens the existing exporter with a shell installation preselected.</summary>
    public BookmarkExportWindow(BraveBackup.Core.BraveAppInfo? initialApp)
    {
        InitializeComponent();
        var viewModel = new MainViewModel(PickSaveFileAsync);
        if (initialApp != null) viewModel.SelectedApp = new AppChannelItem(initialApp);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    private async System.Threading.Tasks.Task<IStorageFile?> PickSaveFileAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return null;
        }

        var defaultName = (DataContext as MainViewModel)?.TargetFilePath;
        if (string.IsNullOrWhiteSpace(defaultName) ||
            !defaultName.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
        {
            defaultName = "brave-bookmarks-backup.json";
        }
        else
        {
            defaultName = System.IO.Path.GetFileName(defaultName);
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Bookmarks Backup",
            DefaultExtension = "json",
            SuggestedFileName = defaultName,
            FileTypeChoices = new[]
            {
                new FilePickerFileType("JSON files (*.json)")
                {
                    Patterns = new[] { "*.json" },
                    MimeTypes = new[] { "application/json" }
                },
                new FilePickerFileType("All files (*.*)")
                {
                    Patterns = new[] { "*.*" }
                }
            }
        });

        return file;
    }
}