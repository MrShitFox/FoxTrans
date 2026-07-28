namespace FoxTrans.Desktop.Models;

public sealed record VoiceOrbRenderState(
    float CoreScale,
    float HaloOpacity,
    float HaloScale,
    float Energy,
    float Rms,
    float Peak,
    float PeakImpulse,
    float LowEnergy,
    float MidEnergy,
    float HighEnergy,
    float SpeechActivity,
    float ProcessingIntensity,
    float SuccessPulse,
    float ErrorPulse,
    float StateValue,
    float ReducedMotionFactor,
    bool IsClipping,
    ReadOnlyMemory<float> SpectralBands);

public sealed class VoiceOrbAnimationModel
{
    private readonly float[] _bands =
        new float[Pcm16AudioFeatureExtractor.SpectrumBandCount];
    private DateTimeOffset? _lastUpdate;
    private DateTimeOffset? _modeEnteredAt;
    private VoiceVisualizationMode _lastMode = VoiceVisualizationMode.Idle;
    private float _rms;
    private float _peak;
    private float _peakImpulse;

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

        if (firstUpdate || mode != _lastMode)
        {
            _lastMode = mode;
            _modeEnteredAt = now;
        }

        float targetRms = frame?.IsSupported == true ? frame.Rms : 0;
        float targetPeak = frame?.IsSupported == true ? frame.Peak : 0;
        _rms = firstUpdate
            ? targetRms
            : Smooth(_rms, targetRms, elapsed, targetRms > _rms ? 20 : 5.5);
        _peak = firstUpdate
            ? targetPeak
            : Smooth(_peak, targetPeak, elapsed, targetPeak > _peak ? 30 : 8);
        float impulseTarget = Math.Max(0, targetPeak - _peak) * 2.2f;
        _peakImpulse = firstUpdate
            ? impulseTarget
            : Smooth(
                _peakImpulse,
                impulseTarget,
                elapsed,
                impulseTarget > _peakImpulse ? 34 : 7);

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
                    target > _bands[index] ? 16 : 5);
        }

        float low = Average(_bands.AsSpan(0, 4));
        float mid = Average(_bands.AsSpan(4, 4));
        float high = Average(_bands.AsSpan(8, 4));
        float energy = Math.Clamp(
            _rms * 2.4f +
            _peak * 0.28f +
            low * 0.22f +
            mid * 0.14f,
            0,
            1);
        float speech = frame?.IsSpeechActive == true ? 1 : 0;
        float processing =
            mode == VoiceVisualizationMode.Processing ? 1 : 0;
        float modeAge = _modeEnteredAt is { } entered && now >= entered
            ? (float)(now - entered).TotalSeconds
            : 0;
        float success = mode == VoiceVisualizationMode.Success
            ? DecayPulse(modeAge, 0.62f)
            : 0;
        float error = mode == VoiceVisualizationMode.Error
            ? 0.58f + 0.22f * MathF.Exp(-modeAge * 2.6f)
            : 0;
        float breath = reducedMotion
            ? 0
            : 0.012f * MathF.Sin(
                (float)(now.ToUnixTimeMilliseconds() / 1000d) *
                (mode == VoiceVisualizationMode.Idle ? 1.05f : 1.8f));
        float modeScale = mode switch
        {
            VoiceVisualizationMode.Idle => 0.92f,
            VoiceVisualizationMode.Listening => 0.97f,
            VoiceVisualizationMode.Speech => 1.0f,
            VoiceVisualizationMode.Processing => 0.985f,
            VoiceVisualizationMode.Success => 1.015f,
            VoiceVisualizationMode.Error => 0.88f,
            VoiceVisualizationMode.Stopping => 0.91f,
            _ => 0.95f
        };
        float coreScale = modeScale +
            breath +
            energy * (mode == VoiceVisualizationMode.Speech ? 0.22f : 0.09f) +
            low * 0.07f +
            success * 0.08f -
            error * 0.035f;
        float haloOpacity = mode switch
        {
            VoiceVisualizationMode.Idle => 0.10f,
            VoiceVisualizationMode.Listening => 0.18f + energy * 0.10f,
            VoiceVisualizationMode.Speech => 0.27f + energy * 0.34f,
            VoiceVisualizationMode.Processing => 0.34f,
            VoiceVisualizationMode.Success => 0.34f + success * 0.36f,
            VoiceVisualizationMode.Error => 0.28f + error * 0.18f,
            VoiceVisualizationMode.Stopping => 0.14f,
            _ => 0.12f
        };
        float haloScale =
            1.15f +
            energy * 0.18f +
            low * 0.08f +
            success * 0.24f;

        return new(
            Math.Clamp(coreScale, 0.76f, 1.38f),
            Math.Clamp(haloOpacity, 0.05f, 0.78f),
            Math.Clamp(haloScale, 1.04f, 1.72f),
            energy,
            _rms,
            _peak,
            _peakImpulse,
            low,
            mid,
            high,
            speech,
            processing,
            success,
            error,
            (float)mode,
            reducedMotion ? 0 : 1,
            frame?.IsClipping == true,
            _bands);
    }

    public int RetainedBandCount => _bands.Length;
    public bool RetainsRawAudio => false;

    private static float Average(ReadOnlySpan<float> values)
    {
        float total = 0;
        foreach (float value in values)
            total += value;
        return values.IsEmpty ? 0 : total / values.Length;
    }

    private static float DecayPulse(float seconds, float duration)
    {
        if (seconds >= duration)
            return 0;
        float progress = Math.Clamp(seconds / duration, 0, 1);
        return MathF.Sin(progress * MathF.PI) *
            MathF.Pow(1 - progress, 0.35f);
    }

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
