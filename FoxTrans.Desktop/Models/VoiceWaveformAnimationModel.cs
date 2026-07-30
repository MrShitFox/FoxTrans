namespace FoxTrans.Desktop.Models;

public readonly record struct VoiceWaveformRenderState(
    float Opacity,
    float BorderIntensity,
    float VisualEnergy,
    float AdaptiveGain,
    float NoiseFloor,
    float SweepProgress,
    float SuccessPulse,
    float ErrorPulse,
    float StateValue,
    bool IsClipping,
    ReadOnlyMemory<float> BarHeights);

public sealed class VoiceWaveformAnimationModel
{
    public const int BarCount = Pcm16AudioFeatureExtractor.SpectrumBandCount;
    private const double AttackSeconds = 0.045;
    private const double ReleaseSeconds = 0.160;
    private const double SuccessPulseSeconds = 0.45;
    private const double ErrorPulseSeconds = 0.18;
    private const double StoppingFadeSeconds = 0.25;

    private readonly float[] _bars = new float[BarCount];
    private DateTimeOffset? _lastUpdate;
    private DateTimeOffset? _modeEnteredAt;
    private VoiceVisualizationMode _lastMode = VoiceVisualizationMode.Idle;
    private float _energy;
    private float _noiseFloor = 0.006f;
    private float _speechReference = 0.055f;
    private bool _hasCalibration;

    public VoiceWaveformRenderState Update(
        VoiceVisualizationMode mode,
        AudioVisualFrame? frame,
        DateTimeOffset now,
        bool reducedMotion)
    {
        bool firstUpdate = _lastUpdate is null;
        double elapsed = _lastUpdate is { } previous && now >= previous
            ? Math.Min(0.25, (now - previous).TotalSeconds)
            : 1.0 / 30;
        _lastUpdate = now;

        if (firstUpdate || mode != _lastMode)
        {
            _lastMode = mode;
            _modeEnteredAt = now;
        }

        bool hasAudio = frame?.IsSupported == true;
        float rms = hasAudio ? Math.Clamp(frame!.Rms, 0, 1) : 0;
        float peak = hasAudio ? Math.Clamp(frame!.Peak, 0, 1) : 0;
        float loudness = Math.Max(rms, peak * 0.28f);
        bool reportedSpeech = hasAudio && frame!.IsSpeechActive;
        float targetEnergy = CalibrateEnergy(
            loudness,
            reportedSpeech,
            hasAudio,
            elapsed);
        _energy = firstUpdate || reducedMotion
            ? targetEnergy
            : Smooth(
                _energy,
                targetEnergy,
                elapsed,
                targetEnergy > _energy ? AttackSeconds : ReleaseSeconds);

        ReadOnlySpan<float> spectrum = hasAudio
            ? frame!.Spectrum.Span
            : ReadOnlySpan<float>.Empty;
        float spectrumPeak = 0;
        foreach (float value in spectrum)
            spectrumPeak = Math.Max(spectrumPeak, Math.Clamp(value, 0, 1));
        float spectrumScale = Math.Max(spectrumPeak, 0.0005f);
        for (int index = 0; index < _bars.Length; index++)
        {
            float band = index < spectrum.Length &&
                spectrumPeak > 0.00001f
                ? Math.Clamp(spectrum[index] / spectrumScale, 0, 1)
                : 0;
            float target = TargetHeight(mode, index, _energy, band);
            if (mode == VoiceVisualizationMode.Stopping)
            {
                float age = ModeAge(now);
                target *= Math.Clamp(
                    1 - age / (float)StoppingFadeSeconds,
                    0,
                    1);
            }

            _bars[index] = firstUpdate || reducedMotion
                ? target
                : Smooth(
                    _bars[index],
                    target,
                    elapsed,
                    target > _bars[index]
                        ? AttackSeconds
                        : ReleaseSeconds);
        }

        float modeAge = ModeAge(now);
        float success = mode == VoiceVisualizationMode.Success &&
            !reducedMotion
                ? BoundedPulse(modeAge, (float)SuccessPulseSeconds)
                : 0;
        float error = mode == VoiceVisualizationMode.Error &&
            !reducedMotion
                ? BoundedPulse(modeAge, (float)ErrorPulseSeconds)
                : 0;
        float sweep = mode == VoiceVisualizationMode.Processing &&
            !reducedMotion
                ? (modeAge % 1.2f) / 1.2f
                : -1;
        float opacity = mode == VoiceVisualizationMode.Stopping
            ? Math.Clamp(
                1 - modeAge / (float)StoppingFadeSeconds,
                0.36f,
                1)
            : 1;
        float border = mode switch
        {
            VoiceVisualizationMode.Listening =>
                0.30f + _energy * 0.18f,
            VoiceVisualizationMode.Speech =>
                0.46f + _energy * 0.38f,
            VoiceVisualizationMode.Processing => 0.58f,
            VoiceVisualizationMode.Success => 0.62f + success * 0.30f,
            VoiceVisualizationMode.Error => 0.70f + error * 0.25f,
            VoiceVisualizationMode.Stopping => 0.24f,
            _ => 0.22f
        };

        return new(
            opacity,
            Math.Clamp(border, 0, 1),
            _energy,
            Math.Clamp(
                0.055f /
                Math.Max(_speechReference - _noiseFloor, 0.004f),
                0.18f,
                12),
            _noiseFloor,
            sweep,
            success,
            error,
            (float)mode,
            frame?.IsClipping == true,
            _bars);
    }

