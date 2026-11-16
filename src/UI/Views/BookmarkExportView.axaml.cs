using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UI.Views;

/// <summary>Embeds profile selection and HTML/JSON export without opening a separate window.</summary>
public partial class BookmarkExportView : UserControl
{
    /// <summary>Creates export controls; the host supplies a MainViewModel and its native file-picker callback.</summary>
    public BookmarkExportView() => AvaloniaXamlLoader.Load(this);
}