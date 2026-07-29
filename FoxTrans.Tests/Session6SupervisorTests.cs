using System.Runtime.CompilerServices;
using Xunit;

public sealed class Session6SupervisorTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task ReconnectCreatesFreshSessionAndMonotonicGeneration()
    {
        var source = new WaitingSource();
        var generations = new List<int>();
        int active = 0;
        int maximumActive = 0;
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (generation, _) =>
            {
                generations.Add(generation);
                return generation == 1
                    ? new ScriptedTranscriber(generation, true, Enter, Exit)
                    : new ScriptedTranscriber(generation, false, Enter, Exit);
            },
            _ => Task.CompletedTask,
            new DelegateTimeProvider(
                delay: (_, _) => Task.CompletedTask));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<StreamingTranscriptionEvent> events = supervisor
            .TranscribeAsync(source.ReadFramesAsync(cancellation.Token), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);

        var observed = new List<StreamingTranscriptionEvent>();
        while (!observed.OfType<StreamingPartialTranscript>().Any(partial => partial.Text == "new"))
        {
            Assert.True(await events.MoveNextAsync());
            observed.Add(events.Current);
        }

        Assert.Equal([1, 2], generations);
        Assert.Equal(1, maximumActive);
        Assert.Equal(1, source.ReadCount);
        Assert.Contains(observed, item => item is VoxtralConnectionLost);
        Assert.Contains(observed, item => item is VoxtralReconnectScheduled { Attempt: 1, Delay.TotalSeconds: 1 });
        Assert.Contains(observed, item => item is VoxtralReconnected { ConnectionGeneration: 2 });
        Assert.Contains(observed, item => item is RealtimeAudioGapStarted);
        Assert.Contains(observed, item => item is RealtimeAudioGapCompleted);
        Assert.Contains(observed, item => item is StreamingSessionStarted { ConnectionGeneration: 2 });

        cancellation.Cancel();

        void Enter()
        {
            int now = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, now);
        }
        void Exit() => Interlocked.Decrement(ref active);
    }

    [Fact]
    public async Task CancellationInterruptsBackoffWithoutAnotherAttempt()
    {
        var source = new WaitingSource();
        var delayStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int factories = 0;
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (generation, _) =>
            {
                factories++;
                return new ScriptedTranscriber(generation, true);
            },
            _ => Task.CompletedTask,
            new DelegateTimeProvider(delay: async (_, token) =>
            {
                delayStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<StreamingTranscriptionEvent> events = supervisor
            .TranscribeAsync(source.ReadFramesAsync(cancellation.Token), cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        while (true)
        {
            Assert.True(await events.MoveNextAsync());
            if (events.Current is VoxtralReconnectScheduled)
                break;
        }

        Task<bool> waiting = events.MoveNextAsync().AsTask();
        await delayStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(1, factories);
    }

    [Fact]
    public async Task FatalErrorAfterSessionDoesNotRetry()
    {
        var source = new WaitingSource();
        int factories = 0;
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (generation, _) =>
            {
                factories++;
                return new FatalTranscriber(generation);
            },
            _ => Task.CompletedTask,
            new DelegateTimeProvider(delay: (_, _) =>
                throw new InvalidOperationException("Backoff must not run.")));
        VoxtralFoxException error = await Assert.ThrowsAsync<VoxtralFoxException>(
            async () =>
            {
                await foreach (StreamingTranscriptionEvent _ in supervisor.TranscribeAsync(
                    source.ReadFramesAsync(TestContext.Current.CancellationToken),
                    TestContext.Current.CancellationToken))
                {
                }
            });
        Assert.Equal("invalid_configuration", error.Code);
        Assert.Equal(1, factories);
    }

    [Fact]
    public async Task MicrophoneEndIsFatalAndDoesNotReconnect()
    {
        var source = new FiniteSource();
        int factories = 0;
        await using var supervisor = new VoxtralConnectionSupervisor(
            Format,
            (generation, _) =>
            {
                factories++;
                return new WaitingTranscriber(generation);
            },
            _ => Task.CompletedTask,
            new DelegateTimeProvider(delay: (_, _) =>
                throw new InvalidOperationException("Backoff must not run.")));
        await Assert.ThrowsAsync<RealtimeAudioSourceEndedException>(
            async () =>
            {
                await foreach (StreamingTranscriptionEvent _ in supervisor.TranscribeAsync(
                    source.ReadFramesAsync(TestContext.Current.CancellationToken),
                    TestContext.Current.CancellationToken))
                {
                }
            });
        Assert.Equal(1, factories);
        Assert.Equal(1, source.ReadCount);
    }

    private sealed class ScriptedTranscriber(
        int generation,
        bool fail,
        Action? enter = null,
        Action? exit = null) : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            enter?.Invoke();
            try
            {
                yield return new StreamingSessionStarted(
                    $"session-{generation}", 1, "model", 240, generation);
                yield return new StreamingPartialTranscript(
                    1, generation == 1 ? "old" : "new", 80);
                await Task.Yield();
                if (fail)
                    throw new IOException("temporary network failure");
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                exit?.Invoke();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitingSource : IAudioSource
    {
        public AudioFormat Format => Session6SupervisorTests.Format;
        public int ReadCount { get; private set; }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class FatalTranscriber(int generation) : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new StreamingSessionStarted(
                $"session-{generation}", 1, "model", 240, generation);
            await Task.Yield();
            throw new VoxtralFoxException(
                "invalid_configuration",
                "fatal client configuration");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class WaitingTranscriber(int generation) : IStreamingTranscriber
    {
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return new StreamingSessionStarted(
                $"session-{generation}", 1, "model", 240, generation);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FiniteSource : IAudioSource
    {
        public AudioFormat Format => Session6SupervisorTests.Format;
        public int ReadCount { get; private set; }

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadCount++;
            yield return new AudioFrame(new byte[320], Format);
            await Task.Yield();
        }
    }
}
