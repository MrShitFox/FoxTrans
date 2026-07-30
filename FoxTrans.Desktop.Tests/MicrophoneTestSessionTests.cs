using FoxTrans.Desktop.Services;
using System.Runtime.CompilerServices;
using Xunit;

public sealed class MicrophoneTestSessionTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task StopCancelsAndDisposesTheCaptureExactlyOnce()
    {
        var capture = new TrackingCapture();
        var factory = new TrackingFactory(capture);
        await using var session = new MicrophoneTestSession(factory);

        await session.StartAsync(new(4, "Test input", Format), TestContext.Current.CancellationToken);
        await capture.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(session.IsRunning);

        await session.StopAsync();
        await session.StopAsync();

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, capture.DisposeCount);
        Assert.False(session.IsRunning);
    }

    private sealed class TrackingFactory(TrackingCapture capture) : IAudioCaptureFactory
    {
        public int CreateCount { get; private set; }

        public IAudioCapture Create(ResolvedAudioInput input)
        {
            CreateCount++;
            return capture;
        }
    }

    private sealed class TrackingCapture : IAudioCapture
    {
        private int _disposed;

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount => Volatile.Read(ref _disposed);
        public AudioFormat Format => MicrophoneTestSessionTests.Format;

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            yield return new AudioFrame(new byte[640], Format);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);
    }
}
