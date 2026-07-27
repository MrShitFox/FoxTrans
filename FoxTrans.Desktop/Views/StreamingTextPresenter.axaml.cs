using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FoxTrans.Desktop.Views;

public sealed partial class StreamingTextPresenter : UserControl
{
    public StreamingTextPresenter() => AvaloniaXamlLoader.Load(this);
}
