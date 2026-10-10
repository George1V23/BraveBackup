using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using UI.ViewModels;

namespace UI.Views;

/// <summary>
/// Interaction logic for MainWindow view.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(PickSaveFileAsync);
    }

    private async System.Threading.Tasks.Task<IStorageFile?> PickSaveFileAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider == null)
        {
            return null;
        }

        var defaultName = (DataContext as MainViewModel)?.TargetFilePath;
        if (string.IsNullOrWhiteSpace(defaultName) || !defaultName.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
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