    public int RetainedBandCount => _bars.Length;
    public bool RetainsRawAudio => false;
    public static double AttackTimeSeconds => AttackSeconds;
    public static double ReleaseTimeSeconds => ReleaseSeconds;
    public static TimeSpan SuccessPulseDuration =>
        TimeSpan.FromSeconds(SuccessPulseSeconds);
    public static TimeSpan ErrorPulseDuration =>
        TimeSpan.FromSeconds(ErrorPulseSeconds);
    public static TimeSpan StoppingFadeDuration =>
        TimeSpan.FromSeconds(StoppingFadeSeconds);

    private float CalibrateEnergy(
        float loudness,
        bool reportedSpeech,
        bool hasAudio,
        double elapsed)
    {
        if (!hasAudio)
            return 0;

        if (!_hasCalibration)
        {
            _noiseFloor = reportedSpeech
                ? Math.Clamp(loudness * 0.12f, 0.0015f, 0.008f)
                : Math.Clamp(loudness, 0.0015f, 0.025f);
            _speechReference = reportedSpeech
                ? Math.Max(loudness, _noiseFloor + 0.006f)
                : Math.Max(0.055f, _noiseFloor + 0.025f);
            _hasCalibration = true;
        }
        else
        {
            if (!reportedSpeech)
            {
                float floorTarget = Math.Clamp(loudness, 0.0015f, 0.080f);
                _noiseFloor = Smooth(
                    _noiseFloor,
                    floorTarget,
                    elapsed,
                    floorTarget < _noiseFloor ? 0.65 : 4.0);
            }

            float activityMargin = Math.Max(0.0035f, _noiseFloor * 0.60f);
            bool visuallyActive =
                reportedSpeech || loudness > _noiseFloor + activityMargin;
            if (visuallyActive)
            {
                float referenceTarget = Math.Clamp(
                    Math.Max(loudness, _noiseFloor + 0.006f),
                    0.008f,
                    0.90f);
                _speechReference = Smooth(
                    _speechReference,
                    referenceTarget,
                    elapsed,
                    referenceTarget > _speechReference ? 0.18 : 0.10);
            }
            else
            {
                float restingReference = Math.Max(
                    0.035f,
                    _noiseFloor * 3.5f);
                _speechReference = Smooth(
                    _speechReference,
                    restingReference,
                    elapsed,
                    5.0);
            }
        }

        float signal = Math.Max(0, loudness - _noiseFloor);
        float calibratedRange = Math.Max(
            _speechReference - _noiseFloor,
            0.004f);
        float normalized = Math.Clamp(signal / calibratedRange, 0, 1.25f);
        if (!reportedSpeech &&
            loudness <=
            _noiseFloor + Math.Max(0.0035f, _noiseFloor * 0.60f))
        {
            normalized *= 0.22f;
        }
        normalized = MathF.Pow(normalized, 0.72f);
        return Math.Clamp(normalized, 0, 1);
    }

    private float ModeAge(DateTimeOffset now) =>
        _modeEnteredAt is { } entered && now >= entered
            ? (float)(now - entered).TotalSeconds
            : 0;

    private static float TargetHeight(
        VoiceVisualizationMode mode,
        int index,
        float energy,
        float band)
    {
        float distance = Math.Abs(index - (BarCount - 1) / 2f) /
            ((BarCount - 1) / 2f);
        float envelope = 1 - distance * 0.40f;
        return mode switch
        {
            VoiceVisualizationMode.Listening =>
                Math.Clamp(
                    0.08f +
                    envelope * energy * (0.38f + band * 0.22f),
                    0.08f,
                    0.66f),
            VoiceVisualizationMode.Speech =>
                Math.Clamp(
                    0.10f +
                    envelope * energy * (0.72f + band * 0.38f),
                    0.10f,
                    1),
            VoiceVisualizationMode.Processing =>
                0.14f + envelope * 0.08f,
            VoiceVisualizationMode.Success =>
                0.11f + envelope * 0.07f,
            VoiceVisualizationMode.Error =>
                0.08f + envelope * 0.04f,
            VoiceVisualizationMode.Stopping =>
                Math.Clamp(
                    0.06f +
                    envelope * energy * (0.18f + band * 0.08f),
                    0.06f,
                    0.30f),
            _ => 0.055f + envelope * 0.025f
        };
    }

    private static float BoundedPulse(float seconds, float duration)
    {
        if (seconds <= 0 || seconds >= duration)
            return 0;
        float progress = Math.Clamp(seconds / duration, 0, 1);
        return MathF.Sin(progress * MathF.PI);
    }

    private static float Smooth(
        float current,
        float target,
        double elapsed,
        double timeConstant)
    {
        float factor = (float)(1 - Math.Exp(-elapsed / timeConstant));
        return current + (target - current) * factor;
    }
}
