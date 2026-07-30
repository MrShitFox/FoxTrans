using System.Runtime.CompilerServices;
using Xunit;

public sealed class PcmChunkAggregatorTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task FourTwentyMillisecondFramesBecomeOneChunk()
    {
        AudioFrame[] frames = Enumerable.Range(0, 4)
            .Select(index => new AudioFrame(
                Enumerable.Repeat((byte)index, 640).ToArray(),
                Format))
            .ToArray();
        ReadOnlyMemory<byte>[] chunks = await ReadAsync(Frames(frames));
        Assert.Single(chunks);
        Assert.Equal(2560, chunks[0].Length);
        Assert.Equal([0, 1, 2, 3], Enumerable.Range(0, 4)
            .Select(index => (int)chunks[0].Span[index * 640])
            .ToArray());
    }

    [Fact]
    public async Task ArbitraryBoundariesPreserveEveryByteInOrder()
    {
        byte[] expected = Enumerable.Range(0, 6002).Select(index => (byte)index).ToArray();
        AudioFrame[] frames =
        [
            new(expected.AsMemory(0, 13), Format),
            new(expected.AsMemory(13, 2548), Format),
            new(expected.AsMemory(2561, 3441), Format)
        ];
        ReadOnlyMemory<byte>[] chunks = await ReadAsync(Frames(frames));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 2560));
        Assert.Equal(expected, chunks.SelectMany(chunk => chunk.ToArray()).ToArray());
    }

    [Fact]
    public async Task OddTotalByteCountFailsExplicitly()
    {
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            async () => await ReadAsync(Frames([new AudioFrame(new byte[3], Format)])));
        Assert.Equal("invalid_audio_stream", exception.Code);
    }

    [Fact]
    public async Task EmptySourceProducesNoChunks()
    {
        Assert.Empty(await ReadAsync(Frames([])));
    }

    [Fact]
    public async Task SilenceIsPreserved()
    {
        ReadOnlyMemory<byte>[] chunks = await ReadAsync(
            Frames([new AudioFrame(new byte[5120], Format)]));
        Assert.Equal(2, chunks.Length);
        Assert.All(chunks, chunk => Assert.All(chunk.ToArray(), value => Assert.Equal(0, value)));
    }

    [Fact]
    public async Task CancellationDoesNotYieldAnEmptyOrPartialChunk()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<ReadOnlyMemory<byte>> enumerator =
            PcmChunkAggregator.AggregateAsync(BlockingFrames(cancellation.Token), cancellation.Token)
                .GetAsyncEnumerator(cancellation.Token);
        Task<bool> move = enumerator.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
    }

    private static async Task<ReadOnlyMemory<byte>[]> ReadAsync(
        IAsyncEnumerable<AudioFrame> frames)
    {
        var chunks = new List<ReadOnlyMemory<byte>>();
        await foreach (ReadOnlyMemory<byte> chunk in PcmChunkAggregator.AggregateAsync(
                           frames,
                           TestContext.Current.CancellationToken))
            chunks.Add(chunk.ToArray());
        return chunks.ToArray();
    }

    private static async IAsyncEnumerable<AudioFrame> Frames(
        IReadOnlyList<AudioFrame> frames)
    {
        foreach (AudioFrame frame in frames)
        {
            yield return frame;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AudioFrame> BlockingFrames(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return new AudioFrame(new byte[640], Format);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
