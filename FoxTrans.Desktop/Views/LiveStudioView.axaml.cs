using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using FoxTrans.Desktop.Controls;

namespace FoxTrans.Desktop.Views;

public sealed partial class LiveStudioView : UserControl
{
    private readonly VoiceOrbControl _orb;

    public LiveStudioView()
    {
        AvaloniaXamlLoader.Load(this);
        _orb = this.FindControl<VoiceOrbControl>("Orb")!;
        SizeChanged += (_, _) => UpdateOrbSize();
    }

    private void UpdateOrbSize()
    {
        double diameter = Math.Clamp(
            Math.Min(Bounds.Width * 0.46, Bounds.Height * 0.62),
            318,
            432);
        _orb.Width = diameter;
        _orb.Height = diameter;
    }
}
