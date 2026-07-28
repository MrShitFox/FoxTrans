using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using FoxTrans.Desktop.Models;
using SkiaSharp;

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
            Color.Parse("#62D4D0"));
    public static readonly StyledProperty<Color> ErrorColorProperty =
        AvaloniaProperty.Register<VoiceOrbControl, Color>(
            nameof(ErrorColor),
            Color.Parse("#E46F6F"));

    private readonly VoiceOrbAnimationModel _animation = new();
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

        var operation = new VoiceOrbShaderOperation(
            new Rect(Bounds.Size),
            CurrentState,
            (float)Math.Max(0, AnimationSeconds),
            AccentColor,
            SecondaryColor,
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
        double radius = Math.Min(Bounds.Width, Bounds.Height) *
            0.31 *
            CurrentState.CoreScale;
        Color active = Mode == VoiceVisualizationMode.Error
            ? ErrorColor
            : AccentColor;
        var halo = new RadialGradientBrush
        {
            Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
            GradientOrigin =
                new RelativePoint(0.46, 0.42, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(
                    Color.FromArgb(
                        (byte)(42 + CurrentState.Energy * 48),
                        active.R,
                        active.G,
                        active.B),
                    0),
                new GradientStop(
                    Color.FromArgb(26, active.R, active.G, active.B),
                    0.48),
                new GradientStop(Colors.Transparent, 1)
            ]
        };
        double haloRadius = radius * 1.72;
        context.DrawEllipse(
            halo,
            null,
            new Rect(
                center.X - haloRadius,
                center.Y - haloRadius,
                haloRadius * 2,
                haloRadius * 2));

        var core = new RadialGradientBrush
        {
            Center = new RelativePoint(0.42, 0.38, RelativeUnit.Relative),
            GradientOrigin =
                new RelativePoint(0.36, 0.30, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.62, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.62, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(
                    Color.FromArgb(235, 230, 235, 255),
                    0),
                new GradientStop(
                    Color.FromArgb(
                        210,
                        SecondaryColor.R,
                        SecondaryColor.G,
                        SecondaryColor.B),
                    0.31),
                new GradientStop(
                    Color.FromArgb(225, active.R, active.G, active.B),
                    0.72),
                new GradientStop(
                    Color.FromArgb(80, active.R, active.G, active.B),
                    1)
            ]
        };
        context.DrawEllipse(
            core,
            null,
            new Rect(
                center.X - radius,
                center.Y - radius,
                radius * 2,
                radius * 2));
    }

    private sealed class VoiceOrbShaderOperation : ICustomDrawOperation
    {
        private static readonly ThreadLocal<ShaderResources?> ThreadResources =
            new(CreateResources);
        private static string? _effectCompilationError;

        private readonly VoiceOrbRenderState _state;
        private readonly float _time;
        private readonly Color _accent;
        private readonly Color _secondary;
        private readonly Color _error;
        private readonly Action _available;
        private readonly Action _unavailable;

        public VoiceOrbShaderOperation(
            Rect bounds,
            VoiceOrbRenderState state,
            float time,
            Color accent,
            Color secondary,
            Color error,
            Action available,
            Action unavailable)
        {
            Bounds = bounds;
            _state = state;
            _time = time;
            _accent = accent;
            _secondary = secondary;
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
            ReadOnlySpan<float> bands = _state.SpectralBands.Span;
            builder.Uniforms["resolution"] =
                new SKPoint((float)Bounds.Width, (float)Bounds.Height);
            builder.Uniforms["time"] = _time;
            builder.Uniforms["rms"] = _state.Rms;
            builder.Uniforms["peak"] = _state.Peak;
            builder.Uniforms["peakImpulse"] = _state.PeakImpulse;
            builder.Uniforms["clipping"] = _state.IsClipping ? 1f : 0f;
            builder.Uniforms["speechActivity"] = _state.SpeechActivity;
            builder.Uniforms["processingIntensity"] =
                _state.ProcessingIntensity;
            builder.Uniforms["lowPhase"] = _state.LowPhase;
            builder.Uniforms["midPhase"] = _state.MidPhase;
            builder.Uniforms["highPhase"] = _state.HighPhase;
            builder.Uniforms["successPulse"] = _state.SuccessPulse;
            builder.Uniforms["errorPulse"] = _state.ErrorPulse;
            builder.Uniforms["mode"] = _state.StateValue;
            builder.Uniforms["coreScale"] = _state.CoreScale;
            builder.Uniforms["haloOpacity"] = _state.HaloOpacity;
            builder.Uniforms["haloScale"] = _state.HaloScale;
            builder.Uniforms["reducedMotion"] =
                _state.ReducedMotionFactor;
            builder.Uniforms["bands0"] = BandVector(bands, 0);
            builder.Uniforms["bands1"] = BandVector(bands, 4);
            builder.Uniforms["bands2"] = BandVector(bands, 8);
            builder.Uniforms["accentA"] = ToColor(_accent);
            builder.Uniforms["accentB"] = ToColor(_secondary);
            builder.Uniforms["errorColor"] = ToColor(_error);

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

        private static SKColorF BandVector(
            ReadOnlySpan<float> bands,
            int offset) =>
            new(
                Value(bands, offset),
                Value(bands, offset + 1),
                Value(bands, offset + 2),
                Value(bands, offset + 3));

        private static float Value(ReadOnlySpan<float> values, int index) =>
            index < values.Length ? values[index] : 0;

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
            uniform float time;
            uniform float rms;
            uniform float peak;
            uniform float peakImpulse;
            uniform float clipping;
            uniform float speechActivity;
            uniform float processingIntensity;
            uniform float lowPhase;
            uniform float midPhase;
            uniform float highPhase;
            uniform float successPulse;
            uniform float errorPulse;
            uniform float mode;
            uniform float coreScale;
            uniform float haloOpacity;
            uniform float haloScale;
            uniform float reducedMotion;
            uniform float4 bands0;
            uniform float4 bands1;
            uniform float4 bands2;
            uniform float4 accentA;
            uniform float4 accentB;
            uniform float4 errorColor;

            float hash21(float2 p) {
                p = fract(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return fract(p.x * p.y);
            }

            float noise(float2 p) {
                float2 i = floor(p);
                float2 f = fract(p);
                float2 u = f * f * (3.0 - 2.0 * f);
                return mix(
                    mix(hash21(i), hash21(i + float2(1.0, 0.0)), u.x),
                    mix(hash21(i + float2(0.0, 1.0)),
                        hash21(i + float2(1.0, 1.0)), u.x),
                    u.y);
            }

            float fbm(float2 p) {
                float value = 0.0;
                float amplitude = 0.52;
                for (int octave = 0; octave < 5; ++octave) {
                    value += amplitude * noise(p);
                    p = float2(
                        1.64 * p.x - 1.18 * p.y,
                        1.18 * p.x + 1.64 * p.y) + 7.31;
                    amplitude *= 0.49;
                }
                return value;
            }

            float warpedFog(float2 p, float motion) {
                float2 q = float2(
                    fbm(p + float2(0.0, motion)),
                    fbm(p + float2(4.6, -motion * 0.77)));
                float2 r = float2(
                    fbm(p + 2.1 * q + float2(1.7, 8.2) +
                        motion * 0.18),
                    fbm(p + 2.1 * q + float2(8.3, 2.8) -
                        motion * 0.14));
                float2 s = float2(
                    fbm(p + 1.6 * r + float2(3.1, 1.2)),
                    fbm(p + 1.8 * r + float2(5.4, 9.1)));
                return fbm(p + 1.55 * s + 0.65 * r);
            }

            half4 main(float2 fragCoord) {
                float shortest = min(resolution.x, resolution.y);
                float2 uv = (fragCoord - resolution * 0.5) / shortest;
                float low = dot(bands0, float4(0.31, 0.28, 0.23, 0.18));
                float mid = dot(bands1, float4(0.22, 0.28, 0.28, 0.22));
                float high = dot(bands2, float4(0.18, 0.23, 0.28, 0.31));
                float energy = clamp(
                    rms * 2.15 + peak * 0.20 + low * 0.20 +
                    mid * 0.16 + high * 0.08,
                    0.0,
                    1.0);
                float voiceDrive =
                    speechActivity * (0.18 + energy * 0.82);
                float activeTime = time * reducedMotion;
                float radius = 0.340 * coreScale;
                float angle = activeTime * (0.08 + processingIntensity * 0.16);
                float2 flowUv = float2(
                    cos(angle) * uv.x - sin(angle) * uv.y,
                    sin(angle) * uv.x + cos(angle) * uv.y);
                float2 lowDrift = float2(
                    sin(lowPhase * 0.73 + 0.8),
                    cos(lowPhase * 0.61 - 0.4));
                float2 midWarp = float2(
                    cos(midPhase * 0.91 + 1.7),
                    sin(midPhase * 0.83 - 0.6));
                flowUv *= 3.18 - low * 0.52;
                flowUv += float2(
                    sin(activeTime * 0.21),
                    cos(activeTime * 0.17)) * 0.23;
                flowUv += lowDrift * low * (0.42 + voiceDrive * 0.30);
                flowUv += midWarp * mid * (0.34 + voiceDrive * 0.44);

                float fogA = warpedFog(
                    flowUv + lowDrift * (low * 1.18 + voiceDrive * 0.12),
                    lowPhase * (0.42 + low * 0.48));
                float fogB = warpedFog(
                    flowUv * (1.38 + mid * 0.20) +
                        float2(7.2, -3.7) + midWarp * mid * 1.35,
                    -midPhase * (0.34 + mid * 0.62));
                float fogC = warpedFog(
                    flowUv * 2.08 + float2(-4.4, 6.1),
                    highPhase * (0.16 + high * 0.48));
                float fog = fogA * 0.48 + fogB * 0.34 + fogC * 0.18;
                float slowWave = 0.5 + 0.5 * sin(
                    (flowUv.x * 1.52 + flowUv.y * 0.86) *
                        (3.2 + low * 3.6) +
                    lowPhase);
                float midStructure = warpedFog(
                    flowUv * (1.10 + mid * 0.34) +
                        midWarp * (0.9 + mid),
                    midPhase * 0.72);
                float fineDetail = noise(
                    flowUv * (10.5 + high * 5.5) +
                    float2(
                        sin(highPhase * 1.17),
                        cos(highPhase * 1.31)) * 2.3);
                fog += (slowWave - 0.5) * low *
                    (0.32 + voiceDrive * 0.48);
                fog += (midStructure - 0.5) * mid *
                    (0.62 + voiceDrive * 0.72);
                fog += (fineDetail - 0.5) * high *
                    (0.22 + voiceDrive * 0.24);

                float2 impulseCenter = float2(
                    sin(lowPhase * 1.37 + 1.2),
                    cos(midPhase * 1.11 - 0.7)) * radius * 0.34;
                float impulseDistance = length(uv - impulseCenter);
                float peakPocket = exp(
                    -pow(
                        impulseDistance / max(radius * 0.24, 0.001),
                        2.0) * 2.7) * peakImpulse;
                float peakRing = exp(
                    -pow(
                        (impulseDistance - radius * 0.17) /
                            max(radius * 0.075, 0.001),
                        2.0)) * peakImpulse;
                fog += peakPocket * 0.50 + peakRing * 0.20;

                float directional = 0.5 + 0.5 * sin(
                    (flowUv.x * 2.4 - flowUv.y * 0.8) * 3.14159 -
                    activeTime * 1.25);
                fog = mix(
                    fog,
                    fog * 0.72 + directional * 0.42,
                    processingIntensity * 0.62);

                float fogContrast =
                    1.0 + voiceDrive * 1.05 + energy * 0.38 + mid * 0.42;
                float shapedFog = clamp(
                    0.5 + (fog - 0.5) * fogContrast,
                    0.0,
                    1.0);
                float turbulence =
                    (shapedFog - 0.52) *
                    (0.013 + low * 0.014 + mid * 0.038 +
                     high * 0.022 + voiceDrive * 0.010) *
                    reducedMotion;
                float edgeImpulse =
                    (fineDetail - 0.5) * high * 0.010 *
                    reducedMotion;
                float distanceToCenter = length(uv);
                float signedDistance =
                    distanceToCenter - radius - turbulence - edgeImpulse;
                float coreMask = 1.0 - smoothstep(-0.006, 0.016, signedDistance);

                float inner = clamp(1.0 - distanceToCenter / radius, 0.0, 1.0);
                float innerLight = pow(inner, 1.55) *
                    (0.32 + shapedFog * 0.78 + energy * 0.38 +
                     peakPocket * 0.42);
                float edgeLight = exp(-abs(signedDistance) * 76.0) *
                    (0.20 + high * 0.92 + fineDetail * high * 0.42 +
                     clipping * 0.5);
                float mist =
                    smoothstep(0.26, 0.82, shapedFog) * coreMask;

                float3 baseA = accentA.rgb;
                float3 baseB = accentB.rgb;
                float3 color = mix(
                    baseA * 0.48,
                    baseB * 0.86,
                    clamp(
                        shapedFog * 0.88 + uv.y * 0.28 + 0.06,
                        0.0,
                        1.0));
                color += float3(0.78, 0.82, 1.0) * innerLight;
                color += mix(baseB, float3(0.96), high * 0.48) * edgeLight;
                color += mix(baseA, float3(0.96), 0.56) *
                    (peakPocket * 0.56 + peakRing * 0.24) * coreMask;
                color += float3(0.78, 0.88, 1.0) *
                    max(fineDetail - 0.58, 0.0) * high *
                    (0.18 + voiceDrive * 0.34) * coreMask;
                color *=
                    0.58 + mist * 0.76 + voiceDrive * 0.28 + energy * 0.12;
                color = mix(
                    color,
                    errorColor.rgb * (0.34 + inner * 0.08),
                    errorPulse * 0.24);
                float successPresence =
                    1.0 - clamp(abs(mode - 4.0), 0.0, 1.0);
                color += baseB * successPresence *
                    (0.11 + successPulse * 0.18) * coreMask;

                float haloRadius = radius * haloScale;
                float halo = exp(
                    -pow(distanceToCenter / max(haloRadius, 0.001), 3.2) * 3.4);
                halo *= haloOpacity * (0.52 + energy * 0.34);
                halo *= 1.0 - smoothstep(0.46, 0.50, distanceToCenter);
                float successSignal =
                    max(successPulse, successPresence * 0.32);
                float successWave = exp(
                    -pow(
                        (distanceToCenter -
                         radius * (1.03 + successSignal * 0.30)) * 38.0,
                        2.0)) * successSignal;
                float successBloom = exp(
                    -pow(
                        distanceToCenter /
                        max(radius * (1.34 + successSignal * 0.12), 0.001),
                        2.0) * 2.8) * successSignal;
                float errorRing = exp(
                    -pow((distanceToCenter - radius * 0.96) * 52.0, 2.0)) *
                    errorPulse;
                float alpha = clamp(
                    coreMask * (0.80 + inner * 0.2) +
                    halo * 0.44 +
                    successWave * 0.54 +
                    successBloom * 0.24 +
                    errorRing * 0.30,
                    0.0,
                    1.0);
                color += baseA * halo * 0.34;
                color += baseB * successWave * 0.88;
                color += mix(baseA, baseB, 0.64) * successBloom * 0.54;
                color += errorColor.rgb * errorRing * 0.78;
                color += errorColor.rgb * clipping * edgeLight * 0.28;
                return half4(color * alpha, alpha);
            }
            """;
    }
}
