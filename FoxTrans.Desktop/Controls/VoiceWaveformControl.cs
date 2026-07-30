using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.Controls;

public sealed class VoiceWaveformControl : Control
{
    public static readonly StyledProperty<VoiceVisualizationMode> ModeProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, VoiceVisualizationMode>(
            nameof(Mode));
    public static readonly StyledProperty<AudioVisualFrame?> AudioFrameProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, AudioVisualFrame?>(
            nameof(AudioFrame));
    public static readonly StyledProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, bool>(
            nameof(ReducedMotion));
    public static readonly StyledProperty<double> AnimationSecondsProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, double>(
            nameof(AnimationSeconds));
    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, Color>(
            nameof(AccentColor),
            Color.Parse("#39BCE8"));
    public static readonly StyledProperty<Color> HighlightColorProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, Color>(
            nameof(HighlightColor),
            Color.Parse("#E4F6FF"));
    public static readonly StyledProperty<Color> SuccessColorProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, Color>(
            nameof(SuccessColor),
            Color.Parse("#6FC89A"));
    public static readonly StyledProperty<Color> ErrorColorProperty =
        AvaloniaProperty.Register<VoiceWaveformControl, Color>(
            nameof(ErrorColor),
            Color.Parse("#E46F6F"));

    private readonly VoiceWaveformAnimationModel _animation = new();
    private readonly SolidColorBrush _baseBrush = new();
    private readonly SolidColorBrush _highlightBrush = new();

    static VoiceWaveformControl()
    {
        AffectsRender<VoiceWaveformControl>(
            ModeProperty,
            AudioFrameProperty,
            ReducedMotionProperty,
            AnimationSecondsProperty,
            AccentColorProperty,
            HighlightColorProperty,
            SuccessColorProperty,
            ErrorColorProperty);
    }

    public VoiceWaveformControl()
    {
        Height = 76;
    }

    public VoiceVisualizationMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public AudioVisualFrame? AudioFrame
    {
        get => GetValue(AudioFrameProperty);
        set => SetValue(AudioFrameProperty, value);
    }

    public bool ReducedMotion
    {
        get => GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public double AnimationSeconds
    {
        get => GetValue(AnimationSecondsProperty);
        set => SetValue(AnimationSecondsProperty, value);
    }

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public Color HighlightColor
    {
        get => GetValue(HighlightColorProperty);
        set => SetValue(HighlightColorProperty, value);
    }

    public Color SuccessColor
    {
        get => GetValue(SuccessColorProperty);
        set => SetValue(SuccessColorProperty, value);
    }

    public Color ErrorColor
    {
        get => GetValue(ErrorColorProperty);
        set => SetValue(ErrorColorProperty, value);
    }

    public VoiceWaveformRenderState CurrentState { get; private set; } =
        new(
            1,
            0.22f,
            0,
            1,
            0.006f,
            -1,
            0,
            0,
            0,
            false,
            new float[VoiceWaveformAnimationModel.BarCount]);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        DateTimeOffset time = DateTimeOffset.UnixEpoch.AddSeconds(
            Math.Max(0, AnimationSeconds));
        CurrentState = _animation.Update(
            Mode,
            AudioFrame,
            time,
            ReducedMotion);

        Color stateColor = Mode switch
        {
            VoiceVisualizationMode.Success => SuccessColor,
            VoiceVisualizationMode.Error => ErrorColor,
            _ when CurrentState.IsClipping => ErrorColor,
            _ => AccentColor
        };
        float effect = Math.Max(
            CurrentState.SuccessPulse,
            CurrentState.ErrorPulse);
        byte baseAlpha = (byte)Math.Clamp(
            (122 + CurrentState.BorderIntensity * 78 +
            effect * 34) * CurrentState.Opacity,
            0,
            255);
        _baseBrush.Color = WithAlpha(stateColor, baseAlpha);
        _highlightBrush.Color = WithAlpha(
            Mode == VoiceVisualizationMode.Success
                ? SuccessColor
                : Mode == VoiceVisualizationMode.Error
                    ? ErrorColor
                    : HighlightColor,
            (byte)Math.Clamp(
                (148 + effect * 92) * CurrentState.Opacity,
                0,
                255));

        ReadOnlySpan<float> bars = CurrentState.BarHeights.Span;
        double barWidth = Math.Clamp(
            Bounds.Width / (bars.Length * 6.2),
            3.2,
            5.2);
        double gap = barWidth * 2.2;
        double totalWidth =
            bars.Length * barWidth + (bars.Length - 1) * gap;
        double startX = (Bounds.Width - totalWidth) / 2;
        double maximumHeight = Math.Max(4, Bounds.Height - 4);
        double minimumHeight = 2.4;
        double centerY = Bounds.Height / 2;

        for (int index = 0; index < bars.Length; index++)
        {
            double height = Math.Clamp(
                minimumHeight + bars[index] * (maximumHeight - minimumHeight),
                minimumHeight,
                maximumHeight);
            var rect = new Rect(
                startX + index * (barWidth + gap),
                centerY - height / 2,
                barWidth,
                height);
            double radius = barWidth / 2;
            context.DrawRectangle(
                _baseBrush,
                null,
                rect,
                radius,
                radius);

            if (ShouldHighlight(index, bars.Length, CurrentState))
            {
                context.DrawRectangle(
                    _highlightBrush,
                    null,
                    rect,
                    radius,
                    radius);
            }
        }
    }

    private static bool ShouldHighlight(
        int index,
        int count,
        VoiceWaveformRenderState state)
    {
        if (state.SweepProgress >= 0)
        {
            float position = state.SweepProgress * (count + 2) - 1;
            return Math.Abs(index - position) <= 1.15f;
        }

        return (state.SuccessPulse > 0.12f ||
                state.ErrorPulse > 0.12f) &&
            Math.Abs(index - (count - 1) / 2f) <=
                1.5f + Math.Max(state.SuccessPulse, state.ErrorPulse) * 4;
    }

    private static Color WithAlpha(Color value, byte alpha) =>
        Color.FromArgb(alpha, value.R, value.G, value.B);
}
