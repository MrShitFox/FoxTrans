using System.Threading.Channels;

internal sealed class NormalizedCaptureFrameBuffer
{
    private readonly Channel<AudioFrame> _frames;
    private readonly PcmFrameNormalizer _normalizer;
    private readonly Action _onOverflow;

    public NormalizedCaptureFrameBuffer(
        AudioFormat format,
        int capacity,
        Action onOverflow)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        Capacity = capacity;
        _normalizer = new PcmFrameNormalizer(format);
        _onOverflow = onOverflow ?? throw new ArgumentNullException(nameof(onOverflow));
        _frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public int Capacity { get; }

    public bool TryWrite(AudioFrame callbackFrame)
    {
        foreach (AudioFrame frame in _normalizer.Normalize(callbackFrame))
        {
            if (_frames.Writer.TryWrite(frame))
                continue;

            if (_frames.Writer.TryComplete(new AudioBufferOverflowException(Capacity)))
                _onOverflow();
            return false;
        }

        return true;
    }

    public IAsyncEnumerable<AudioFrame> ReadAllAsync(CancellationToken cancellationToken) =>
        _frames.Reader.ReadAllAsync(cancellationToken);

    public void Complete() => _frames.Writer.TryComplete();
}
