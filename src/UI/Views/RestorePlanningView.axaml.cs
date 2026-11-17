using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace UI.Views;

/// <summary>Hosts reviewed source choices, target mapping and write-free preflight controls.</summary>
public partial class RestorePlanningView : UserControl
{
    public RestorePlanningView() => AvaloniaXamlLoader.Load(this);
}
