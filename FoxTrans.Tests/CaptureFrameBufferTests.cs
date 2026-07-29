using Xunit;

public sealed class CaptureFrameBufferTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task CallbackFramesAreNormalizedAndOverflowStopsTheProducerOnce()
    {
        int overflows = 0;
        var buffer = new NormalizedCaptureFrameBuffer(
            Format,
            capacity: 2,
            onOverflow: () => Interlocked.Increment(ref overflows));
        var received = new List<AudioFrame>();

        Assert.False(buffer.TryWrite(new AudioFrame(new byte[3 * 640], Format)));
        Assert.Equal(1, Volatile.Read(ref overflows));
        Assert.False(buffer.TryWrite(new AudioFrame(new byte[640], Format)));
        Assert.Equal(1, Volatile.Read(ref overflows));

        await Assert.ThrowsAsync<AudioBufferOverflowException>(async () =>
        {
            await foreach (AudioFrame frame in buffer.ReadAllAsync(
                               TestContext.Current.CancellationToken))
                received.Add(frame);
        });

        Assert.Equal(2, received.Count);
        Assert.All(received, frame => Assert.Equal(640, frame.Pcm.Length));
    }

    [Fact]
    public async Task WaitingConsumerCanBeCancelledWithoutAProducer()
    {
        var buffer = new NormalizedCaptureFrameBuffer(Format, 2, static () => { });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
#pragma warning disable xUnit1051 // The test intentionally cancels this linked token itself.
        await using IAsyncEnumerator<AudioFrame> reader = buffer
            .ReadAllAsync(cancellation.Token)
            .GetAsyncEnumerator();
#pragma warning restore xUnit1051

        Task<bool> read = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
