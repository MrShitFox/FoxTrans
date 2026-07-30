using System.Buffers.Binary;

public sealed record AudioVisualFrame(
    float Rms,
    float Peak,
    bool IsClipping,
    bool IsSpeechActive,
    ReadOnlyMemory<float> Spectrum,
    long Sequence,
    DateTimeOffset ObservedAt,
    bool IsSupported = true);

public interface IAudioVisualSink
{
    void Publish(AudioVisualFrame frame);
}

public sealed class LatestAudioVisualFrame : IAudioVisualSink
{
    private AudioVisualFrame? _latest;
    private long _published;

    public AudioVisualFrame? Latest => Volatile.Read(ref _latest);
    public long PublishedCount => Interlocked.Read(ref _published);

    public void Publish(AudioVisualFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Interlocked.Exchange(ref _latest, frame);
        Interlocked.Increment(ref _published);
    }
}

public sealed class Pcm16AudioFeatureExtractor
{
    public const int SpectrumBandCount = 12;
    public const int WindowSampleCount = 512;
    public static readonly TimeSpan DefaultPublishInterval =
        TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 15);

    private static readonly float[] BandFrequencies =
        [100, 180, 300, 480, 720, 1050, 1500, 2100, 2900, 3900, 5200, 6800];

    private readonly AudioFormat _format;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly TimeSpan _publishInterval;
    private readonly float[] _sampleWindow = new float[WindowSampleCount];
    private readonly float[] _windowWeights = new float[WindowSampleCount];
    private readonly float[,] _cosine =
        new float[SpectrumBandCount, WindowSampleCount];
    private readonly float[,] _sine =
        new float[SpectrumBandCount, WindowSampleCount];
    private int _writeIndex;
    private int _availableSamples;
    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;
    private long _sequence;

    public Pcm16AudioFeatureExtractor(
        AudioFormat format,
        Func<DateTimeOffset>? getUtcNow = null,
        TimeSpan? publishInterval = null)
    {
        _format = format;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _publishInterval = publishInterval ?? DefaultPublishInterval;
        if (_publishInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(publishInterval));

        for (int sample = 0; sample < WindowSampleCount; sample++)
        {
            _windowWeights[sample] =
                0.5f - 0.5f * MathF.Cos(
                    2 * MathF.PI * sample / (WindowSampleCount - 1));
        }
        for (int band = 0; band < SpectrumBandCount; band++)
        {
            float frequency = Math.Min(
                BandFrequencies[band],
                Math.Max(1, format.SampleRate / 2f - 1));
            for (int sample = 0; sample < WindowSampleCount; sample++)
            {
                float phase =
                    2 * MathF.PI * frequency * sample / format.SampleRate;
                _cosine[band, sample] = MathF.Cos(phase);
                _sine[band, sample] = MathF.Sin(phase);
            }
        }
    }

    public bool IsSupported =>
        _format.BitsPerSample == 16 &&
        _format.Channels == 1 &&
        _format.SampleRate > 0;

    public int RetainedSignalSampleCount => _sampleWindow.Length;
    public int FixedWorkingFloatCount =>
        _sampleWindow.Length +
        _windowWeights.Length +
        _cosine.Length +
        _sine.Length;

    public bool TryProcess(
        AudioFrame frame,
        out AudioVisualFrame visualFrame)
    {
        if (frame.Format != _format)
            throw new InvalidDataException(
                "The PCM format changed while extracting visual audio features.");

        DateTimeOffset now = _getUtcNow();
        if (!IsSupported || frame.Pcm.Length % 2 != 0)
        {
            visualFrame = new(
                0,
                0,
                false,
                false,
                ReadOnlyMemory<float>.Empty,
                Interlocked.Increment(ref _sequence),
                now,
                false);
            _lastPublished = now;
            return true;
        }

        ReadOnlySpan<byte> pcm = frame.Pcm.Span;
        double sumSquares = 0;
        float peak = 0;
        int sampleCount = pcm.Length / 2;
        for (int index = 0; index < pcm.Length; index += 2)
        {
            short value = BinaryPrimitives.ReadInt16LittleEndian(
                pcm.Slice(index, 2));
            float normalized = value / 32768f;
            _sampleWindow[_writeIndex] = normalized;
            _writeIndex = (_writeIndex + 1) % WindowSampleCount;
            _availableSamples = Math.Min(
                WindowSampleCount,
                _availableSamples + 1);
            sumSquares += normalized * normalized;
            peak = Math.Max(peak, Math.Abs(normalized));
        }

        if (_lastPublished != DateTimeOffset.MinValue &&
            now - _lastPublished < _publishInterval)
        {
            visualFrame = null!;
            return false;
        }

        float rms = sampleCount == 0
            ? 0
            : (float)Math.Sqrt(sumSquares / sampleCount);
        float[] spectrum = CalculateSpectrum();
        visualFrame = new(
            Math.Clamp(rms, 0, 1),
            Math.Clamp(peak, 0, 1),
            peak >= 0.999f,
            rms >= 0.015f || peak >= 0.06f,
            spectrum,
            Interlocked.Increment(ref _sequence),
            now);
        _lastPublished = now;
        return true;
    }

    private float[] CalculateSpectrum()
    {
        var result = new float[SpectrumBandCount];
        if (_availableSamples == 0)
            return result;

        int missing = WindowSampleCount - _availableSamples;
        int oldest = _availableSamples == WindowSampleCount ? _writeIndex : 0;
        for (int band = 0; band < SpectrumBandCount; band++)
        {
            double real = 0;
            double imaginary = 0;
            for (int sample = missing; sample < WindowSampleCount; sample++)
            {
                int source = (oldest + sample - missing) % WindowSampleCount;
                float value = _sampleWindow[source] * _windowWeights[sample];
                real += value * _cosine[band, sample];
                imaginary -= value * _sine[band, sample];
            }
            double magnitude =
                4.0 * Math.Sqrt(real * real + imaginary * imaginary) /
                WindowSampleCount;
            result[band] = (float)Math.Clamp(magnitude, 0, 1);
        }
        return result;
    }
}
