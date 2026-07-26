using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

public sealed class PipelineConcurrencyTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task SegmentationContinuesWhileFirstTranslationWaitsAndNoSegmentIsLost()
    {
        var source = new FiniteAudioSource([Frame(1), Frame(2)]);
        var segmenter = new PassThroughSegmenter(expectedCount: 2);
        var translator = new ControlledTranslator();
        var output = new RecordingOutput();
        var reporter = new RecordingReporter();

        Task pipeline = FoxTransApp.RunDirectAudioPipelineAsync(
            source,
            segmenter,
            translator,
            [output],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        await translator.FirstStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await segmenter.ExpectedCountReached.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, segmenter.ProcessedCount);
        Assert.Equal(1, translator.CallCount);

        translator.ReleaseFirst.TrySetResult();
        await pipeline.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(2, translator.CallCount);
        Assert.Equal(
            ["translation-1", "translation-2"],
            output.Updates
                .Where(update => update.Kind == TranslationUpdateKind.Translation)
                .Select(update => update.Text!)
                .ToArray());
    }

    [Fact]
    public async Task TypingReturnsToFalseAfterTranslationError()
    {
        var output = new RecordingOutput();
        var reporter = new RecordingReporter();

        await FoxTransApp.RunDirectAudioPipelineAsync(
            new FiniteAudioSource([Frame(1)]),
            new PassThroughSegmenter(expectedCount: 1),
            new ThrowingTranslator(),
            [output],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        TranslationUpdate[] typing = output.Updates
            .Where(update => update.Kind == TranslationUpdateKind.Typing)
            .ToArray();
        Assert.True(typing[0].IsTyping);
        Assert.False(typing[^1].IsTyping);
        Assert.Contains(reporter.Events, appEvent => appEvent.Kind == AppEventKind.ApiError);
    }

    [Fact]
    public async Task CancellationStopsWorkersAndForcesTypingFalse()
    {
        var source = new BlockingAudioSource();
        var output = new RecordingOutput();
        var reporter = new RecordingReporter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        Task pipeline = FoxTransApp.RunDirectAudioPipelineAsync(
            source,
            new PassThroughSegmenter(expectedCount: int.MaxValue),
            new ControlledTranslator(),
            [output],
            reporter,
            cancellationToken: cancellation.Token);

        await source.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pipeline.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.False(output.Updates.ToArray()[^1].IsTyping);
        Assert.Contains(reporter.Events, appEvent => appEvent.Kind == AppEventKind.Stopped);
    }

    private static AudioFrame Frame(int marker)
    {
        byte[] pcm = new byte[640];
        pcm[0] = (byte)marker;
        return new AudioFrame(pcm, Format);
    }

    private sealed class FiniteAudioSource(IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public AudioFormat Format => PipelineConcurrencyTests.Format;

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (AudioFrame frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }
    }

    private sealed class BlockingAudioSource : IAudioSource
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AudioFormat Format => PipelineConcurrencyTests.Format;

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private sealed class PassThroughSegmenter(int expectedCount) : IAudioSegmenter
    {
        public TaskCompletionSource ExpectedCountReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ProcessedCount { get; private set; }

        public async IAsyncEnumerable<SegmentationUpdate> SegmentAsync(
            IAsyncEnumerable<AudioFrame> frames,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (AudioFrame frame in frames.WithCancellation(cancellationToken))
            {
                ProcessedCount++;
                if (ProcessedCount == expectedCount)
                {
                    ExpectedCountReached.TrySetResult();
                }

                yield return new SegmentationUpdate(
                    SegmentationUpdateKind.SegmentCompleted,
                    new AudioSegment(frame.Pcm, frame.Format));
            }
        }
    }

    private sealed class ControlledTranslator : IAudioTranslator
    {
        private int _callCount;

        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<string> TranslateAsync(
            AudioSegment segment,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                FirstStarted.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }

            return $"translation-{segment.Pcm.Span[0]}";
        }
    }

    private sealed class ThrowingTranslator : IAudioTranslator
    {
        public Task<string> TranslateAsync(AudioSegment segment, CancellationToken cancellationToken) =>
            Task.FromException<string>(new AudioTranslationException("expected failure"));
    }

    private sealed class RecordingOutput : IOutputSink
    {
        public ConcurrentQueue<TranslationUpdate> Updates { get; } = new();

        public string Name => "test output";

        public Task PublishAsync(TranslationUpdate update, CancellationToken cancellationToken)
        {
            Updates.Enqueue(update);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingReporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();

        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }
}
