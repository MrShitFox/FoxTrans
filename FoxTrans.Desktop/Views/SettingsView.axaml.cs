using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FoxTrans.Desktop.Views;

public sealed partial class SettingsView : UserControl
{
    public SettingsView() => AvaloniaXamlLoader.Load(this);
}
