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

        var model = DataContext as MainViewModel;
        var format = model?.SelectedExportFormat ?? BookmarkExportFormatOption.BrowserHtml;
        var path = model?.FullDestinationPath;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = model?.ExportButtonText ?? "Export Bookmarks as HTML",
            DefaultExtension = format.Extension,
            SuggestedFileName = string.IsNullOrWhiteSpace(path)
                ? $"brave-bookmarks-export.{format.Extension}"
                : System.IO.Path.GetFileName(path),
            FileTypeChoices = new[]
            {
                new FilePickerFileType(format.DisplayName)
                {
                    Patterns = new[] { $"*.{format.Extension}" },
                    MimeTypes = new[] { format.MimeType }
                }
            }
        });

        return file;
    }
}