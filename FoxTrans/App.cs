using System.Threading.Channels;
using System.Diagnostics;

public enum AppEventKind
{
    RuntimeStateChanged,
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
    RealtimeTranslationAccepted,
    RealtimeTranslationFailed,
    RealtimeTranslationCompleted,
    RealtimeOutputTranslationCoalesced,
    RealtimeOutputDiscarded,
    RealtimeOutputTimedOut,
    RealtimeOutputQuarantined,
    RealtimeOutputControlOverflow,
    TranscriptEpochResynchronized,
    Telemetry,
    OutputDelivery,
    Stopped,
    FatalError
}

public sealed record AppEvent(
    AppEventKind Kind,
    string? Message = null,
    TimeSpan? Duration = null,
    RealtimeTranslationTelemetry? TranslationTelemetry = null,
    RealtimeOutputTelemetry? OutputTelemetry = null,
    PipelineTelemetry? Telemetry = null)
{
    public static AppEvent RuntimeStateChanged(
        RuntimeState state,
        long generation,
        string? failureCategory = null) =>
        new(
            AppEventKind.RuntimeStateChanged,
            state.ToString(),
            Telemetry: new RuntimeLifecycleTelemetry(
                state,
                generation,
                failureCategory));
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
        $"Session {ShortId(session.SessionId)}; model {session.Model}; protocol {session.ProtocolVersion}; delay {session.TranscriptionDelayMs} ms; connection {session.ConnectionGeneration}.",
        Telemetry: new RealtimeIdentityTelemetry(
            0, 0, 0,
            ConnectionGeneration: session.ConnectionGeneration,
            SessionId: ShortId(session.SessionId),
            ProtocolVersion: session.ProtocolVersion));
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
        $"Utterance {candidate.UtteranceId}: {candidate.SourceText}",
        Telemetry: Identity(candidate));
    public static AppEvent LogicalUtteranceUpdated(TranslationCandidate candidate) => new(
        AppEventKind.LogicalUtteranceUpdated,
        candidate.SourceText,
        Telemetry: Identity(candidate));
    public static AppEvent LogicalUtteranceSettled(TranslationCandidate candidate) => new(
        AppEventKind.LogicalUtteranceSettled,
        $"Utterance {candidate.UtteranceId} settled.",
        Telemetry: Identity(candidate));
    public static AppEvent RealtimeTranslationStarted(
        TranslationCandidate candidate,
        RealtimeSchedulingDecision decision,
        TimeSpan candidateAge) => new(
        AppEventKind.TranslationRequestStarted,
        $"Translation e{candidate.TranscriptEpoch}/u{candidate.UtteranceId}/r{candidate.Revision} " +
        $"started ({decision}); candidate age {candidateAge.TotalMilliseconds:F0} ms.",
        Telemetry: Identity(candidate) with { RequestedRevision = candidate.Revision });
    public static AppEvent RealtimeTranslationCoalesced(TranslationCandidate candidate) => new(
        AppEventKind.TranslationRequestCoalesced,
        $"Retained newest utterance {candidate.UtteranceId}, revision {candidate.Revision}.");
    public static AppEvent RealtimeTranslationAccepted(
        TranslationCandidate candidate,
        string translation) => new(
        AppEventKind.RealtimeTranslationAccepted,
        translation,
        Telemetry: Identity(candidate));
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
    public static AppEvent RealtimeOutput(RealtimeOutputTelemetry telemetry) => new(
        telemetry.Kind switch
        {
            RealtimeOutputTelemetryKind.Pending or
            RealtimeOutputTelemetryKind.Delivered =>
                AppEventKind.OutputDelivery,
            RealtimeOutputTelemetryKind.TranslationCoalesced =>
                AppEventKind.RealtimeOutputTranslationCoalesced,
            RealtimeOutputTelemetryKind.DiscardedOldEpoch or
            RealtimeOutputTelemetryKind.DiscardedOldUtterance =>
                AppEventKind.RealtimeOutputDiscarded,
            RealtimeOutputTelemetryKind.TimedOut =>
                AppEventKind.RealtimeOutputTimedOut,
            RealtimeOutputTelemetryKind.Quarantined =>
                AppEventKind.RealtimeOutputQuarantined,
            RealtimeOutputTelemetryKind.ControlOverflow =>
                AppEventKind.RealtimeOutputControlOverflow,
            _ => throw new ArgumentOutOfRangeException(nameof(telemetry))
        },
        FormatRealtimeOutput(telemetry),
        telemetry.Duration ?? telemetry.Timeout,
        OutputTelemetry: telemetry,
        Telemetry: telemetry.Kind is RealtimeOutputTelemetryKind.Pending or
            RealtimeOutputTelemetryKind.Delivered
            ? new OutputDeliveryTelemetry(
                new($"output:{telemetry.OutputIndex + 1}"),
                telemetry.OutputName,
                telemetry.OperationSequence,
                telemetry.UpdateKind,
                telemetry.Kind == RealtimeOutputTelemetryKind.Pending
                    ? OutputDeliveryPhase.Pending
                    : OutputDeliveryPhase.Delivered,
                telemetry.Duration)
            : null);
    public static AppEvent TranscriptEpochResynchronized(string warning) => new(
        AppEventKind.TranscriptEpochResynchronized,
        warning);
    public static AppEvent RuntimeTelemetry(PipelineTelemetry telemetry) => new(
        AppEventKind.Telemetry,
        Telemetry: telemetry);
    public static AppEvent OutputDelivered(OutputDeliveryTelemetry telemetry) => new(
        AppEventKind.OutputDelivery,
        $"{telemetry.OutputName} {telemetry.UpdateKind.ToString().ToLowerInvariant()} delivered.",
        telemetry.Duration,
        Telemetry: telemetry);
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
    private static string FormatRealtimeOutput(RealtimeOutputTelemetry telemetry)
    {
        string identity =
            $"{telemetry.OutputName} e{telemetry.TranscriptEpoch}/" +
            $"u{telemetry.UtteranceId}/r{telemetry.Revision}";
        return telemetry.Kind switch
        {
            RealtimeOutputTelemetryKind.Pending =>
                $"{identity}: accepted by output scheduler.",
            RealtimeOutputTelemetryKind.Delivered =>
                $"{identity}: delivered in {telemetry.Duration?.TotalMilliseconds:F0} ms.",
            RealtimeOutputTelemetryKind.TranslationCoalesced =>
                $"{identity}: coalesced {telemetry.CoalescedCount} older translation update(s).",
            RealtimeOutputTelemetryKind.DiscardedOldUtterance =>
                $"{identity}: discarded after utterance invalidation.",
            RealtimeOutputTelemetryKind.DiscardedOldEpoch =>
                $"{identity}: discarded after transcript epoch invalidation.",
            RealtimeOutputTelemetryKind.TimedOut =>
                $"{identity}: publication timed out after {telemetry.Timeout?.TotalSeconds:F1} seconds.",
            RealtimeOutputTelemetryKind.Quarantined =>
                $"{identity}: output quarantined because publication ignored cancellation.",
            RealtimeOutputTelemetryKind.ControlOverflow =>
                $"{identity}: output control queue exceeded its hard bound.",
            _ => identity
        };
    }
    private static string ShortId(string value) => value.Length <= 12 ? value : value[..12] + "…";
    private static RealtimeIdentityTelemetry Identity(TranslationCandidate candidate) =>
        new(
            candidate.TranscriptEpoch,
            candidate.UtteranceId,
            candidate.Revision);
}

