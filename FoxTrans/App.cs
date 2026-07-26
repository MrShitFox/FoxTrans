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
    VoxtralHealthChecked,
    VoxtralConnecting,
    RealtimeAudioRouteActivated,
    VoxtralSessionStarted,
    VoxtralConnectionLost,
    VoxtralReconnectScheduled,
    VoxtralReconnectAttempt,
    VoxtralReconnected,
    RealtimeAudioGapStarted,
    RealtimeAudioGapCompleted,
    VoxtralWarning,
    VoxtralSessionCancelled,
    LogicalUtteranceStarted,
    LogicalUtteranceUpdated,
    LogicalUtteranceSettled,
    TranslationRequestStarted,
    TranslationRequestCoalesced,
    RealtimeTranslationPublished,
    RealtimeTranslationFailed,
    RealtimeTranslationCompleted,
    TranscriptEpochResynchronized,
    Stopped,
    FatalError
}

public sealed record AppEvent(
    AppEventKind Kind,
    string? Message = null,
    TimeSpan? Duration = null,
    RealtimeTranslationTelemetry? TranslationTelemetry = null)
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
    public static AppEvent VoxtralHealthChecked(VoxtralHealthInfo health) => new(
        AppEventKind.VoxtralHealthChecked,
        $"Server {health.ServerVersion}; model {health.Model}; delay capabilities {string.Join(", ", health.SupportedTranscriptionDelayMs)} ms.");
    public static AppEvent VoxtralConnecting(Uri endpoint) => new(
        AppEventKind.VoxtralConnecting,
        $"Connecting to {endpoint.Host}:{endpoint.Port}.");
    public static AppEvent RealtimeAudioRouteActivated(RealtimeAudioRouteActivated route) => new(
        AppEventKind.RealtimeAudioRouteActivated,
        $"Connection {route.ConnectionGeneration} audio ready after {route.SessionCreatedWait.TotalMilliseconds:F0} ms; " +
        $"{route.NormalizedFrameBytes}-byte/{RealtimeAudioPump.NormalizedFrameDurationMilliseconds} ms frames; " +
        $"{route.QueueCapacityDuration.TotalSeconds:F1}-second route capacity ({route.QueueCapacityBytes} PCM bytes).");
    public static AppEvent VoxtralSessionStarted(StreamingSessionStarted session) => new(
        AppEventKind.VoxtralSessionStarted,
        $"Session {ShortId(session.SessionId)}; model {session.Model}; protocol {session.ProtocolVersion}; delay {session.TranscriptionDelayMs} ms; connection {session.ConnectionGeneration}.");
    public static AppEvent VoxtralConnectionLost(VoxtralConnectionLost lost) => new(
        AppEventKind.VoxtralConnectionLost,
        $"{lost.Code}: {lost.Message}");
    public static AppEvent VoxtralReconnectScheduled(VoxtralReconnectScheduled scheduled) => new(
        AppEventKind.VoxtralReconnectScheduled,
        $"Reconnect attempt {scheduled.Attempt} in {scheduled.Delay.TotalSeconds:F0} second(s).",
        scheduled.Delay);
    public static AppEvent VoxtralReconnectAttempt(VoxtralReconnectAttempt attempt) => new(
        AppEventKind.VoxtralReconnectAttempt,
        $"Starting reconnect attempt {attempt.Attempt}.");
    public static AppEvent VoxtralReconnected(VoxtralReconnected reconnected) => new(
        AppEventKind.VoxtralReconnected,
        $"Voxtral reconnected; connection generation {reconnected.ConnectionGeneration}.");
    public static AppEvent RealtimeAudioGapStarted() => new(
        AppEventKind.RealtimeAudioGapStarted,
        "Audio is not being transcribed during reconnect.");
    public static AppEvent RealtimeAudioGapCompleted(RealtimeAudioGapCompleted completed) => new(
        AppEventKind.RealtimeAudioGapCompleted,
        $"Reconnected. Audio gap: approximately {completed.ApproximateDuration.TotalSeconds:F1} seconds " +
        $"({completed.LostBytes} PCM bytes were not transmitted).",
        completed.ApproximateDuration);
    public static AppEvent VoxtralWarning(StreamingServerWarning warning) => new(
        AppEventKind.VoxtralWarning,
        $"{warning.Code}: {warning.Message}" +
        (warning.BacklogMs is null ? "" : $" (backlog {warning.BacklogMs:F0} ms)"));
    public static AppEvent VoxtralSessionCancelled() => new(
        AppEventKind.VoxtralSessionCancelled,
        "The persistent VoxtralFox session was cancelled.");
    public static AppEvent LogicalUtteranceStarted(TranslationCandidate candidate) => new(
        AppEventKind.LogicalUtteranceStarted,
        $"Utterance {candidate.UtteranceId}: {candidate.SourceText}");
    public static AppEvent LogicalUtteranceUpdated(TranslationCandidate candidate) => new(
        AppEventKind.LogicalUtteranceUpdated,
        candidate.SourceText);
    public static AppEvent LogicalUtteranceSettled(TranslationCandidate candidate) => new(
        AppEventKind.LogicalUtteranceSettled,
        $"Utterance {candidate.UtteranceId} settled.");
    public static AppEvent RealtimeTranslationStarted(
        TranslationCandidate candidate,
        RealtimeSchedulingDecision decision,
        TimeSpan candidateAge) => new(
        AppEventKind.TranslationRequestStarted,
        $"Translation e{candidate.TranscriptEpoch}/u{candidate.UtteranceId}/r{candidate.Revision} " +
        $"started ({decision}); candidate age {candidateAge.TotalMilliseconds:F0} ms.");
    public static AppEvent RealtimeTranslationCoalesced(TranslationCandidate candidate) => new(
        AppEventKind.TranslationRequestCoalesced,
        $"Retained newest utterance {candidate.UtteranceId}, revision {candidate.Revision}.");
    public static AppEvent RealtimeTranslationPublished(
        TranslationCandidate candidate,
        string translation) => new(
        AppEventKind.RealtimeTranslationPublished,
        translation);
    public static AppEvent RealtimeTranslationFailed(string operation, string message) => new(
        AppEventKind.RealtimeTranslationFailed,
        message.StartsWith(operation + ":", StringComparison.OrdinalIgnoreCase)
            ? message
            : $"{operation}: {message}");
    public static AppEvent RealtimeTranslationCompleted(
        RealtimeTranslationTelemetry telemetry) => new(
        AppEventKind.RealtimeTranslationCompleted,
        FormatTranslationCompletion(telemetry),
        telemetry.TranslationDuration,
        telemetry);
    public static AppEvent TranscriptEpochResynchronized(string warning) => new(
        AppEventKind.TranscriptEpochResynchronized,
        warning);
    public static AppEvent Stopped() => new(AppEventKind.Stopped);
    public static AppEvent FatalError(string message) => new(AppEventKind.FatalError, message);
    private static string FormatTranslationCompletion(
        RealtimeTranslationTelemetry telemetry)
    {
        string disposition = telemetry.Disposition switch
        {
            TranslationCompletionDisposition.PublishedIntermediate =>
                "published as intermediate",
            TranslationCompletionDisposition.PublishedFinal =>
                "published as final",
            TranslationCompletionDisposition.DiscardedOldUtterance =>
                "discarded: utterance changed",
            TranslationCompletionDisposition.DiscardedOldEpoch =>
                "discarded: transcript epoch changed",
            TranslationCompletionDisposition.DiscardedInvalidLifecycle =>
                "discarded: scheduler lifecycle changed",
            TranslationCompletionDisposition.DiscardedOlderThanAcceptedWatermark =>
                "discarded: a newer translation was already accepted",
            TranslationCompletionDisposition.Cancelled => "cancelled",
            TranslationCompletionDisposition.Failed => "failed",
            _ => telemetry.Disposition.ToString()
        };
        string current = telemetry.CurrentRevision is long revision
            ? $"r{revision}"
            : "none";
        return
            $"Translation e{telemetry.TranscriptEpoch}/u{telemetry.UtteranceId}/" +
            $"r{telemetry.RequestedRevision} {disposition} in " +
            $"{telemetry.TranslationDuration.TotalMilliseconds:F0} ms; " +
            $"current revision {current}; candidate age " +
            $"{telemetry.CandidateAge.TotalMilliseconds:F0} ms; " +
            $"reason {telemetry.SchedulingReason}; newer pending " +
            $"{(telemetry.NewerPendingCandidateExisted ? "yes" : "no")}.";
    }
    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12] + "…";
}

