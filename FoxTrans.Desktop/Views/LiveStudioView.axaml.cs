using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FoxTrans.Desktop.Views;

public sealed partial class LiveStudioView : UserControl
{
    private const double StackedTextThreshold = 760;
    private readonly Grid _textPanels;
    private readonly Border _sourcePanel;
    private readonly Border _translationPanel;

    public LiveStudioView()
    {
        AvaloniaXamlLoader.Load(this);
        _textPanels = this.FindControl<Grid>("TextPanels")!;
        _sourcePanel = this.FindControl<Border>("SourcePanel")!;
        _translationPanel = this.FindControl<Border>("TranslationPanel")!;
        SizeChanged += (_, _) => UpdateTextLayout();
    }

    private void UpdateTextLayout()
    {
        bool stacked = Bounds.Width < StackedTextThreshold;
        _textPanels.ColumnDefinitions = new(stacked ? "*" : "*,*");
        _textPanels.RowDefinitions = new(stacked ? "Auto,Auto" : "Auto");
        Grid.SetColumn(_sourcePanel, 0);
        Grid.SetRow(_sourcePanel, 0);
        Grid.SetColumn(_translationPanel, stacked ? 0 : 1);
        Grid.SetRow(_translationPanel, stacked ? 1 : 0);
    }
}
