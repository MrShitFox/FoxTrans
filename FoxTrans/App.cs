using System.Threading.Channels;

public enum AppEventKind
{
    Listening,
    SpeechStarted,
    SegmentCompleted,
    ProcessingStarted,
    ProcessingCompleted,
    TranscriptionStarted,
    TranscriptionCompleted,
    TextTranslationStarted,
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
    VoxtralTransportPreview,
    VoxtralHealthChecked,
    VoxtralConnecting,
    VoxtralSessionStarted,
    VoxtralTranscriptUpdated,
    VoxtralWarning,
    VoxtralSessionCancelled,
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
    public static AppEvent TranscriptionStarted() => new(AppEventKind.TranscriptionStarted);
    public static AppEvent TranscriptionCompleted(string sourceText) => new(AppEventKind.TranscriptionCompleted, sourceText);
    public static AppEvent TextTranslationStarted() => new(AppEventKind.TextTranslationStarted);
    public static AppEvent TranslationCompleted(string translation) =>
        new(AppEventKind.TranslationCompleted, translation);
    public static AppEvent ShortPhraseIgnored(TimeSpan duration) =>
        new(AppEventKind.ShortPhraseIgnored, Duration: duration);
    public static AppEvent ApiError(string message) => new(AppEventKind.ApiError, message);
    public static AppEvent ProviderError(string operation, string message) =>
        new(AppEventKind.ApiError, message.StartsWith(operation + ":", StringComparison.OrdinalIgnoreCase) ? message : $"{operation}: {message}");
    public static AppEvent OutputError(string output, string message) =>
        new(AppEventKind.OutputError, $"{output}: {message}");
    public static AppEvent QueueOverflow(string message) => new(AppEventKind.QueueOverflow, message);
    public static AppEvent ConfigCreated(string path) => new(AppEventKind.ConfigCreated, path);
    public static AppEvent ConfigMigrated(string path) => new(AppEventKind.ConfigMigrated, path);
    public static AppEvent ConfigWarning(string message) => new(AppEventKind.ConfigWarning, message);
    public static AppEvent ConfigError(string message) => new(AppEventKind.ConfigError, message);
    public static AppEvent UnsupportedValidPipeline(string message) => new(AppEventKind.UnsupportedValidPipeline, message);
    public static AppEvent VoxtralTransportPreview() => new(
        AppEventKind.VoxtralTransportPreview,
        "VoxtralFox transport preview is active. Realtime translation and configured outputs are not active in this beta session.");
    public static AppEvent VoxtralHealthChecked(VoxtralHealthInfo health) => new(
        AppEventKind.VoxtralHealthChecked,
        $"Server {health.ServerVersion}; model {health.Model}; delay capabilities {string.Join(", ", health.SupportedTranscriptionDelayMs)} ms.");
    public static AppEvent VoxtralConnecting(Uri endpoint) => new(
        AppEventKind.VoxtralConnecting,
        $"Connecting to {endpoint.Host}:{endpoint.Port}.");
    public static AppEvent VoxtralSessionStarted(StreamingSessionStarted session) => new(
        AppEventKind.VoxtralSessionStarted,
        $"Session {ShortId(session.SessionId)}; model {session.Model}; protocol {session.ProtocolVersion}; delay {session.TranscriptionDelayMs} ms; connection {session.ConnectionGeneration}.");
    public static AppEvent VoxtralTranscriptUpdated(StreamingPartialTranscript transcript) => new(
        AppEventKind.VoxtralTranscriptUpdated,
        transcript.Text);
    public static AppEvent VoxtralWarning(StreamingServerWarning warning) => new(
        AppEventKind.VoxtralWarning,
        $"{warning.Code}: {warning.Message}" +
        (warning.BacklogMs is null ? "" : $" (backlog {warning.BacklogMs:F0} ms)"));
    public static AppEvent VoxtralSessionCancelled() => new(
        AppEventKind.VoxtralSessionCancelled,
        "The persistent VoxtralFox session was cancelled.");
    public static AppEvent Stopped() => new(AppEventKind.Stopped);
    public static AppEvent FatalError(string message) => new(AppEventKind.FatalError, message);
    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12] + "…";
}

public interface IAppReporter
{
    void Report(AppEvent appEvent);
}

