using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Xunit;

public sealed class BatchPipelineTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task BatchPipelinePreservesOrderAndCaptureContinuesWhileTranscriptionBlocks()
    {
        var segmenter = new Segmenter(2);
        var transcriber = new ControlledTranscriber();
        var translator = new RecordingTranslator();
        var output = new Output();
        Task run = FoxTransApp.RunBatchTranscriptionPipelineAsync(new Source([Frame(1), Frame(2)]), segmenter, transcriber, translator, [output], new Reporter(), cancellationToken: TestContext.Current.CancellationToken);
        await transcriber.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await segmenter.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, segmenter.Count);
        Assert.Equal(1, transcriber.Count);
        transcriber.Release.TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(["transcript-1", "transcript-2"], translator.Sources.ToArray());
        Assert.Equal(["translation-transcript-1", "translation-transcript-2"], output.Translations.ToArray());
        Assert.Equal(1, translator.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task ProviderFailuresDoNotPublishAndNextPhraseStillProcesses()
    {
        var transcriber = new FailingFirstTranscriber();
        var translator = new RecordingTranslator(); var output = new Output(); var reporter = new Reporter();
        await FoxTransApp.RunBatchTranscriptionPipelineAsync(new Source([Frame(1), Frame(2)]), new Segmenter(2), transcriber, translator, [output], reporter, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["transcript-2"], translator.Sources.ToArray());
        Assert.Equal(["translation-transcript-2"], output.Translations.ToArray());
        Assert.Contains(reporter.Events, x => x.Kind == AppEventKind.ApiError);
        Assert.False(output.Typing.ToArray()[^1]);
    }

    [Fact]
    public async Task TranslationFailureSkipsOutputAndRestoresTyping()
    {
        var output = new Output();
        await FoxTransApp.RunBatchTranscriptionPipelineAsync(new Source([Frame(1)]), new Segmenter(1), new FixedTranscriber(), new FailingTranslator(), [output], new Reporter(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(output.Translations);
        Assert.False(output.Typing.ToArray()[^1]);
    }

    private static AudioFrame Frame(byte marker) => new(new byte[] { marker, 0, 0, 0 }, Format);
    private sealed class Source(IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public AudioFormat Format => BatchPipelineTests.Format;
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken) { foreach (var frame in frames) { yield return frame; await Task.Yield(); } }
    }
    private sealed class Segmenter(int expected) : IAudioSegmenter
    {
        public int Count { get; private set; } public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<SegmentationUpdate> SegmentAsync(IAsyncEnumerable<AudioFrame> frames, [EnumeratorCancellation] CancellationToken cancellationToken) { await foreach (var frame in frames.WithCancellation(cancellationToken)) { Count++; if (Count == expected) Completed.TrySetResult(); yield return new(SegmentationUpdateKind.SegmentCompleted, new AudioSegment(frame.Pcm, frame.Format)); } }
    }
    private sealed class ControlledTranscriber : IBatchTranscriber
    {
        private int _count; public int Count => Volatile.Read(ref _count); public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> TranscribeAsync(AudioSegment segment, CancellationToken cancellationToken) { int call = Interlocked.Increment(ref _count); if (call == 1) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); } return $"transcript-{segment.Pcm.Span[0]}"; }
    }
    private sealed class FailingFirstTranscriber : IBatchTranscriber
    {
        private int _count; public Task<string> TranscribeAsync(AudioSegment segment, CancellationToken cancellationToken) => Interlocked.Increment(ref _count) == 1 ? Task.FromException<string>(new OpenAiProviderException("audio transcription", "failed")) : Task.FromResult($"transcript-{segment.Pcm.Span[0]}");
    }
    private sealed class FixedTranscriber : IBatchTranscriber { public Task<string> TranscribeAsync(AudioSegment segment, CancellationToken cancellationToken) => Task.FromResult("source"); }
    private sealed class RecordingTranslator : ITextTranslator
    {
        private int _active; private int _max; public ConcurrentQueue<string> Sources { get; } = new(); public int MaximumConcurrentCalls => _max;
        public Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken) { int active = Interlocked.Increment(ref _active); _max = Math.Max(_max, active); Sources.Enqueue(sourceText); Interlocked.Decrement(ref _active); return Task.FromResult($"translation-{sourceText}"); }
    }
    private sealed class FailingTranslator : ITextTranslator { public Task<string> TranslateAsync(string sourceText, CancellationToken cancellationToken) => Task.FromException<string>(new OpenAiProviderException("text translation", "failed")); }
    private sealed class Output : IOutputSink
    {
        public string Name => "test"; public ConcurrentQueue<string> Translations { get; } = new(); public ConcurrentQueue<bool> Typing { get; } = new();
        public Task PublishAsync(TranslationUpdate update, CancellationToken cancellationToken) { if (update.Kind == TranslationUpdateKind.Translation) Translations.Enqueue(update.Text!); else Typing.Enqueue(update.IsTyping); return Task.CompletedTask; }
    }
    private sealed class Reporter : IAppReporter { public ConcurrentQueue<AppEvent> Events { get; } = new(); public void Report(AppEvent appEvent) => Events.Enqueue(appEvent); }
}
