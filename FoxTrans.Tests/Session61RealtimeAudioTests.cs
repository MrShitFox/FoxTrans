using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Xunit;

public sealed class Session61PcmNormalizerTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public void TenTinyFramesBecomeOneTwentyMillisecondFrame()
    {
        var normalizer = new PcmFrameNormalizer(Format);
        AudioFrame[] output = Enumerable.Range(0, 10)
            .SelectMany(index => normalizer.Normalize(
                new AudioFrame(Enumerable.Repeat((byte)index, 64).ToArray(), Format)))
            .ToArray();

        AudioFrame frame = Assert.Single(output);
        Assert.Equal(640, frame.Pcm.Length);
        Assert.Equal(
            Enumerable.Range(0, 10).SelectMany(index => Enumerable.Repeat((byte)index, 64)),
            frame.Pcm.ToArray());
    }

    [Fact]
    public void LargeFrameBecomesFourOrderedFrames()
    {
        byte[] input = Enumerable.Range(0, 2560).Select(index => (byte)index).ToArray();
        var normalizer = new PcmFrameNormalizer(Format);

        AudioFrame[] output = normalizer.Normalize(new AudioFrame(input, Format)).ToArray();

        Assert.Equal(4, output.Length);
        Assert.All(output, frame => Assert.Equal(640, frame.Pcm.Length));
        Assert.Equal(input, output.SelectMany(frame => frame.Pcm.ToArray()).ToArray());
    }

    [Fact]
    public void MixedBoundariesPreserveEveryByteAndCarry()
    {
        byte[] input = Enumerable.Range(0, 1920).Select(index => (byte)(index * 17)).ToArray();
        int[] boundaries = [2, 638, 100, 8, 700, 472];
        var normalizer = new PcmFrameNormalizer(Format);
        var output = new List<AudioFrame>();
        int offset = 0;
        foreach (int length in boundaries)
        {
            output.AddRange(normalizer.Normalize(
                new AudioFrame(input.AsMemory(offset, length), Format)));
            offset += length;
        }

        Assert.Equal(3, output.Count);
        Assert.Equal(input, output.SelectMany(frame => frame.Pcm.ToArray()).ToArray());
        Assert.Equal(0, normalizer.CarryByteCount);
    }

    [Fact]
    public void EmptyFramesProduceNothingAndDoNotDisturbCarry()
    {
        var normalizer = new PcmFrameNormalizer(Format);
        Assert.Empty(normalizer.Normalize(new AudioFrame(new byte[100], Format)));
        Assert.Empty(normalizer.Normalize(new AudioFrame(Array.Empty<byte>(), Format)));
        Assert.Equal(100, normalizer.CarryByteCount);
        Assert.Single(normalizer.Normalize(new AudioFrame(new byte[540], Format)));
    }

    [Fact]
    public void OddPcmAndFormatChangesFailExplicitly()
    {
        var normalizer = new PcmFrameNormalizer(Format);
        Assert.Throws<InvalidDataException>(() =>
            normalizer.Normalize(new AudioFrame(new byte[3], Format)).ToArray());
        Assert.Throws<InvalidDataException>(() =>
            normalizer.Normalize(
                new AudioFrame(new byte[640], new AudioFormat(48000, 16, 1))).ToArray());
    }

    [Fact]
    public void CancellationCanDiscardPartialWithoutCorruptOutput()
    {
        var normalizer = new PcmFrameNormalizer(Format);
        Assert.Empty(normalizer.Normalize(new AudioFrame(new byte[638], Format)));
        Assert.Equal(638, normalizer.DiscardCarry());
        Assert.Equal(0, normalizer.CarryByteCount);
        Assert.Empty(normalizer.Normalize(new AudioFrame(Array.Empty<byte>(), Format)));
    }

    [Fact]
    public void CarryMemoryIsBoundedBelowOneNormalizedFrame()
    {
        var normalizer = new PcmFrameNormalizer(Format);
        for (int index = 0; index < 1000; index++)
            _ = normalizer.Normalize(new AudioFrame(new byte[2], Format)).ToArray();

        Assert.Equal(640, normalizer.CarryCapacity);
        Assert.InRange(normalizer.CarryByteCount, 0, 638);
    }

    [Fact]
    public void NormalizedFrameHasExactTwentyMillisecondDuration()
    {
        var normalizer = new PcmFrameNormalizer(Format);
        Assert.Equal(640, normalizer.FrameByteCount);
        Assert.Equal(TimeSpan.FromMilliseconds(20), normalizer.FrameDuration);
        Assert.Equal(
            TimeSpan.FromMilliseconds(20),
            Format.DurationOf(normalizer.FrameByteCount));
    }
}

