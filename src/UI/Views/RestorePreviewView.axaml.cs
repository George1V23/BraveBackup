using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UI.Views;

/// <summary>Displays approved read-only Restore inspection controls; native picking belongs to the owning window.</summary>
public partial class RestorePreviewView : UserControl
{
    public RestorePreviewView() => AvaloniaXamlLoader.Load(this);
}
