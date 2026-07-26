public sealed class PcmFrameNormalizer
{
    public const int DefaultFrameDurationMilliseconds = 20;

    private readonly AudioFormat _format;
    private readonly byte[] _carry;
    private int _carryLength;

    public PcmFrameNormalizer(
        AudioFormat format,
        int frameDurationMilliseconds = DefaultFrameDurationMilliseconds)
    {
        if (frameDurationMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameDurationMilliseconds));
        if (format.BlockAlign <= 0)
            throw new ArgumentException("The PCM format must have a positive block alignment.", nameof(format));

        long durationBytes = checked(
            (long)format.BytesPerSecond * frameDurationMilliseconds);
        if (durationBytes % 1000 != 0)
        {
            throw new ArgumentException(
                "The normalized frame duration must contain a whole number of PCM bytes.",
                nameof(frameDurationMilliseconds));
        }
        long bytes = durationBytes / 1000;
        if (bytes <= 0 || bytes > int.MaxValue || bytes % format.BlockAlign != 0)
        {
            throw new ArgumentException(
                "The normalized frame duration must contain a whole number of PCM samples.",
                nameof(frameDurationMilliseconds));
        }

        _format = format;
        FrameDuration = TimeSpan.FromMilliseconds(frameDurationMilliseconds);
        FrameByteCount = (int)bytes;
        _carry = new byte[FrameByteCount];
    }

    public AudioFormat Format => _format;
    public int FrameByteCount { get; }
    public TimeSpan FrameDuration { get; }
    public int CarryByteCount => _carryLength;
    public int CarryCapacity => _carry.Length;

    public IEnumerable<AudioFrame> Normalize(AudioFrame source)
    {
        if (source.Format != _format)
            throw new InvalidDataException("The PCM format changed during realtime audio capture.");
        if (source.Pcm.Length % _format.BlockAlign != 0)
        {
            throw new InvalidDataException(
                $"A realtime PCM frame contained {source.Pcm.Length} bytes, which is not aligned to the {_format.BlockAlign}-byte PCM block size.");
        }
        if (source.Pcm.IsEmpty)
            yield break;

        ReadOnlyMemory<byte> remaining = source.Pcm;
        if (_carryLength > 0)
        {
            int copyLength = Math.Min(FrameByteCount - _carryLength, remaining.Length);
            remaining.Span[..copyLength].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += copyLength;
            remaining = remaining[copyLength..];
            if (_carryLength == FrameByteCount)
            {
                yield return new AudioFrame(_carry.ToArray(), _format);
                _carryLength = 0;
            }
        }

        while (remaining.Length >= FrameByteCount)
        {
            yield return new AudioFrame(remaining[..FrameByteCount].ToArray(), _format);
            remaining = remaining[FrameByteCount..];
        }

        if (!remaining.IsEmpty)
        {
            remaining.Span.CopyTo(_carry);
            _carryLength = remaining.Length;
        }
    }

    public AudioFrame? Complete()
    {
        if (_carryLength == 0)
            return null;

        byte[] final = _carry.AsSpan(0, _carryLength).ToArray();
        _carryLength = 0;
        return new AudioFrame(final, _format);
    }

    public int DiscardCarry()
    {
        int discarded = _carryLength;
        _carryLength = 0;
        return discarded;
    }
}
