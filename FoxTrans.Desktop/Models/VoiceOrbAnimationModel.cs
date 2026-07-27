namespace FoxTrans.Desktop.Models;

public sealed record VoiceOrbRenderState(
    float CoreScale,
    float HaloOpacity,
    float HaloScale,
    float Rotation,
    float Energy,
    bool IsClipping,
    ReadOnlyMemory<float> Contour);

public sealed class VoiceOrbAnimationModel
{
    public const int ContourPointCount = 32;

    private readonly float[] _bands =
        new float[Pcm16AudioFeatureExtractor.SpectrumBandCount];
    private readonly float[] _contour = new float[ContourPointCount];
    private DateTimeOffset? _lastUpdate;
    private float _rms;
    private float _peak;

    public VoiceOrbRenderState Update(
        VoiceVisualizationMode mode,
        AudioVisualFrame? frame,
        DateTimeOffset now,
        bool reducedMotion)
    {
        bool firstUpdate = _lastUpdate is null;
        double elapsed = _lastUpdate is { } previous && now >= previous
            ? Math.Min(0.25, (now - previous).TotalSeconds)
            : 1.0 / 60;
        _lastUpdate = now;

        float targetRms = frame?.IsSupported == true ? frame.Rms : 0;
        float targetPeak = frame?.IsSupported == true ? frame.Peak : 0;
        _rms = firstUpdate
            ? targetRms
            : Smooth(_rms, targetRms, elapsed, targetRms > _rms ? 18 : 6);
        _peak = firstUpdate
            ? targetPeak
            : Smooth(_peak, targetPeak, elapsed, targetPeak > _peak ? 28 : 8);

        ReadOnlySpan<float> spectrum = frame is null
            ? ReadOnlySpan<float>.Empty
            : frame.Spectrum.Span;
        for (int index = 0; index < _bands.Length; index++)
        {
            float target = index < spectrum.Length ? spectrum[index] : 0;
            _bands[index] = firstUpdate
                ? target
                : Smooth(
                    _bands[index],
                    target,
                    elapsed,
                    target > _bands[index] ? 14 : 5);
        }

        double seconds = now.ToUnixTimeMilliseconds() / 1000.0;
        float breath = reducedMotion
            ? 0
            : mode == VoiceVisualizationMode.Idle
                ? 0.018f * MathF.Sin((float)(seconds * 1.25))
                : 0.008f * MathF.Sin((float)(seconds * 2.0));
        float modeScale = mode switch
        {
            VoiceVisualizationMode.Idle => 0.94f,
            VoiceVisualizationMode.Listening => 1.0f,
            VoiceVisualizationMode.Speech => 1.035f,
            VoiceVisualizationMode.Processing => 1.02f,
            VoiceVisualizationMode.Success => 1.08f,
            VoiceVisualizationMode.Error => 0.91f,
            VoiceVisualizationMode.Stopping => 0.96f,
            _ => 1
        };
        float energy = Math.Clamp(_rms * 2.8f + _peak * 0.5f, 0, 1);
        float coreScale = modeScale + breath + energy * 0.18f;
        float haloOpacity = mode switch
        {
            VoiceVisualizationMode.Idle => 0.11f,
            VoiceVisualizationMode.Listening => 0.24f + energy * 0.18f,
            VoiceVisualizationMode.Speech => 0.38f + energy * 0.32f,
            VoiceVisualizationMode.Processing => 0.42f,
            VoiceVisualizationMode.Success => 0.62f,
            VoiceVisualizationMode.Error => 0.38f,
            VoiceVisualizationMode.Stopping => 0.18f,
            _ => 0.2f
        };
        float processingPulse =
            mode == VoiceVisualizationMode.Processing && !reducedMotion
                ? 0.045f * MathF.Sin((float)(seconds * 3.4))
                : 0;
        float haloScale = 1.14f + energy * 0.2f + processingPulse;
        float rotation = reducedMotion
            ? 0
            : (float)(seconds * (mode == VoiceVisualizationMode.Processing
                ? 0.36
                : 0.12));

        for (int point = 0; point < _contour.Length; point++)
        {
            float bandPosition =
                point * _bands.Length / (float)_contour.Length;
            int first = (int)bandPosition % _bands.Length;
            int second = (first + 1) % _bands.Length;
            float fraction = bandPosition - MathF.Floor(bandPosition);
            float spectral = _bands[first] +
                (_bands[second] - _bands[first]) * fraction;
            float organic = reducedMotion
                ? 0
                : MathF.Sin(point * 1.71f + rotation * 2.3f) *
                  (0.006f + energy * 0.012f);
            _contour[point] =
                1 + spectral * 0.13f + organic;
        }

        return new(
            coreScale,
            Math.Clamp(haloOpacity, 0, 0.8f),
            haloScale,
            rotation,
            energy,
            frame?.IsClipping == true,
            _contour);
    }

    public int RetainedBandCount => _bands.Length;
    public bool RetainsRawAudio => false;

    private static float Smooth(
        float current,
        float target,
        double elapsed,
        double speed)
    {
        float factor = (float)(1 - Math.Exp(-speed * elapsed));
        return current + (target - current) * factor;
    }
}
