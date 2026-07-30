using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FoxTrans.Desktop.Views;

public sealed partial class VrOverlayView : UserControl
{
    public VrOverlayView() => AvaloniaXamlLoader.Load(this);
}
