using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using FoxTrans.Desktop.Models;
using SkiaSharp;
using System.Runtime.InteropServices;

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
            Color.Parse("#8B8FFF"));
    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(SecondaryColor),
            Color.Parse("#E6F7FF"));
    public static readonly StyledProperty<Color> SuccessColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(SuccessColor),
            Color.Parse("#6FC89A"));
    public static readonly StyledProperty<Color> ErrorColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(ErrorColor),
            Color.Parse("#E46F6F"));

    private readonly VoiceOrbAnimationModel _animation = new();
    private readonly VoiceOrbFluidSimulation _fluid = new();
    private int _shaderUnavailable;
    private int _shaderAvailable;

    static VoiceOrbControl()
    {
        AffectsRender<VoiceOrbControl>(
            ModeProperty,
            AudioFrameProperty,
            ReducedMotionProperty,
            AnimationSecondsProperty,
            AccentColorProperty,
            SecondaryColorProperty,
            SuccessColorProperty,
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

    public VoiceOrbRenderState CurrentState { get; private set; } =
        new(
            0.92f,
            0.1f,
            1.15f,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            false,
            new float[Pcm16AudioFeatureExtractor.SpectrumBandCount]);

    public bool IsRuntimeShaderAvailable =>
        Volatile.Read(ref _shaderAvailable) != 0;
    public bool IsUsingFallback =>
        Volatile.Read(ref _shaderUnavailable) != 0;
    public string? ShaderCompilationError =>
        VoiceOrbShaderOperation.CompilationError;

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
        byte[] fluidPixels = _fluid.Update(
            CurrentState,
            Math.Max(0, AnimationSeconds),
            ReducedMotion).ToArray();

        var operation = new VoiceOrbShaderOperation(
            new Rect(Bounds.Size),
            CurrentState,
            fluidPixels,
            AccentColor,
            SecondaryColor,
            SuccessColor,
            ErrorColor,
            MarkShaderAvailable,
            MarkShaderUnavailable);
        context.Custom(operation);

        if (VoiceOrbShaderOperation.CompilationError is not null ||
            Volatile.Read(ref _shaderUnavailable) != 0)
        {
            DrawIntentionalFallback(context);
        }
    }

    private void MarkShaderAvailable()
    {
        Volatile.Write(ref _shaderAvailable, 1);
        Volatile.Write(ref _shaderUnavailable, 0);
    }

    private void MarkShaderUnavailable() =>
        Volatile.Write(ref _shaderUnavailable, 1);

    private void DrawIntentionalFallback(DrawingContext context)
    {
        Point center = new(Bounds.Width / 2, Bounds.Height / 2);
        double radius = Math.Min(Bounds.Width, Bounds.Height) * 0.34;
        Rect circle = new(
            center.X - radius,
            center.Y - radius,
            radius * 2,
            radius * 2);
        var elements = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.18, 0.88, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.82, 0.12, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(
                    Color.FromArgb(
                        255,
                        (byte)(AccentColor.R * 0.38),
                        (byte)(AccentColor.G * 0.55),
                        (byte)(AccentColor.B * 0.72)),
                    0),
                new GradientStop(
                    Color.FromArgb(
                        255,
                        AccentColor.R,
                        AccentColor.G,
                        AccentColor.B),
                    0.43),
                new GradientStop(
                    Color.FromArgb(
                        245,
                        SecondaryColor.R,
                        SecondaryColor.G,
                        SecondaryColor.B),
                    1)
            ]
        };
        Color ringColor = Mode switch
        {
            VoiceVisualizationMode.Success => SuccessColor,
            VoiceVisualizationMode.Error => ErrorColor,
            _ => Color.FromArgb(105, 225, 244, 255)
        };
        double ringWidth = Math.Max(1, radius * 0.012);
        context.DrawEllipse(
            elements,
            new Pen(new SolidColorBrush(ringColor), ringWidth),
            circle);
    }

    private sealed class VoiceOrbShaderOperation : ICustomDrawOperation
    {
        private static readonly ThreadLocal<ShaderResources?> ThreadResources =
            new(CreateResources);
        private static string? _effectCompilationError;

        private readonly VoiceOrbRenderState _state;
        private readonly byte[] _fluidPixels;
        private readonly Color _accent;
        private readonly Color _secondary;
        private readonly Color _success;
        private readonly Color _error;
        private readonly Action _available;
        private readonly Action _unavailable;

        public VoiceOrbShaderOperation(
            Rect bounds,
            VoiceOrbRenderState state,
            byte[] fluidPixels,
            Color accent,
            Color secondary,
            Color success,
            Color error,
            Action available,
            Action unavailable)
        {
            Bounds = bounds;
            _state = state;
            _fluidPixels = fluidPixels;
            _accent = accent;
            _secondary = secondary;
            _success = success;
            _error = error;
            _available = available;
            _unavailable = unavailable;
        }

        public static string? CompilationError =>
            Volatile.Read(ref _effectCompilationError);
        public Rect Bounds { get; }

        public bool HitTest(Point point) => false;

        public void Render(ImmediateDrawingContext context)
        {
            ISkiaSharpApiLeaseFeature? leaseFeature =
                context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null)
            {
                _unavailable();
                return;
            }

            using ISkiaSharpApiLease lease = leaseFeature.Lease();
            ShaderResources? resources = ThreadResources.Value;
            if (resources is null)
            {
                _unavailable();
                return;
            }
            SKCanvas canvas = lease.SkCanvas;
            SKRuntimeShaderBuilder builder = resources.Builder;
            builder.Uniforms["resolution"] =
                new SKPoint((float)Bounds.Width, (float)Bounds.Height);
            builder.Uniforms["energy"] = _state.Energy;
            builder.Uniforms["clipping"] = _state.IsClipping ? 1f : 0f;
            builder.Uniforms["successPulse"] = _state.SuccessPulse;
            builder.Uniforms["errorPulse"] = _state.ErrorPulse;
            builder.Uniforms["accentA"] = ToColor(_accent);
            builder.Uniforms["accentB"] = ToColor(_secondary);
            builder.Uniforms["successColor"] = ToColor(_success);
            builder.Uniforms["errorColor"] = ToColor(_error);
            builder.Uniforms["fluidResolution"] =
                (float)VoiceOrbFluidSimulation.Resolution;

            using var fluidBitmap = new SKBitmap(
                new SKImageInfo(
                    VoiceOrbFluidSimulation.Resolution,
                    VoiceOrbFluidSimulation.Resolution,
                    SKColorType.Rgba8888,
                    SKAlphaType.Opaque));
            Marshal.Copy(
                _fluidPixels,
                0,
                fluidBitmap.GetPixels(),
                _fluidPixels.Length);
            using SKShader fluidShader = fluidBitmap.ToShader(
                SKShaderTileMode.Clamp,
                SKShaderTileMode.Clamp,
                new SKSamplingOptions(SKCubicResampler.Mitchell));
            builder.Children["fluidTexture"] = fluidShader;

            using SKShader? shader = builder.Build();
            if (shader is null)
            {
                _unavailable();
                return;
            }
            using var paint = new SKPaint
            {
                Shader = shader,
                IsAntialias = true,
                BlendMode = SKBlendMode.SrcOver
            };
            canvas.DrawRect(
                SKRect.Create(
                    (float)Bounds.X,
                    (float)Bounds.Y,
                    (float)Bounds.Width,
                    (float)Bounds.Height),
                paint);
            _available();
        }

        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose()
        {
        }

        private static SKColorF ToColor(Color value) =>
            new(
                value.R / 255f,
                value.G / 255f,
                value.B / 255f,
                value.A / 255f);

        private static SKRuntimeEffect? CreateEffect(out string? error)
        {
            SKRuntimeEffect? effect =
                SKRuntimeEffect.CreateShader(ShaderSource, out string text);
            error = effect is null
                ? string.IsNullOrWhiteSpace(text)
                    ? "Skia could not compile the voice shader."
                    : text
                : null;
            return effect;
        }

        private static ShaderResources? CreateResources()
        {
            SKRuntimeEffect? effect =
                CreateEffect(out string? compilationError);
            Volatile.Write(
                ref _effectCompilationError,
                compilationError);
            return effect is null ? null : new ShaderResources(effect);
        }

        private sealed class ShaderResources
        {
            public ShaderResources(SKRuntimeEffect effect)
            {
                Effect = effect;
                Builder = new(effect);
            }

            public SKRuntimeEffect Effect { get; }
            public SKRuntimeShaderBuilder Builder { get; }
        }

        private const string ShaderSource = """
            uniform float2 resolution;
            uniform float energy;
            uniform float clipping;
            uniform float successPulse;
            uniform float errorPulse;
            uniform float4 accentA;
            uniform float4 accentB;
            uniform float4 successColor;
            uniform float4 errorColor;
            uniform float fluidResolution;
            uniform shader fluidTexture;

            half4 main(float2 fragCoord) {
                float shortest = min(resolution.x, resolution.y);
                float2 uv = (fragCoord - resolution * 0.5) / shortest;
                float radius = 0.340;
                float distanceToCenter = length(uv);
                float antialias = max(1.25 / shortest, 0.0015);
                float circleMask =
                    1.0 - smoothstep(
                        radius - antialias,
                        radius + antialias,
                        distanceToCenter);

                float2 p = uv / radius;
                float2 fluidCoord =
                    (p * 0.5 + 0.5) * (fluidResolution - 1.0) + 0.5;
                half4 fluid = fluidTexture.eval(fluidCoord);
                float flow = fluid.g;
                float pressure = fluid.b * 2.0 - 1.0;
                float waterLeft =
                    fluidTexture.eval(fluidCoord + float2(-1.0, 0.0)).r;
                float waterRight =
                    fluidTexture.eval(fluidCoord + float2(1.0, 0.0)).r;
                float waterTop =
                    fluidTexture.eval(fluidCoord + float2(0.0, -1.0)).r;
                float waterBottom =
                    fluidTexture.eval(fluidCoord + float2(0.0, 1.0)).r;
                float filteredWater = clamp(
                    (fluid.r * 4.0 +
                     waterLeft + waterRight +
                     waterTop + waterBottom) / 8.0,
                    0.0,
                    1.0);
                float waterShare = filteredWater;
                float airShare = 1.0 - waterShare;
                float mixedShare =
                    4.0 * waterShare * airShare;
                float2 gradient = float2(
                    waterRight - waterLeft,
                    waterBottom - waterTop);
                float interfaceLight =
                    4.0 * waterShare * airShare;
                float3 surfaceNormal = normalize(
                    float3(-gradient * 3.2, 1.0));
                float volumeLight = clamp(
                    dot(
                        surfaceNormal,
                        normalize(float3(-0.44, -0.58, 0.68))) *
                        0.5 + 0.5,
                    0.0,
                    1.0);

                float3 waterDeep =
                    accentA.rgb * float3(0.16, 0.40, 0.68);
                float3 waterBright =
                    accentA.rgb * float3(0.92, 1.06, 1.16);
                float3 waterColor = mix(
                    waterDeep,
                    waterBright,
                    clamp(
                        0.22 + volumeLight * 0.46 +
                        flow * 0.16 + energy * 0.05,
                        0.0,
                        1.0));
                float3 windShadow =
                    mix(accentA.rgb, accentB.rgb, 0.84) * 0.72;
                float3 windColor = mix(
                    windShadow,
                    accentB.rgb,
                    clamp(
                        0.38 + volumeLight * 0.34 +
                        flow * 0.12,
                        0.0,
                        1.0));
                float3 color = mix(waterColor, windColor, airShare);
                float3 mixedColor =
                    mix(accentA.rgb, accentB.rgb, 0.52) *
                    (0.92 + volumeLight * 0.18);
                color = mix(
                    color,
                    mixedColor,
                    mixedShare * (0.20 + flow * 0.12));
                color += mix(accentA.rgb, accentB.rgb, 0.64) *
                    interfaceLight *
                    (0.13 + energy * 0.10);
                color += mix(accentA.rgb, accentB.rgb, 0.48) *
                    pressure * (0.055 + energy * 0.045);
                color *=
                    0.82 + waterShare * 0.04 +
                    airShare * 0.05 + energy * 0.10;

                float innerShade =
                    smoothstep(0.20, 1.0, distanceToCenter / radius);
                color *= 1.0 - innerShade * 0.20;
                color += accentB.rgb *
                    pow(
                        clamp(
                            1.0 - distanceToCenter / radius,
                            0.0,
                            1.0),
                        2.1) * 0.10;

                float rim =
                    smoothstep(
                        radius - 0.013,
                        radius - 0.004,
                        distanceToCenter) * circleMask;
                float successSignal = successPulse;
                float errorSignal = errorPulse;
                float3 neutralRim = mix(accentA.rgb, accentB.rgb, 0.82);
                float3 rimColor = mix(
                    neutralRim,
                    successColor.rgb,
                    clamp(successSignal * 1.8, 0.0, 1.0));
                rimColor = mix(
                    rimColor,
                    errorColor.rgb,
                    clamp(errorSignal * 1.5, 0.0, 1.0));
                float rimStrength =
                    0.18 +
                    successSignal * 0.76 +
                    errorSignal * 0.72 +
                    clipping * 0.28;
                color = mix(color, rimColor, rim * rimStrength);

                float alpha = circleMask;
                return half4(color * alpha, alpha);
            }
            """;
    }
}
