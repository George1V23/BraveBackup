using Avalonia.Controls;

namespace UI.Views;

/// <summary>Embeds the established profile selection and JSON export workflow without opening a separate window.</summary>
public partial class BookmarkExportView : UserControl
{
    /// <summary>Creates export controls; the host supplies a MainViewModel and its native file-picker callback.</summary>
    public BookmarkExportView() => InitializeComponent();
}