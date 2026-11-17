using Avalonia.Controls;
using Avalonia.Markup.Xaml;
namespace UI.Views;
/// <summary>Reviewed safety settings only; no restore or backup write action is exposed.</summary>
public partial class RestoreSafetyOptionsView : UserControl
{
    public RestoreSafetyOptionsView() => AvaloniaXamlLoader.Load(this);
}
