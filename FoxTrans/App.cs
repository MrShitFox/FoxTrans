using System.Threading.Channels;

public enum AppEventKind
{
    Listening,
    SpeechStarted,
    SegmentCompleted,
    ProcessingStarted,
    ProcessingCompleted,
    TranslationCompleted,
    ShortPhraseIgnored,
    ApiError,
    OutputError,
    QueueOverflow,
    ConfigCreated,
    ConfigMigrated,
    ConfigWarning,
    ConfigError,
    UnsupportedValidPipeline,
    Stopped,
    FatalError
}

public sealed record AppEvent(AppEventKind Kind, string? Message = null, TimeSpan? Duration = null)
{
    public static AppEvent Listening() => new(AppEventKind.Listening);
    public static AppEvent SpeechStarted() => new(AppEventKind.SpeechStarted);
    public static AppEvent SegmentCompleted(TimeSpan duration) =>
        new(AppEventKind.SegmentCompleted, Duration: duration);
    public static AppEvent ProcessingStarted() => new(AppEventKind.ProcessingStarted);
    public static AppEvent ProcessingCompleted() => new(AppEventKind.ProcessingCompleted);
    public static AppEvent TranslationCompleted(string translation) =>
        new(AppEventKind.TranslationCompleted, translation);
    public static AppEvent ShortPhraseIgnored(TimeSpan duration) =>
        new(AppEventKind.ShortPhraseIgnored, Duration: duration);
    public static AppEvent ApiError(string message) => new(AppEventKind.ApiError, message);
    public static AppEvent OutputError(string output, string message) =>
        new(AppEventKind.OutputError, $"{output}: {message}");
    public static AppEvent QueueOverflow(string message) => new(AppEventKind.QueueOverflow, message);
    public static AppEvent ConfigCreated(string path) => new(AppEventKind.ConfigCreated, path);
    public static AppEvent ConfigMigrated(string path) => new(AppEventKind.ConfigMigrated, path);
    public static AppEvent ConfigWarning(string message) => new(AppEventKind.ConfigWarning, message);
    public static AppEvent ConfigError(string message) => new(AppEventKind.ConfigError, message);
    public static AppEvent UnsupportedValidPipeline(string message) => new(AppEventKind.UnsupportedValidPipeline, message);
    public static AppEvent Stopped() => new(AppEventKind.Stopped);
    public static AppEvent FatalError(string message) => new(AppEventKind.FatalError, message);
}

public interface IAppReporter
{
    void Report(AppEvent appEvent);
}

public sealed record DirectAudioPipelineOptions(int CompletedSegmentCapacity = 4);

public static class FoxTransApp
{
    public static async Task RunDirectAudioPipelineAsync(
        IAudioSource audioSource,
        IAudioSegmenter segmenter,
        IAudioTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        DirectAudioPipelineOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new DirectAudioPipelineOptions();
        if (options.CompletedSegmentCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        var completedSegments = Channel.CreateBounded<AudioSegment>(
            new BoundedChannelOptions(options.CompletedSegmentCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });

        reporter.Report(AppEvent.Listening());

        Task segmentationWorker = ProduceSegmentsAsync(
            audioSource,
            segmenter,
            completedSegments.Writer,
            reporter,
            cancellationToken);
        Task translationWorker = ConsumeSegmentsAsync(
            completedSegments.Reader,
            translator,
            outputs,
            reporter,
            cancellationToken);

        try
        {
            await Task.WhenAll(segmentationWorker, translationWorker);
        }
        finally
        {
            completedSegments.Writer.TryComplete();
            await EnsureTypingStoppedAsync(outputs, reporter);
            reporter.Report(AppEvent.Stopped());
        }
    }

    private static async Task ProduceSegmentsAsync(
        IAudioSource source,
        IAudioSegmenter segmenter,
        ChannelWriter<AudioSegment> writer,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        try
        {
            IAsyncEnumerable<AudioFrame> frames = source.ReadFramesAsync(cancellationToken);
            await foreach (SegmentationUpdate update in
                segmenter.SegmentAsync(frames, cancellationToken).WithCancellation(cancellationToken))
            {
                switch (update.Kind)
                {
                    case SegmentationUpdateKind.SpeechStarted:
                        reporter.Report(AppEvent.SpeechStarted());
                        break;
                    case SegmentationUpdateKind.ShortPhraseIgnored:
                        reporter.Report(AppEvent.ShortPhraseIgnored(update.Duration ?? TimeSpan.Zero));
                        reporter.Report(AppEvent.Listening());
                        break;
                    case SegmentationUpdateKind.SegmentCompleted when update.Segment is not null:
                        reporter.Report(AppEvent.SegmentCompleted(update.Segment.Duration));
                        if (!writer.TryWrite(update.Segment))
                        {
                            reporter.Report(AppEvent.QueueOverflow(
                                "Completed-segment queue is full; capture is applying backpressure."));
                            await writer.WriteAsync(update.Segment, cancellationToken);
                        }

                        reporter.Report(AppEvent.Listening());
                        break;
                }
            }

            writer.TryComplete();
        }
        catch (Exception exception)
        {
            if (exception is AudioBufferOverflowException)
            {
                reporter.Report(AppEvent.QueueOverflow(exception.Message));
            }

            writer.TryComplete(exception);
            throw;
        }
    }

    private static async Task ConsumeSegmentsAsync(
        ChannelReader<AudioSegment> reader,
        IAudioTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        await foreach (AudioSegment segment in reader.ReadAllAsync(cancellationToken))
        {
            reporter.Report(AppEvent.ProcessingStarted());
            await PublishSafelyAsync(outputs, TranslationUpdate.Typing(true), reporter, cancellationToken);

            try
            {
                string translation = await translator.TranslateAsync(segment, cancellationToken);
                reporter.Report(AppEvent.TranslationCompleted(translation));
                await PublishSafelyAsync(
                    outputs,
                    TranslationUpdate.Translated(translation),
                    reporter,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                reporter.Report(AppEvent.ApiError(exception.Message));
            }
            finally
            {
                await PublishSafelyAsync(
                    outputs,
                    TranslationUpdate.Typing(false),
                    reporter,
                    CancellationToken.None);
                reporter.Report(AppEvent.ProcessingCompleted());
                reporter.Report(AppEvent.Listening());
            }
        }
    }

    private static async Task PublishSafelyAsync(
        IReadOnlyList<IOutputSink> outputs,
        TranslationUpdate update,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        foreach (IOutputSink output in outputs)
        {
            try
            {
                await output.PublishAsync(update, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                reporter.Report(AppEvent.OutputError(output.Name, exception.Message));
            }
        }
    }

    private static Task EnsureTypingStoppedAsync(
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter) =>
        PublishSafelyAsync(outputs, TranslationUpdate.Typing(false), reporter, CancellationToken.None);
}
