using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Xunit;

public sealed class RealtimePipelineTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task CompleteRealtimePipelineUpdatesBeforeSilenceSettlesOnceAndStartsNextUtterance()
    {
        var clock = new ManualTiming(
            new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero));
        var source = new ContinuousSource();
        var transcriber = new EventTranscriber();
        var translator = new Translator();
        var output = new Output("output");
        var reporter = new Reporter();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        Task run = FoxTransApp.RunRealtimeTranscriptionPipelineAsync(
            source,
            transcriber,
            translator,
            [output],
            new ResolvedRealtimeSettings(100, 500, 2, 1000, 18),
            reporter,
            clock.Timing,
            cancellation.Token);

        await transcriber.Started.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        transcriber.Enqueue(new StreamingPartialTranscript(1, "one two", 80));
        await WaitUntilAsync(() => reporter.Count(AppEventKind.LogicalUtteranceStarted) == 1);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => translator.Sources.Count == 1);
        Assert.Equal("one two", translator.Sources.Single());
        Assert.Single(output.Translations);

        transcriber.Enqueue(new StreamingPartialTranscript(
            2,
            "one two three four five six seven eight",
            160));
        await WaitUntilAsync(() => reporter.Count(AppEventKind.LogicalUtteranceUpdated) == 1);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => translator.Sources.Count == 2);
        Assert.EndsWith("six seven eight", translator.Sources.Last());
        Assert.True(TranslationWindows.TextElementCount(translator.Sources.Last()) <= 18);

        transcriber.Enqueue(new StreamingPartialTranscript(
            3,
            "one two three four five six seven eight nine",
            240));
        await WaitUntilAsync(() => reporter.Count(AppEventKind.LogicalUtteranceUpdated) == 2);
        transcriber.Enqueue(new StreamingServerWarning(
            "processing_lag",
            "behind",
            100,
            null, null, null, null, null, null, null));
        await WaitUntilAsync(() => reporter.Count(AppEventKind.VoxtralWarning) == 1);
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(1000));
        await WaitUntilAsync(() => reporter.Count(AppEventKind.LogicalUtteranceSettled) == 1);
        await WaitUntilAsync(() => translator.Sources.Count == 3);
        Assert.False(output.Typing.Last());

        transcriber.Enqueue(new StreamingPartialTranscript(
            4,
            "one two three four five six seven eight nine   next words",
            320));
        await WaitUntilAsync(() => reporter.Count(AppEventKind.LogicalUtteranceStarted) == 2);
        Assert.Equal(2, output.Typing.Count(value => value));
        await clock.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => translator.Sources.Count == 4);
        Assert.Equal("next words", translator.Sources.Last());
        Assert.All(output.Translations, translation =>
            Assert.StartsWith("translated:", translation));
        Assert.Equal(1, transcriber.SessionCount);
        Assert.True(source.Read);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken));
        Assert.False(output.Typing.Last());
        Assert.Equal(1, reporter.Count(AppEventKind.LogicalUtteranceSettled));
    }

    [Fact]
    public async Task OutputsAreCalledInConfigurationOrderAndFailureDoesNotStopLaterOutput()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var order = new ConcurrentQueue<string>();
        var reporter = new Reporter();
        var first = new OrderedOutput("first", order, failTranslations: true);
        var second = new OrderedOutput("second", order, failTranslations: false);
        var translator = new Translator();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator,
            [first, second],
            new ResolvedRealtimeSettings(0, 0, 1, 1000, 100),
            reporter,
            getUtcNow: () => now);
        await scheduler.SubmitAsync(
            new TranslationCandidate(
                1, 1, 1, "source", false, true, 1, 80, now, now),
            now,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => order.Contains("second:Translation"));
        Assert.Equal(
            ["first:Translation", "second:Translation"],
            order.Where(item => item.EndsWith(":Translation")).ToArray());
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.OutputError &&
            item.Message!.StartsWith("first:", StringComparison.Ordinal));
        await scheduler.SubmitAsync(
            new TranslationCandidate(
                1, 1, 1, "source", false, true, 1, 80, now, now),
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        await scheduler.TickAsync(
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        Assert.Single(translator.Sources);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    private sealed class ManualTiming(DateTimeOffset initial)
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _delays = new();
        private DateTimeOffset _now = initial;
        public RealtimePipelineTiming Timing => new(
            () => _now,
            DelayAsync);

        public async Task AdvanceAsync(TimeSpan amount)
        {
            await WaitUntilAsync(() => !_delays.IsEmpty);
            _now += amount;
            Assert.True(_delays.TryDequeue(out TaskCompletionSource? delay));
            delay.TrySetResult();
            await Task.Yield();
        }

        private Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            _delays.Enqueue(completion);
            return completion.Task;
        }
    }

    private sealed class ContinuousSource : IAudioSource
    {
        public bool Read { get; private set; }
        public AudioFormat Format => RealtimePipelineTests.Format;
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Read = true;
            while (true)
            {
                yield return new AudioFrame(new byte[640], Format);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }
    }

    private sealed class EventTranscriber : IStreamingTranscriber
    {
        private readonly Channel<StreamingTranscriptionEvent> _events =
            Channel.CreateUnbounded<StreamingTranscriptionEvent>();
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SessionCount { get; private set; }

        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await using IAsyncEnumerator<AudioFrame> frames =
                audio.GetAsyncEnumerator(cancellationToken);
            Assert.True(await frames.MoveNextAsync());
            SessionCount++;
            Started.TrySetResult();
            yield return new StreamingSessionStarted("st", 1, "model", 240, 1);
            await foreach (StreamingTranscriptionEvent item in
                _events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }

        public void Enqueue(StreamingTranscriptionEvent item) =>
            Assert.True(_events.Writer.TryWrite(item));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Translator : ITextTranslator
    {
        public ConcurrentQueue<string> Sources { get; } = new();
        public Task<string> TranslateAsync(
            string sourceText,
            CancellationToken cancellationToken)
        {
            Sources.Enqueue(sourceText);
            return Task.FromResult("translated:" + sourceText);
        }
    }

    private sealed class Output(string name) : IOutputSink
    {
        public ConcurrentQueue<string> Translations { get; } = new();
        public ConcurrentQueue<bool> Typing { get; } = new();
        public string Name => name;
        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            if (update.Kind == TranslationUpdateKind.Translation)
                Translations.Enqueue(update.Text!);
            else
                Typing.Enqueue(update.IsTyping);
            return Task.CompletedTask;
        }
    }

    private sealed class OrderedOutput(
        string name,
        ConcurrentQueue<string> order,
        bool failTranslations) : IOutputSink
    {
        public string Name => name;
        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            order.Enqueue($"{name}:{update.Kind}");
            return failTranslations && update.Kind == TranslationUpdateKind.Translation
                ? Task.FromException(new InvalidOperationException("expected"))
                : Task.CompletedTask;
        }
    }

    private sealed class Reporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public int Count(AppEventKind kind) => Events.Count(item => item.Kind == kind);
        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }
}