public sealed class Session61RealtimePumpTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public void AttemptsHaveExplicitPreparedActiveAndEndedStates()
    {
        var source = new ControlledSource();
        var pump = Pump(source);
        PreparedRealtimeAudioAttempt first = pump.PrepareAttempt(1);
        Assert.Throws<InvalidOperationException>(() => pump.PrepareAttempt(2));

        var otherPump = Pump(new ControlledSource());
        PreparedRealtimeAudioAttempt other = otherPump.PrepareAttempt(99);
        Assert.Throws<InvalidOperationException>(() => pump.ActivateAttempt(other));

        pump.ActivateAttempt(first);
        Assert.Throws<InvalidOperationException>(() => pump.PrepareAttempt(2));
        pump.EndFailedAttempt(first, new IOException("failed"));
        PreparedRealtimeAudioAttempt second = pump.PrepareAttempt(2);
        Assert.Throws<InvalidOperationException>(() => pump.ActivateAttempt(first));
        pump.ActivateAttempt(second);
    }

    [Fact]
    public async Task TinyCallbacksConsumeCapacityByPcmDuration()
    {
        var source = new ControlledSource();
        var pump = Pump(
            source,
            new RealtimeAudioPumpTiming((_, _) => Task.CompletedTask));
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(7);
        pump.ActivateAttempt(attempt);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);

        for (int index = 0; index < 2500; index++)
            source.Emit(Frame(64, (byte)index));
        source.Emit(new AudioFrame(Array.Empty<byte>(), Format));
        await source.EmptyFrameYielded.Task.WaitAsync(
            TestContext.Current.CancellationToken);

        Assert.False(run.IsCompleted);
        Assert.Equal(250, attempt.Reader.Count);
        Assert.Equal(RealtimeAudioPump.ActiveQueueByteCapacity, 250 * 640);
        Assert.Equal(TimeSpan.FromSeconds(5), RealtimeAudioPump.ActiveQueueDuration);

        for (int index = 0; index < 10; index++)
            source.Emit(Frame(64, 3));
        RealtimeAudioQueueOverflowException error =
            await Assert.ThrowsAsync<RealtimeAudioQueueOverflowException>(() => run);
        Assert.Equal(7, error.ConnectionGeneration);
        Assert.Equal(250, error.BufferedFrames);
        Assert.Equal(160000, error.BufferedPcmBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), error.ApproximateBufferedDuration);
        Assert.Contains("5.0 seconds", error.Message);
        Assert.Contains("160000 PCM bytes", error.Message);
    }

    [Fact]
    public async Task HealthyConsumerReceivesTinyInputInExactOrder()
    {
        var source = new ControlledSource();
        var pump = Pump(source);
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);
        Task<byte[]> consume = ConsumeAsync(attempt, pump, 4, TestContext.Current.CancellationToken);
        byte[] expected = Enumerable.Range(0, 2560).Select(index => (byte)(index * 31)).ToArray();
        int offset = 0;
        foreach (int length in new[] { 64, 256, 2, 1000, 1238 })
        {
            source.Emit(new AudioFrame(expected.AsMemory(offset, length), Format));
            offset += length;
        }
        source.Complete();

        Assert.Equal(expected, await consume);
        await run;
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task PreparedAttemptReceivesNoAudioWhilePumpDrainsGap()
    {
        var source = new ControlledSource();
        var pump = Pump(source);
        PreparedRealtimeAudioAttempt prepared = pump.PrepareAttempt(2);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);
        for (int index = 0; index < 1000; index++)
            source.Emit(Frame(64, 7));
        source.Emit(new AudioFrame(Array.Empty<byte>(), Format));
        await SpinUntilAsync(
            () => source.ReadCount == 1 && pump.Snapshot.GapBytes == 64000,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, prepared.Reader.Count);
        pump.ActivateAttempt(prepared);
        RealtimeAudioGapCompleted gap =
            Assert.IsType<RealtimeAudioGapCompleted>(pump.CompleteGap());
        Assert.Equal(64000, gap.LostBytes);
        source.Complete();
        await run;
    }

    [Fact]
    public async Task BriefFullQueueWaitMakesProgressWithoutDropping()
    {
        var source = new ControlledSource();
        var waitStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pump = Pump(
            source,
            new RealtimeAudioPumpTiming(async (_, token) =>
            {
                waitStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }));
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);
        for (int index = 0; index < 251; index++)
            source.Emit(Frame(640, (byte)index));
        await waitStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        AudioFrame first = await attempt.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal((byte)0, first.Pcm.Span[0]);
        source.Complete();
        var received = new List<byte>();
        await foreach (AudioFrame frame in attempt.Reader.ReadAllAsync(
                           TestContext.Current.CancellationToken))
            received.Add(frame.Pcm.Span[0]);
        await run;

        Assert.Equal(250, received.Count);
        Assert.Equal(
            Enumerable.Range(1, 250).Select(index => (byte)index),
            received);
    }

    [Fact]
    public async Task CancellationReleasesBlockedWriterWithoutLeakingTask()
    {
        var source = new ControlledSource();
        var blocked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverTimeouts = new RealtimeAudioPumpTiming(
            async (_, token) =>
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        var pump = Pump(source, neverTimeouts);
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task run = pump.RunAsync(cancellation.Token);
        for (int index = 0; index < 251; index++)
            source.Emit(Frame(640, 1));
        await blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(250, attempt.Reader.Count);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(run.IsCompleted);
    }

    [Fact]
    public async Task FailureBeforeActivationInventsNoGap()
    {
        var source = new ControlledSource();
        var pump = Pump(source);
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.EndFailedAttempt(attempt, new IOException("handshake"));

        Assert.Null(pump.CompleteGap());
        Assert.Equal(0, source.ReadCount);
        await Assert.ThrowsAsync<ChannelClosedException>(
            async () => await attempt.Reader.ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OldSendReportCannotChangeNewAttemptGapAccounting()
    {
        var source = new ControlledSource();
        var pump = Pump(source);
        PreparedRealtimeAudioAttempt first = pump.PrepareAttempt(1);
        pump.ActivateAttempt(first);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);
        source.Emit(Frame(640, 1));
        await SpinUntilAsync(
            () => first.Reader.CanCount && first.Reader.Count == 1,
            TestContext.Current.CancellationToken);
        pump.EndFailedAttempt(first, new IOException("drop"));

        PreparedRealtimeAudioAttempt second = pump.PrepareAttempt(2);
        pump.ActivateAttempt(second);
        pump.ReportSent(first, 640);
        RealtimeAudioGapCompleted gap =
            Assert.IsType<RealtimeAudioGapCompleted>(pump.CompleteGap());
        Assert.Equal(640, gap.LostBytes);

        source.Complete();
        await run;
    }

    private static RealtimeAudioPump Pump(
        ControlledSource source,
        RealtimeAudioPumpTiming? timing = null) =>
        new(source.ReadFramesAsync(TestContext.Current.CancellationToken), Format, timing);

    private static AudioFrame Frame(int length, byte value) =>
        new(Enumerable.Repeat(value, length).ToArray(), Format);

    private static async Task<byte[]> ConsumeAsync(
        PreparedRealtimeAudioAttempt attempt,
        RealtimeAudioPump pump,
        int count,
        CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        for (int index = 0; index < count; index++)
        {
            AudioFrame frame = await attempt.Reader.ReadAsync(cancellationToken);
            bytes.AddRange(frame.Pcm.ToArray());
            pump.ReportSent(attempt, frame.Pcm.Length);
        }
        return bytes.ToArray();
    }

    private static async Task SpinUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < 5000 && !condition(); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1, cancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class ControlledSource : IAudioSource
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();
        public AudioFormat Format => Session61RealtimePumpTests.Format;
        public int ReadCount { get; private set; }
        public int YieldCount => Volatile.Read(ref _yieldCount);
        public TaskCompletionSource EmptyFrameYielded { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _yieldCount;

        public void Emit(AudioFrame frame) => Assert.True(_frames.Writer.TryWrite(frame));
        public void Complete() => _frames.Writer.TryComplete();

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            await foreach (AudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _yieldCount);
                if (frame.Pcm.IsEmpty)
                    EmptyFrameYielded.TrySetResult();
                yield return frame;
            }
        }
    }
}