public interface IAppReporter
{
    void Report(AppEvent appEvent);
}

public sealed record DirectAudioPipelineOptions(int CompletedSegmentCapacity = 4);
public sealed record BatchTranscriptionPipelineOptions(int CompletedSegmentCapacity = 4);

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
            TimeProvider.System,
            cancellationToken);

    public static async Task RunRealtimeTranscriptionPipelineAsync(
        IAudioSource audioSource,
        IStreamingTranscriber transcriber,
        ITextTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        ResolvedRealtimeSettings realtimeSettings,
        IAppReporter reporter,
        TimeProvider timing,
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
                await Task.Delay(
                    TimeSpan.FromMilliseconds(75),
                    timing,
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
        long operationId = 0;
        await RunSegmentedPipelineAsync(audioSource, segmenter, async (segment, token) =>
        {
            long work = Interlocked.Increment(ref operationId);
            reporter.Report(AppEvent.ProcessingStarted());
            reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                new("audio-llm"), work, StageOperationPhase.Started,
                AudioDuration: segment.Duration, Detail: "packing WAV and requesting audio model")));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                string translation = await translator.TranslateAsync(segment, token);
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                    new("audio-llm"), work, StageOperationPhase.Completed,
                    stopwatch.Elapsed, segment.Duration, translation.Length)));
                reporter.Report(AppEvent.TranslationCompleted(translation) with
                {
                    Duration = stopwatch.Elapsed
                });
                return translation;
            }
            catch
            {
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                    new("audio-llm"), work, StageOperationPhase.Failed,
                    stopwatch.Elapsed, segment.Duration)));
                throw;
            }
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
        long operationId = 0;
        await RunSegmentedPipelineAsync(audioSource, segmenter, async (segment, token) =>
        {
            long work = Interlocked.Increment(ref operationId);
            var stopwatch = Stopwatch.StartNew();
            reporter.Report(AppEvent.TranscriptionStarted());
            reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                new("batch-stt"), work, StageOperationPhase.Started,
                AudioDuration: segment.Duration)));
            string transcript;
            try
            {
                transcript = await transcriber.TranscribeAsync(segment, token);
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                    new("batch-stt"), work, StageOperationPhase.Completed,
                    stopwatch.Elapsed, segment.Duration, transcript.Length)));
                reporter.Report(AppEvent.TranscriptionCompleted(transcript) with
                {
                    Duration = stopwatch.Elapsed
                });
            }
            catch
            {
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                    new("batch-stt"), work, StageOperationPhase.Failed,
                    stopwatch.Elapsed, segment.Duration)));
                throw;
            }

            stopwatch.Restart();
            reporter.Report(AppEvent.TextTranslationStarted());
            reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                new("text-translation"), work, StageOperationPhase.Started)));
            try
            {
                string translation = await translator.TranslateAsync(transcript, token);
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                    new("text-translation"), work, StageOperationPhase.Completed,
                    stopwatch.Elapsed, ResultLength: translation.Length)));
                reporter.Report(AppEvent.TranslationCompleted(translation) with
                {
                    Duration = stopwatch.Elapsed
                });
                return translation;
            }
            catch
            {
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new StageOperationTelemetry(
                    new("text-translation"), work, StageOperationPhase.Failed,
                    stopwatch.Elapsed)));
                throw;
            }
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
        var queue = new SegmentQueueBookkeeping(completedSegmentCapacity, reporter);

        queue.ReportInitial();
        reporter.Report(AppEvent.Listening());

        Task segmentationWorker = ProduceSegmentsAsync(
            audioSource,
            segmenter,
            completedSegments.Writer,
            queue,
            reporter,
            cancellationToken);
        Task translationWorker = ConsumeSegmentsAsync(
            completedSegments.Reader,
            queue,
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
            queue.Complete();
            await EnsureTypingStoppedAsync(outputs, reporter);
            reporter.Report(AppEvent.Stopped());
        }
    }

    private static async Task ProduceSegmentsAsync(
        IAudioSource source,
        IAudioSegmenter segmenter,
        ChannelWriter<AudioSegment> writer,
        SegmentQueueBookkeeping queue,
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
                        if (!queue.TryEnqueue(writer, update.Segment))
                        {
                            queue.Backpressure();
                            reporter.Report(AppEvent.QueueOverflow(
                                "Completed-segment queue is full; capture is applying backpressure."));
                            await queue.EnqueueAsync(
                                writer,
                                update.Segment,
                                cancellationToken);
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
        SegmentQueueBookkeeping queue,
        Func<AudioSegment, CancellationToken, Task<string>> process,
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            while (queue.TryDequeue(reader, out AudioSegment? dequeued))
            {
                AudioSegment segment = dequeued!;
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
    }

    private static async Task PublishSafelyAsync(
        IReadOnlyList<IOutputSink> outputs,
        TranslationUpdate update,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < outputs.Count; index++)
        {
            IOutputSink output = outputs[index];
            long operationId = OutputOperationIds.Next();
            var nodeId = new PipelineNodeId($"output:{index + 1}");
            reporter.Report(AppEvent.RuntimeTelemetry(new OutputDeliveryTelemetry(
                nodeId, output.Name, operationId, update.Kind, OutputDeliveryPhase.Pending)));
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await output.PublishAsync(update, cancellationToken);
                stopwatch.Stop();
                reporter.Report(AppEvent.OutputDelivered(new OutputDeliveryTelemetry(
                    nodeId, output.Name, operationId, update.Kind,
                    OutputDeliveryPhase.Delivered, stopwatch.Elapsed)));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                reporter.Report(AppEvent.RuntimeTelemetry(new OutputDeliveryTelemetry(
                    nodeId, output.Name, operationId, update.Kind,
                    OutputDeliveryPhase.Failed, stopwatch.Elapsed)));
                reporter.Report(AppEvent.OutputError(output.Name, exception.Message));
            }
        }
    }

    private static Task EnsureTypingStoppedAsync(
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter) =>
        PublishSafelyAsync(outputs, TranslationUpdate.Typing(false), reporter, CancellationToken.None);

    private static class OutputOperationIds
    {
        private static long _value;
        public static long Next() => Interlocked.Increment(ref _value);
    }

    private sealed class SegmentQueueBookkeeping(int capacity, IAppReporter reporter)
    {
        private readonly object _gate = new();
        private int _count;
        private long _produced;
        private long _consumed;

        public bool TryEnqueue(ChannelWriter<AudioSegment> writer, AudioSegment segment)
        {
            QueueTelemetry? telemetry = null;
            lock (_gate)
            {
                if (!writer.TryWrite(segment))
                    return false;
                _count = Math.Min(capacity, _count + 1);
                _produced = Add(_produced);
                telemetry = SnapshotLocked(false);
            }
            reporter.Report(AppEvent.RuntimeTelemetry(telemetry));
            return true;
        }

        public async ValueTask EnqueueAsync(
            ChannelWriter<AudioSegment> writer,
            AudioSegment segment,
            CancellationToken cancellationToken)
        {
            while (await writer.WaitToWriteAsync(cancellationToken))
            {
                if (TryEnqueue(writer, segment))
                    return;
            }
            throw new ChannelClosedException();
        }

        public bool TryDequeue(
            ChannelReader<AudioSegment> reader,
            out AudioSegment? segment)
        {
            QueueTelemetry? telemetry = null;
            lock (_gate)
            {
                if (!reader.TryRead(out segment))
                    return false;
                _count = Math.Max(0, _count - 1);
                _consumed = Add(_consumed);
                telemetry = SnapshotLocked(false);
            }
            reporter.Report(AppEvent.RuntimeTelemetry(telemetry));
            return true;
        }

        public void Backpressure() =>
            reporter.Report(AppEvent.RuntimeTelemetry(Snapshot(true)));

        public void ReportInitial() =>
            reporter.Report(AppEvent.RuntimeTelemetry(Snapshot(false)));

        public void Complete()
        {
            lock (_gate)
                _count = 0;
            reporter.Report(AppEvent.RuntimeTelemetry(Snapshot(false)));
        }

        private QueueTelemetry Snapshot(bool backpressured) =>
            WithLock(() => SnapshotLocked(backpressured));

        private QueueTelemetry SnapshotLocked(bool backpressured) =>
            new(Math.Clamp(_count, 0, capacity), capacity,
                _produced, _consumed, backpressured);

        private T WithLock<T>(Func<T> action)
        {
            lock (_gate)
                return action();
        }

        private static long Add(long value) =>
            value == long.MaxValue ? value : value + 1;
    }
}
