using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UI.Views;

/// <summary>Hosts the approved standalone CSV section; file dialogs remain on the owning window.</summary>
public partial class PasswordCsvExportView : UserControl
{
    public PasswordCsvExportView() => AvaloniaXamlLoader.Load(this);
}