public sealed class Session61SupervisorHandshakeTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task InitialHandshakeDoesNotEnumerateOrBufferTinyMicrophoneCallbacks()
    {
        var readiness = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new BurstingTinySource(3000);
        int factories = 0;
        var received = new ConcurrentQueue<byte>();
        var consumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (generation, sent) =>
            {
                factories++;
                return new ReadinessControlledTranscriber(
                    generation,
                    readiness.Task,
                    sent,
                    received,
                    consumed,
                    expectedFrames: 300);
            },
            _ => Task.CompletedTask);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var observed = new ConcurrentQueue<StreamingTranscriptionEvent>();
        Task run = CollectAsync(supervisor, source, observed, cancellation.Token);

        for (int index = 0; index < 1000; index++)
            await Task.Yield();
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(1, factories);
        Assert.False(run.IsCompleted);

        readiness.SetResult();
        await consumed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, source.ReadCount);
        Assert.Equal(1, factories);
        Assert.DoesNotContain(observed, item => item is RealtimeAudioGapStarted);
        Assert.Contains(observed, item => item is RealtimeAudioRouteActivated
        {
            NormalizedFrameBytes: 640,
            QueueCapacityBytes: 160000
        });
        Assert.Equal(
            Enumerable.Range(0, 3000)
                .SelectMany(index => Enumerable.Repeat((byte)index, 64)),
            received);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    [Fact]
    public async Task InitialHandshakeFailureNeverStartsMicrophoneOrReconnects()
    {
        var source = new BurstingTinySource(3000);
        int factories = 0;
        int healthChecks = 0;
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (_, _) =>
            {
                factories++;
                return new InitialFailureTranscriber();
            },
            _ =>
            {
                healthChecks++;
                return Task.CompletedTask;
            },
            new VoxtralSupervisorTiming((_, _) =>
                throw new InvalidOperationException("Backoff must not run.")));

        IOException failure = await Assert.ThrowsAsync<IOException>(
            async () =>
            {
                await foreach (StreamingTranscriptionEvent _ in supervisor.TranscribeAsync(
                    source.ReadFramesAsync(TestContext.Current.CancellationToken),
                    TestContext.Current.CancellationToken))
                {
                }
            });
        Assert.Equal("initial handshake failed", failure.Message);
        Assert.Equal(0, source.ReadCount);
        Assert.Equal(1, factories);
        Assert.Equal(0, healthChecks);
    }

    [Fact]
    public async Task ReconnectHandshakeDrainsTinyFramesAsOneGapWithoutReplay()
    {
        var generationOneConsumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generationTwoPrepared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generationTwoReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var generationTwoConsumed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ReconnectSource();
        var generationTwoAudio = new ConcurrentQueue<byte>();
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (generation, sent) =>
            {
                if (generation == 1)
                {
                    return new FirstGenerationTranscriber(
                        sent,
                        generationOneConsumed);
                }
                generationTwoPrepared.TrySetResult();
                return new SecondGenerationTranscriber(
                    generation,
                    generationTwoReady.Task,
                    sent,
                    generationTwoAudio,
                    generationTwoConsumed);
            },
            _ => Task.CompletedTask,
            new VoxtralSupervisorTiming((_, _) => Task.CompletedTask));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var observed = new ConcurrentQueue<StreamingTranscriptionEvent>();
        Task run = CollectAsync(supervisor, source, observed, cancellation.Token);

        await SpinUntilAsync(
            () => source.ReadCount == 1,
            TestContext.Current.CancellationToken);
        for (int index = 0; index < 10; index++)
            source.Emit(new AudioFrame(Enumerable.Repeat((byte)1, 64).ToArray(), Format));
        await generationOneConsumed.Task.WaitAsync(TestContext.Current.CancellationToken);
        await generationTwoPrepared.Task.WaitAsync(TestContext.Current.CancellationToken);

        for (int index = 0; index < 1001; index++)
            source.Emit(new AudioFrame(Enumerable.Repeat((byte)7, 64).ToArray(), Format));
        source.Emit(new AudioFrame(Array.Empty<byte>(), Format));
        await SpinUntilAsync(
            () => source.YieldCount >= 1012,
            TestContext.Current.CancellationToken);
        Assert.False(generationTwoConsumed.Task.IsCompleted);

        generationTwoReady.SetResult();
        await SpinUntilAsync(
            () => observed.OfType<RealtimeAudioRouteActivated>()
                .Any(route => route.ConnectionGeneration == 2),
            TestContext.Current.CancellationToken);
        for (int index = 0; index < 10; index++)
            source.Emit(new AudioFrame(Enumerable.Repeat((byte)9, 64).ToArray(), Format));
        await generationTwoConsumed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, source.ReadCount);
        Assert.Equal(640, generationTwoAudio.Count);
        Assert.All(generationTwoAudio, value => Assert.Equal((byte)9, value));
        RealtimeAudioGapCompleted gap = Assert.Single(
            observed.OfType<RealtimeAudioGapCompleted>());
        Assert.Equal(64064, gap.LostBytes);
        Assert.Equal(Format.DurationOf(64064), gap.ApproximateDuration);
        Assert.Single(observed.OfType<RealtimeAudioGapStarted>());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
    }

    private static async Task CollectAsync(
        IStreamingTranscriber supervisor,
        IAudioSource source,
        ConcurrentQueue<StreamingTranscriptionEvent> observed,
        CancellationToken cancellationToken)
    {
        await foreach (StreamingTranscriptionEvent item in supervisor
                           .TranscribeAsync(source.ReadFramesAsync(cancellationToken), cancellationToken)
                           .WithCancellation(cancellationToken))
            observed.Enqueue(item);
    }

    private static async Task SpinUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < 20000 && !condition(); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.True(condition());
    }

    private sealed class BurstingTinySource(int count) : IAudioSource
    {
        public AudioFormat Format => Session61SupervisorHandshakeTests.Format;
        public int ReadCount { get; private set; }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            for (int index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AudioFrame(
                    Enumerable.Repeat((byte)index, 64).ToArray(),
                    Format);
                await Task.Yield();
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ReadinessControlledTranscriber(
        int generation,
        Task readiness,
        Action<int> reportSent,
        ConcurrentQueue<byte> received,
        TaskCompletionSource consumed,
        int expectedFrames) : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await readiness.WaitAsync(cancellationToken);
            yield return new StreamingSessionStarted(
                $"session-{generation}", 1, "model", 240, generation);
            int receivedBytes = 0;
            await foreach (AudioFrame frame in audio.WithCancellation(cancellationToken))
            {
                foreach (byte value in frame.Pcm.ToArray())
                    received.Enqueue(value);
                reportSent(frame.Pcm.Length);
                receivedBytes += frame.Pcm.Length;
                if (receivedBytes == expectedFrames * 640)
                {
                    consumed.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InitialFailureTranscriber : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            throw new IOException("initial handshake failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ReconnectSource : IAudioSource
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();
        public AudioFormat Format => Session61SupervisorHandshakeTests.Format;
        public int ReadCount { get; private set; }
        public int YieldCount => Volatile.Read(ref _yieldCount);
        private int _yieldCount;

        public void Emit(AudioFrame frame) => Assert.True(_frames.Writer.TryWrite(frame));

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            await foreach (AudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _yieldCount);
                yield return frame;
            }
        }
    }

    private sealed class FirstGenerationTranscriber(
        Action<int> reportSent,
        TaskCompletionSource consumed) : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new StreamingSessionStarted("session-1", 1, "model", 240, 1);
            await foreach (AudioFrame frame in audio.WithCancellation(cancellationToken))
            {
                reportSent(frame.Pcm.Length);
                consumed.TrySetResult();
                throw new IOException("deterministic disconnect");
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SecondGenerationTranscriber(
        int generation,
        Task readiness,
        Action<int> reportSent,
        ConcurrentQueue<byte> received,
        TaskCompletionSource consumed) : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await readiness.WaitAsync(cancellationToken);
            yield return new StreamingSessionStarted(
                $"session-{generation}", 1, "model", 240, generation);
            await foreach (AudioFrame frame in audio.WithCancellation(cancellationToken))
            {
                foreach (byte value in frame.Pcm.ToArray())
                    received.Enqueue(value);
                reportSent(frame.Pcm.Length);
                consumed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
