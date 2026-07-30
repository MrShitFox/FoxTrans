using System.Buffers.Binary;
using System.Runtime.CompilerServices;

public sealed class MeteredAudioSource : IAudioSource
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(100);
    private readonly IAudioSource _inner;
    private readonly IAppReporter _reporter;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly IAudioVisualSink? _visualSink;
    private readonly Pcm16AudioFeatureExtractor? _visualExtractor;

    public MeteredAudioSource(
        IAudioSource inner,
        IAppReporter reporter,
        Func<DateTimeOffset>? getUtcNow = null,
        IAudioVisualSink? visualSink = null)
    {
        _inner = inner;
        _reporter = reporter;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _visualSink = visualSink;
        _visualExtractor = visualSink is null
            ? null
            : new Pcm16AudioFeatureExtractor(
                inner.Format,
                _getUtcNow);
    }

    public AudioFormat Format => _inner.Format;

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        DateTimeOffset lastPublished = DateTimeOffset.MinValue;
        long frames = 0;
        double recentPeak = 0;
        await foreach (AudioFrame frame in _inner.ReadFramesAsync(cancellationToken)
            .WithCancellation(cancellationToken))
        {
            frames++;
            if (_visualExtractor is not null &&
                _visualExtractor.TryProcess(frame, out AudioVisualFrame visual))
            {
                _visualSink!.Publish(visual);
            }
            bool supported = frame.Format.BitsPerSample == 16 &&
                frame.Format.Channels > 0 &&
                frame.Pcm.Length % 2 == 0;
            double sumSquares = 0;
            int samples = 0;
            double framePeak = 0;
            if (supported)
            {
                ReadOnlySpan<byte> pcm = frame.Pcm.Span;
                for (int index = 0; index < pcm.Length; index += 2)
                {
                    short value = BinaryPrimitives.ReadInt16LittleEndian(pcm[index..]);
                    double normalized = value / 32768d;
                    sumSquares += normalized * normalized;
                    framePeak = Math.Max(framePeak, Math.Abs(normalized));
                    samples++;
                }
                recentPeak = Math.Max(recentPeak, framePeak);
            }

            DateTimeOffset now = _getUtcNow();
            if (lastPublished == DateTimeOffset.MinValue ||
                now - lastPublished >= PublishInterval)
            {
                double rms = supported && samples > 0
                    ? Math.Sqrt(sumSquares / samples)
                    : 0;
                _reporter.Report(AppEvent.RuntimeTelemetry(new AudioLevelTelemetry(
                    ToDb(rms),
                    ToDb(recentPeak),
                    recentPeak >= 0.999,
                    supported,
                    frames)));
                lastPublished = now;
                recentPeak = 0;
            }
            yield return frame;
        }
    }

    private static double ToDb(double amplitude) =>
        amplitude <= 0 ? -96 : Math.Max(-96, 20 * Math.Log10(amplitude));
}