public interface IAppReporter
{
    void Report(AppEvent appEvent);
}

public sealed record DirectAudioPipelineOptions(int CompletedSegmentCapacity = 4);
public sealed record BatchTranscriptionPipelineOptions(int CompletedSegmentCapacity = 4);
public sealed record RealtimePipelineTiming(
    Func<DateTimeOffset> GetUtcNow,
    Func<TimeSpan, CancellationToken, Task> Delay)
{
    public static RealtimePipelineTiming System { get; } = new(
        () => DateTimeOffset.UtcNow,
        Task.Delay);
}

public static class FoxTransApp
{
    public static Task RunRealtimeTranscriptionPipelineAsync(
        IAudioSource audioSource,
        IStreamingTranscriber transcriber,
        ITextTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        ResolvedRealtimeSettings realtimeSettings,
        IAppReporter reporter,
        CancellationToken cancellationToken = default) =>
        RunRealtimeTranscriptionPipelineAsync(
            audioSource,
            transcriber,
            translator,
            outputs,
            realtimeSettings,
            reporter,
            RealtimePipelineTiming.System,
            cancellationToken);

    public static async Task RunRealtimeTranscriptionPipelineAsync(
        IAudioSource audioSource,
        IStreamingTranscriber transcriber,
        ITextTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        ResolvedRealtimeSettings realtimeSettings,
        IAppReporter reporter,
        RealtimePipelineTiming timing,
        CancellationToken cancellationToken = default)
    {
        var required = new AudioFormat(16000, 16, 1);
        if (audioSource.Format != required)
        {
            throw new VoxtralFoxException(
                "unsupported_audio_format",
                $"VoxtralFox requires mono 16000 Hz signed PCM16LE; received {audioSource.Format.SampleRate} Hz, {audioSource.Format.BitsPerSample}-bit, {audioSource.Format.Channels} channel(s).");
        }

        LogicalUtteranceState trackerState = LogicalUtteranceState.Initial;
        using var trackerGate = new SemaphoreSlim(1, 1);
        using var timerCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var scheduler = new RealtimeTranslationScheduler(
            translator,
            outputs,
            realtimeSettings,
            reporter,
            cancellationToken,
            timing.GetUtcNow);

        async Task ApplyTransitionAsync(
            UtteranceTransition transition,
            DateTimeOffset now,
            CancellationToken token)
        {
            if (transition.Warning is not null)
                reporter.Report(AppEvent.TranscriptEpochResynchronized(transition.Warning));
            if (transition.TranscriptEpochChanged && transition.Candidate is null)
            {
                await scheduler.InvalidateTranscriptEpochAsync(
                    transition.State.TranscriptEpoch,
                    token);
            }
            if (transition.Candidate is null)
                return;
            switch (transition.Kind)
            {
                case UtteranceTransitionKind.Started:
                case UtteranceTransitionKind.Resynchronized:
                    reporter.Report(AppEvent.LogicalUtteranceStarted(transition.Candidate));
                    break;
                case UtteranceTransitionKind.Updated:
                case UtteranceTransitionKind.LateAmendment:
                    reporter.Report(AppEvent.LogicalUtteranceUpdated(transition.Candidate));
                    break;
                case UtteranceTransitionKind.Settled:
                    reporter.Report(AppEvent.LogicalUtteranceSettled(transition.Candidate));
                    break;
            }
            await scheduler.SubmitAsync(transition.Candidate, now, token);
        }

        async Task TimerLoopAsync()
        {
            while (true)
            {
                await timing.Delay(
                    TimeSpan.FromMilliseconds(75),
                    timerCancellation.Token);
                DateTimeOffset now = timing.GetUtcNow();
                UtteranceTransition transition;
                await trackerGate.WaitAsync(timerCancellation.Token);
                try
                {
                    transition = UtteranceTracking.CheckInactivity(
                        trackerState,
                        now,
                        realtimeSettings.NewUtteranceAfterMs,
                        realtimeSettings.MaxSourceCharacters);
                    trackerState = transition.State;
                }
                finally
                {
                    trackerGate.Release();
                }
                await ApplyTransitionAsync(
                    transition,
                    now,
                    timerCancellation.Token);
                await scheduler.TickAsync(now, timerCancellation.Token);
            }
        }

        reporter.Report(AppEvent.Listening());
        Task timer = TimerLoopAsync();
        try
        {
            await foreach (StreamingTranscriptionEvent appEvent in transcriber
                .TranscribeAsync(audioSource.ReadFramesAsync(cancellationToken), cancellationToken)
                .WithCancellation(cancellationToken))
            {
                switch (appEvent)
                {
                    case RealtimeAudioRouteActivated route:
                        reporter.Report(AppEvent.RealtimeAudioRouteActivated(route));
                        break;
                    case StreamingSessionStarted started:
                        reporter.Report(AppEvent.VoxtralSessionStarted(started));
                        break;
                    case VoxtralConnectionLost lost:
                    {
                        reporter.Report(AppEvent.VoxtralConnectionLost(lost));
                        await trackerGate.WaitAsync(cancellationToken);
                        long epoch;
                        try
                        {
                            trackerState = UtteranceTracking.StartNewTranscriptEpoch(trackerState);
                            epoch = trackerState.TranscriptEpoch;
                        }
                        finally
                        {
                            trackerGate.Release();
                        }
                        await scheduler.InvalidateTranscriptEpochAsync(epoch, cancellationToken);
                        break;
                    }
                    case VoxtralReconnectScheduled scheduled:
                        reporter.Report(AppEvent.VoxtralReconnectScheduled(scheduled));
                        break;
                    case VoxtralReconnectAttempt attempt:
                        reporter.Report(AppEvent.VoxtralReconnectAttempt(attempt));
                        break;
                    case VoxtralReconnected reconnected:
                        reporter.Report(AppEvent.VoxtralReconnected(reconnected));
                        break;
                    case RealtimeAudioGapStarted:
                        reporter.Report(AppEvent.RealtimeAudioGapStarted());
                        break;
                    case RealtimeAudioGapCompleted completed:
                        reporter.Report(AppEvent.RealtimeAudioGapCompleted(completed));
                        break;
                    case StreamingPartialTranscript partial:
                    {
                        DateTimeOffset now = timing.GetUtcNow();
                        UtteranceTransition transition;
                        await trackerGate.WaitAsync(cancellationToken);
                        try
                        {
                            transition = UtteranceTracking.ReducePartial(
                                trackerState,
                                partial,
                                now,
                                realtimeSettings.MaxSourceCharacters);
                            trackerState = transition.State;
                        }
                        finally
                        {
                            trackerGate.Release();
                        }
                        await ApplyTransitionAsync(
                            transition,
                            now,
                            cancellationToken);
                        break;
                    }
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
            timerCancellation.Cancel();
            try
            {
                await timer;
            }
            catch (OperationCanceledException) when (timerCancellation.IsCancellationRequested)
            {
            }
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
