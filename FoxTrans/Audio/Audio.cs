using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.Wave;

public readonly record struct AudioFormat(int SampleRate, short BitsPerSample, short Channels)
{
    public short BlockAlign => checked((short)(Channels * BitsPerSample / 8));
    public int BytesPerSecond => checked(SampleRate * BlockAlign);

    public TimeSpan DurationOf(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        return TimeSpan.FromSeconds((double)byteCount / BytesPerSecond);
    }
}

public sealed record AudioFrame(ReadOnlyMemory<byte> Pcm, AudioFormat Format);

public sealed record AudioSegment(ReadOnlyMemory<byte> Pcm, AudioFormat Format)
{
    public TimeSpan Duration => Format.DurationOf(Pcm.Length);
}

public interface IAudioSource
{
    AudioFormat Format { get; }

    IAsyncEnumerable<AudioFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

public sealed class AudioBufferOverflowException(int capacity)
    : Exception($"The microphone audio buffer reached its capacity of {capacity} frames.");

public sealed class NAudioMicrophoneSource : IAudioSource, IDisposable
{
    private const int BufferMilliseconds = 20;
    private const int ChannelCapacity = 250;

    private readonly WaveInEvent _waveIn;
    private readonly Channel<AudioFrame> _frames;
    private int _started;
    private int _disposed;

    public NAudioMicrophoneSource(ResolvedAudioInput input)
    {
        Format = input.Format;
        _frames = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });

        _waveIn = new WaveInEvent
        {
            DeviceNumber = input.DeviceNumber,
            WaveFormat = new WaveFormat(
                input.Format.SampleRate,
                input.Format.BitsPerSample,
                input.Format.Channels),
            BufferMilliseconds = BufferMilliseconds
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;
    }

    public AudioFormat Format { get; }
    public int DeviceNumber => _waveIn.DeviceNumber;

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The microphone source can only be read once.");
        }

        using CancellationTokenRegistration registration =
            cancellationToken.Register(static state => ((NAudioMicrophoneSource)state!).Stop(), this);

        try
        {
            _waveIn.StartRecording();
        }
        catch (Exception exception)
        {
            throw new AudioDeviceSelectionException(
                $"Microphone device {_waveIn.DeviceNumber} could not be opened at " +
                $"{Format.SampleRate} Hz, {Format.BitsPerSample}-bit, {Format.Channels} channel(s): " +
                exception.Message);
        }

        await foreach (AudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
        {
            yield return frame;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        byte[] pcm = new byte[eventArgs.BytesRecorded];
        Buffer.BlockCopy(eventArgs.Buffer, 0, pcm, 0, eventArgs.BytesRecorded);

        if (!_frames.Writer.TryWrite(new AudioFrame(pcm, Format)))
        {
            _frames.Writer.TryComplete(new AudioBufferOverflowException(ChannelCapacity));
            Stop();
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        _frames.Writer.TryComplete(eventArgs.Exception);
    }

    private void Stop()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _waveIn.StopRecording();
            }
            catch (InvalidOperationException)
            {
                _frames.Writer.TryComplete();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _waveIn.StopRecording();
        }
        catch (InvalidOperationException)
        {
            // The device was already stopped.
        }

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _frames.Writer.TryComplete();
    }
}
