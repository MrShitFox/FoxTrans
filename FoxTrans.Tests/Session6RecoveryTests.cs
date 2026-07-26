using System.Runtime.CompilerServices;
using Xunit;

public sealed class Session6RecoveryTests
{
    public static IEnumerable<object[]> RetryableCodes()
    {
        foreach (string code in new[]
        {
            "health_request_failed", "server_not_ready", "server_busy", "shutting_down",
            "backend_error", "internal_error", "idle_timeout",
            "realtime_capacity_exceeded", "unexpected_close"
        })
            yield return [code];
    }

    public static IEnumerable<object[]> FatalCodes()
    {
        foreach (string code in new[]
        {
            "unauthorized", "invalid_configuration", "unsupported_audio_format",
            "invalid_audio_frame", "invalid_audio_stream", "invalid_sequence",
            "invalid_json", "invalid_event", "event_too_large", "incompatible_session",
            "incompatible_server", "unsupported_transcription_delay",
            "unexpected_binary_event", "invalid_state", "unknown_fatal"
        })
            yield return [code];
    }

    [Theory]
    [MemberData(nameof(RetryableCodes))]
    public void ClassifiesDocumentedTransientErrors(string code) =>
        Assert.True(VoxtralRetryPolicy.IsRetryable(new VoxtralFoxException(code, "safe")));

    [Theory]
    [MemberData(nameof(FatalCodes))]
    public void ClassifiesProtocolAndConfigurationErrorsAsFatal(string code) =>
        Assert.False(VoxtralRetryPolicy.IsRetryable(new VoxtralFoxException(code, "safe")));

    [Fact]
    public void ClassifiesTransportFailuresAsTransient()
    {
        Assert.True(VoxtralRetryPolicy.IsRetryable(new HttpRequestException("network")));
        Assert.True(VoxtralRetryPolicy.IsRetryable(new IOException("network")));
        Assert.False(VoxtralRetryPolicy.IsRetryable(new InvalidOperationException("client defect")));
        Assert.False(VoxtralRetryPolicy.IsRetryable(new VoxtralFoxException(
            "unexpected_close",
            "policy violation",
            closeStatus: System.Net.WebSockets.WebSocketCloseStatus.PolicyViolation)));
    }

    [Fact]
    public void UsesExactDeterministicBackoff()
    {
        double[] seconds = Enumerable.Range(1, 6)
            .Select(attempt => VoxtralRetryPolicy.DelayForAttempt(attempt).TotalSeconds)
            .ToArray();
        Assert.Equal([1d, 2d, 4d, 8d, 10d, 10d], seconds);
    }

    [Fact]
    public void NewTranscriptEpochStartsWithoutOldServerText()
    {
        LogicalUtteranceState old = LogicalUtteranceState.Initial with
        {
            LatestCumulativeText = "old cumulative",
            CommittedBoundary = "old ",
            ActiveUtteranceText = "cumulative",
            TranscriptEpoch = 7,
            UtteranceId = 4,
            IsActive = true
        };
        LogicalUtteranceState next = UtteranceTracking.StartNewTranscriptEpoch(old);
        Assert.Equal(8, next.TranscriptEpoch);
        Assert.Equal(4, next.UtteranceId);
        Assert.Equal("", next.LatestCumulativeText);
        Assert.Equal("", next.CommittedBoundary);
        Assert.False(next.IsActive);
    }

    [Fact]
    public async Task PumpEnumeratesSourceOnceAndSendsFramesInOrder()
    {
        var source = new OneReadSource([Frame(1), Frame(2), Frame(3)]);
        var pump = new RealtimeAudioPump(
            source.ReadFramesAsync(TestContext.Current.CancellationToken),
            source.Format);
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);
        var values = new List<byte>();
        await foreach (AudioFrame frame in attempt.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            values.Add(frame.Pcm.Span[0]);
            pump.ReportSent(attempt, frame.Pcm.Length);
        }
        await run;
        Assert.Equal([1, 2, 3], values);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task PumpDrainsDisconnectedAudioAndDoesNotReplayIt()
    {
        var source = new GatedSource();
        var pump = new RealtimeAudioPump(
            source.ReadFramesAsync(TestContext.Current.CancellationToken),
            source.Format);
        int gapStarts = 0;
        pump.GapStarted += () => gapStarts++;
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);