public sealed record DirectAudioPipelineOptions(int CompletedSegmentCapacity = 4);
public sealed record BatchTranscriptionPipelineOptions(int CompletedSegmentCapacity = 4);

public static class FoxTransApp
{
    public static async Task RunRealtimeTranscriptionPreviewAsync(
        IAudioSource audioSource,
        IStreamingTranscriber transcriber,
        IAppReporter reporter,
        CancellationToken cancellationToken = default)
    {
        var required = new AudioFormat(16000, 16, 1);
        if (audioSource.Format != required)
        {
            throw new VoxtralFoxException(
                "unsupported_audio_format",
                $"VoxtralFox requires mono 16000 Hz signed PCM16LE; received {audioSource.Format.SampleRate} Hz, {audioSource.Format.BitsPerSample}-bit, {audioSource.Format.Channels} channel(s).");
        }

        reporter.Report(AppEvent.VoxtralTransportPreview());
        reporter.Report(AppEvent.Listening());
        try
        {
            await foreach (StreamingTranscriptionEvent appEvent in transcriber
                .TranscribeAsync(audioSource.ReadFramesAsync(cancellationToken), cancellationToken)
                .WithCancellation(cancellationToken))
            {
                switch (appEvent)
                {
                    case StreamingSessionStarted started:
                        reporter.Report(AppEvent.VoxtralSessionStarted(started));
                        break;
                    case StreamingPartialTranscript partial:
                        reporter.Report(AppEvent.VoxtralTranscriptUpdated(partial));
                        break;
                    case StreamingServerWarning warning:
                        reporter.Report(AppEvent.VoxtralWarning(warning));
                        break;
                    case StreamingSessionCancelled:
                        reporter.Report(AppEvent.VoxtralSessionCancelled());
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reporter.Report(AppEvent.VoxtralSessionCancelled());
            throw;
        }
        finally
        {
            reporter.Report(AppEvent.Stopped());
        }
    }

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
        await RunSegmentedPipelineAsync(audioSource, segmenter, async (segment, token) =>
        {
            reporter.Report(AppEvent.ProcessingStarted());
            string translation = await translator.TranslateAsync(segment, token);
            reporter.Report(AppEvent.TranslationCompleted(translation));
            return translation;
        }, outputs, reporter, options.CompletedSegmentCapacity, cancellationToken);
    }

    public static async Task RunBatchTranscriptionPipelineAsync(
        IAudioSource audioSource,
        IAudioSegmenter segmenter,
        IBatchTranscriber transcriber,
        ITextTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        BatchTranscriptionPipelineOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BatchTranscriptionPipelineOptions();
        await RunSegmentedPipelineAsync(audioSource, segmenter, async (segment, token) =>
        {
            reporter.Report(AppEvent.TranscriptionStarted());
            string transcript = await transcriber.TranscribeAsync(segment, token);
            reporter.Report(AppEvent.TranscriptionCompleted(transcript));
            reporter.Report(AppEvent.TextTranslationStarted());
            string translation = await translator.TranslateAsync(transcript, token);
            reporter.Report(AppEvent.TranslationCompleted(translation));
            return translation;
        }, outputs, reporter, options.CompletedSegmentCapacity, cancellationToken);
    }

    private static async Task RunSegmentedPipelineAsync(
        IAudioSource audioSource,
        IAudioSegmenter segmenter,
        Func<AudioSegment, CancellationToken, Task<string>> process,
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        int completedSegmentCapacity,
        CancellationToken cancellationToken)
    {
        if (completedSegmentCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(completedSegmentCapacity));
        var completedSegments = Channel.CreateBounded<AudioSegment>(
            new BoundedChannelOptions(completedSegmentCapacity)
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
            process,
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
        Func<AudioSegment, CancellationToken, Task<string>> process,
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        await foreach (AudioSegment segment in reader.ReadAllAsync(cancellationToken))
        {
            await PublishSafelyAsync(outputs, TranslationUpdate.Typing(true), reporter, cancellationToken);

            try
            {
                string translation = await process(segment, cancellationToken);
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
                if (exception is OpenAiProviderException provider)
                    reporter.Report(AppEvent.ProviderError(provider.Operation, provider.Message));
                else reporter.Report(AppEvent.ApiError(exception.Message));
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
