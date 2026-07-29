using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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

public sealed class NativeAudioCaptureFactory : IAudioCaptureFactory
{
    public IAudioCapture Create(ResolvedAudioInput input) =>
        new NativeAudioMicrophoneSource(input);
}

public sealed unsafe class NativeAudioMicrophoneSource : IAudioCapture
{
    private const int ChannelCapacity = 250;

    private readonly object _captureGate = new();
    private readonly NormalizedCaptureFrameBuffer _frames;
    private readonly ResolvedAudioInput _input;
    private int _started;
    private int _disposed;
    private int _stopped;
    private long _capturedFrameCount;
    private int _minimumCallbackBytes = int.MaxValue;
    private int _maximumCallbackBytes;
    private IntPtr _capture;
    private GCHandle _callbackHandle;

    public NativeAudioMicrophoneSource(ResolvedAudioInput input)
    {
        Format = input.Format;
        _input = input;
        _frames = new(Format, ChannelCapacity, CompleteWithOverflow);
    }

    public AudioFormat Format { get; }
    public int DeviceNumber => _input.DeviceNumber;
    public long CapturedFrameCount => Interlocked.Read(ref _capturedFrameCount);
    public int MinimumCallbackBytes =>
        Volatile.Read(ref _minimumCallbackBytes) == int.MaxValue
            ? 0
            : Volatile.Read(ref _minimumCallbackBytes);
    public int MaximumCallbackBytes => Volatile.Read(ref _maximumCallbackBytes);

    public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The microphone source can only be read once.");
        }

        using CancellationTokenRegistration registration =
            cancellationToken.Register(static state => ((NativeAudioMicrophoneSource)state!).Stop(), this);

        try
        {
            StartCapture(cancellationToken);
        }
        catch (AudioDeviceSelectionException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AudioDeviceSelectionException(
                $"Microphone device {_input.DeviceNumber} could not be opened at " +
                $"{Format.SampleRate} Hz, {Format.BitsPerSample}-bit, {Format.Channels} channel(s): " +
                exception.Message);
        }

        await foreach (AudioFrame frame in _frames.ReadAllAsync(cancellationToken))
        {
            yield return frame;
        }
    }

    private void StartCapture(CancellationToken cancellationToken)
    {
        lock (_captureGate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _stopped) != 0)
                throw new OperationCanceledException(cancellationToken);
            _callbackHandle = GCHandle.Alloc(this);
            byte[]? nativeId = string.IsNullOrWhiteSpace(_input.NativeId)
                ? null
                : Convert.FromBase64String(_input.NativeId);
            int result = NativeAudioInterop.CreateCapture(
                nativeId,
                &OnNativeFrame,
                GCHandle.ToIntPtr(_callbackHandle),
                out _capture);
            if (result != 0)
            {
                _callbackHandle.Free();
                NativeAudioInterop.ThrowOnError(
                    result,
                    "The microphone device could not be opened");
            }

            result = NativeAudioInterop.AudioCaptureStart(_capture);
            if (result != 0)
            {
                DestroyCaptureLocked();
                NativeAudioInterop.ThrowOnError(
                    result,
                    "The microphone device could not be started");
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeFrame(
        IntPtr userData,
        IntPtr pcm,
        uint frameCount)
    {
        if (userData == IntPtr.Zero || pcm == IntPtr.Zero)
            return;
        if (GCHandle.FromIntPtr(userData).Target is NativeAudioMicrophoneSource source)
            source.ReceiveFrame(pcm, frameCount);
    }

    private void ReceiveFrame(IntPtr pcm, uint frameCount)
    {
        if (Volatile.Read(ref _stopped) != 0 || frameCount == 0)
            return;

        int byteCount;
        try
        {
            byteCount = checked((int)frameCount * Format.BlockAlign);
        }
        catch (OverflowException)
        {
            CompleteWithOverflow();
            return;
        }

        Interlocked.Increment(ref _capturedFrameCount);
        RecordMinimum(ref _minimumCallbackBytes, byteCount);
        RecordMaximum(ref _maximumCallbackBytes, byteCount);
        byte[] buffer = new byte[byteCount];
        Marshal.Copy(pcm, buffer, 0, byteCount);
        _ = _frames.TryWrite(new AudioFrame(buffer, Format));
    }

    private void CompleteWithOverflow()
    {
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => ((NativeAudioMicrophoneSource)state!).Stop(),
            this);
    }

    private static void RecordMinimum(ref int target, int value)
    {
        int current;
        while (value < (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                break;
        }
    }

    private static void RecordMaximum(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
        {
            if (Interlocked.CompareExchange(ref target, value, current) == current)
                break;
        }
    }

    private void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        lock (_captureGate)
        {
            DestroyCaptureLocked();
        }
        _frames.Complete();
    }

    private void DestroyCaptureLocked()
    {
        IntPtr capture = Interlocked.Exchange(ref _capture, IntPtr.Zero);
        if (capture != IntPtr.Zero)
        {
            _ = NativeAudioInterop.AudioCaptureStop(capture);
            NativeAudioInterop.AudioCaptureDestroy(capture);
        }
        if (_callbackHandle.IsAllocated)
            _callbackHandle.Free();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
    }
}
