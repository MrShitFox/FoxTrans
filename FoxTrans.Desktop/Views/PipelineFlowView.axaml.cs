using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FoxTrans.Desktop.Views;

public sealed partial class PipelineFlowView : UserControl
{
    public PipelineFlowView() => AvaloniaXamlLoader.Load(this);
}
