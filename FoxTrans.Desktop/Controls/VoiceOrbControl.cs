using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.Controls;

public sealed class VoiceOrbControl : Control
{
    public static readonly StyledProperty<VoiceVisualizationMode> ModeProperty =
        AvaloniaProperty.Register<VoiceOrbControl, VoiceVisualizationMode>(
            nameof(Mode));
    public static readonly StyledProperty<AudioVisualFrame?> AudioFrameProperty =
        AvaloniaProperty.Register<VoiceOrbControl, AudioVisualFrame?>(
            nameof(AudioFrame));
    public static readonly StyledProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.Register<VoiceOrbControl, bool>(
            nameof(ReducedMotion));
    public static readonly StyledProperty<double> AnimationSecondsProperty =
        AvaloniaProperty.Register<VoiceOrbControl, double>(
            nameof(AnimationSeconds));
    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(AccentColor),
            Color.Parse("#7D8CFF"));
    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(SecondaryColor),
            Color.Parse("#57D7E8"));
    public static readonly StyledProperty<Color> ErrorColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(ErrorColor),
            Color.Parse("#E87878"));

    private readonly VoiceOrbAnimationModel _animation = new();

    static VoiceOrbControl()
    {
        AffectsRender<VoiceOrbControl>(
            ModeProperty,
            AudioFrameProperty,
            ReducedMotionProperty,
            AnimationSecondsProperty,
            AccentColorProperty,
            SecondaryColorProperty,
            ErrorColorProperty);
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

    public Color SecondaryColor
    {
        get => GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    public Color ErrorColor
    {
        get => GetValue(ErrorColorProperty);
        set => SetValue(ErrorColorProperty, value);
    }

    public VoiceOrbRenderState CurrentState { get; private set; } =
        new(1, 0.2f, 1.1f, 0, 0, false, new float[VoiceOrbAnimationModel.ContourPointCount]);

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

        Point center = new(Bounds.Width / 2, Bounds.Height / 2);
        double baseRadius = Math.Min(Bounds.Width, Bounds.Height) * 0.34;
        Color stateColor = Mode == VoiceVisualizationMode.Error
            ? ErrorColor
            : AccentColor;
        double haloRadius = baseRadius * CurrentState.HaloScale;

        context.DrawEllipse(
            new SolidColorBrush(WithAlpha(
                stateColor,
                (byte)(CurrentState.HaloOpacity * 55))),
            null,
            new Rect(
                center.X - haloRadius * 1.34,
                center.Y - haloRadius * 1.34,
                haloRadius * 2.68,
                haloRadius * 2.68));
        context.DrawEllipse(
            new SolidColorBrush(WithAlpha(
                stateColor,
                (byte)(CurrentState.HaloOpacity * 105))),
            null,
            new Rect(
                center.X - haloRadius,
                center.Y - haloRadius,
                haloRadius * 2,
                haloRadius * 2));

        StreamGeometry contour = BuildContour(
            center,
            baseRadius * CurrentState.CoreScale,
            CurrentState);
        var contourBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(WithAlpha(SecondaryColor, 226), 0),
                new GradientStop(WithAlpha(stateColor, 238), 0.55),
                new GradientStop(WithAlpha(AccentColor, 218), 1)
            ]
        };
        context.DrawGeometry(
            contourBrush,
            new Pen(
                new SolidColorBrush(WithAlpha(SecondaryColor, 150)),
                1.2),
            contour);

        double coreRadius =
            baseRadius * CurrentState.CoreScale * (0.72 + CurrentState.Energy * 0.05);
        var coreBrush = new RadialGradientBrush
        {
            Center = new RelativePoint(0.4, 0.34, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.35, 0.28, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.72, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.72, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(WithAlpha(Colors.White, 225), 0),
                new GradientStop(WithAlpha(SecondaryColor, 190), 0.32),
                new GradientStop(WithAlpha(stateColor, 224), 0.74),
                new GradientStop(WithAlpha(stateColor, 105), 1)
            ]
        };
        context.DrawEllipse(
            coreBrush,
            null,
            new Rect(
                center.X - coreRadius,
                center.Y - coreRadius,
                coreRadius * 2,
                coreRadius * 2));

        if (CurrentState.IsClipping)
        {
            context.DrawEllipse(
                null,
                new Pen(new SolidColorBrush(WithAlpha(ErrorColor, 205)), 2),
                new Rect(
                    center.X - haloRadius,
                    center.Y - haloRadius,
                    haloRadius * 2,
                    haloRadius * 2));
        }
    }

    private static StreamGeometry BuildContour(
        Point center,
        double radius,
        VoiceOrbRenderState state)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext path = geometry.Open();
        ReadOnlySpan<float> contour = state.Contour.Span;
        for (int index = 0; index < contour.Length; index++)
        {
            double angle =
                Math.PI * 2 * index / contour.Length + state.Rotation;
            double pointRadius = radius * contour[index];
            var point = new Point(
                center.X + Math.Cos(angle) * pointRadius,
                center.Y + Math.Sin(angle) * pointRadius);
            if (index == 0)
                path.BeginFigure(point, true);
            else
                path.LineTo(point);
        }
        path.EndFigure(true);
        return geometry;
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}
