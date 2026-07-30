using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace FoxTrans.Desktop.Views;

public sealed partial class LiveStudioView : UserControl
{
    private readonly Border _waveformRail;

    public LiveStudioView()
    {
        AvaloniaXamlLoader.Load(this);
        _waveformRail = this.FindControl<Border>("WaveformRail")!;
        SizeChanged += (_, _) => UpdateWaveformWidth();
    }

    private void UpdateWaveformWidth()
    {
        _waveformRail.Width = Math.Clamp(Bounds.Width * 0.42, 320, 440);
    }
}