        source.Emit(Frame(9));
        await source.Observed.Task.WaitAsync(TestContext.Current.CancellationToken);
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        source.Emit(Frame(2));
        source.Complete();

        AudioFrame transmitted = await attempt.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await run;
        RealtimeAudioGapCompleted gap = Assert.IsType<RealtimeAudioGapCompleted>(pump.CompleteGap());
        Assert.Equal(2, transmitted.Pcm.Span[0]);
        Assert.Equal(1, gapStarts);
        Assert.Equal(640, gap.LostBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(20), gap.ApproximateDuration);
        Assert.False(attempt.Reader.TryRead(out _));
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task FailedAttemptAccountsForQueuedButUnsentAudio()
    {
        var source = new GatedSource();
        var pump = new RealtimeAudioPump(
            source.ReadFramesAsync(TestContext.Current.CancellationToken),
            source.Format);
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        Task run = pump.RunAsync(TestContext.Current.CancellationToken);
        source.Emit(Frame(1));
        await source.Observed.Task.WaitAsync(TestContext.Current.CancellationToken);
        pump.EndFailedAttempt(attempt, new IOException("drop"));
        source.Complete();
        await run;
        RealtimeAudioGapCompleted gap = Assert.IsType<RealtimeAudioGapCompleted>(pump.CompleteGap());
        Assert.Equal(640, gap.LostBytes);
    }

    [Fact]
    public async Task ActiveQueueOverflowIsBoundedAndExplicit()
    {
        AudioFrame[] frames = Enumerable.Range(0, RealtimeAudioPump.ActiveQueueCapacity + 1)
            .Select(index => Frame((byte)index))
            .ToArray();
        var source = new OneReadSource(frames);
        var pump = new RealtimeAudioPump(
            source.ReadFramesAsync(TestContext.Current.CancellationToken),
            source.Format,
            new RealtimeAudioPumpTiming((_, _) => Task.CompletedTask));
        PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(1);
        pump.ActivateAttempt(attempt);
        RealtimeAudioQueueOverflowException error =
            await Assert.ThrowsAsync<RealtimeAudioQueueOverflowException>(
                () => pump.RunAsync(TestContext.Current.CancellationToken));
        Assert.Contains("5.0 seconds", error.Message);
        Assert.Contains("160000 PCM bytes", error.Message);
        Assert.Contains("connection generation 1", error.Message);
        Assert.Equal(250, RealtimeAudioPump.ActiveQueueCapacity);
        Assert.Equal(1, source.ReadCount);
    }

    private static AudioFrame Frame(byte value) =>
        new(Enumerable.Repeat(value, 640).ToArray(), new(16000, 16, 1));

    private sealed class OneReadSource(IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public AudioFormat Format { get; } = new(16000, 16, 1);
        public int ReadCount { get; private set; }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            foreach (AudioFrame frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }
    }

    private sealed class GatedSource : IAudioSource
    {
        private readonly System.Threading.Channels.Channel<AudioFrame> _frames =
            System.Threading.Channels.Channel.CreateUnbounded<AudioFrame>();
        public AudioFormat Format { get; } = new(16000, 16, 1);
        public int ReadCount { get; private set; }
        public TaskCompletionSource Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(AudioFrame frame) => _frames.Writer.TryWrite(frame);
        public void Complete() => _frames.Writer.TryComplete();

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            await foreach (AudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
                Observed.TrySetResult();
            }
        }
    }
}
