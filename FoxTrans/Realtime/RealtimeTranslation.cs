using System.Globalization;
using System.Text;

public enum RealtimeSchedulingDecision
{
    Wait,
    MinimumChangedWords,
    Punctuation,
    MaximumInterval,
    Settled,
    Duplicate
}

public static class RealtimeTranslationPolicy
{
    public static RealtimeSchedulingDecision Decide(
        TranslationCandidate candidate,
        TranslationCandidate? lastRequested,
        DateTimeOffset? lastRequestedAt,
        DateTimeOffset now,
        ResolvedRealtimeSettings settings)
    {
        if (lastRequested is not null &&
            candidate.TranscriptEpoch == lastRequested.TranscriptEpoch &&
            candidate.UtteranceId == lastRequested.UtteranceId &&
            string.Equals(candidate.SourceText, lastRequested.SourceText, StringComparison.Ordinal))
        {
            return RealtimeSchedulingDecision.Duplicate;
        }

        if (candidate.IsSettled)
            return RealtimeSchedulingDecision.Settled;

        bool sameUtterance = lastRequested is not null &&
            candidate.TranscriptEpoch == lastRequested.TranscriptEpoch &&
            candidate.UtteranceId == lastRequested.UtteranceId;
        DateTimeOffset intervalStart = sameUtterance && lastRequestedAt is not null
            ? lastRequestedAt.Value
            : candidate.UtteranceStartedAt;
        TimeSpan elapsed = now - intervalStart;
        if (elapsed >= TimeSpan.FromMilliseconds(settings.MaximumIntervalMs))
            return RealtimeSchedulingDecision.MaximumInterval;
        if (elapsed < TimeSpan.FromMilliseconds(settings.MinimumIntervalMs))
            return RealtimeSchedulingDecision.Wait;

        string previous = sameUtterance ? lastRequested!.SourceText : "";
        if (HasNewTerminalPunctuation(previous, candidate.SourceText))
            return RealtimeSchedulingDecision.Punctuation;
        if (CountChangedWords(previous, candidate.SourceText) >= settings.MinimumChangedWords)
            return RealtimeSchedulingDecision.MinimumChangedWords;
        return RealtimeSchedulingDecision.Wait;
    }

    public static int CountChangedWords(string previous, string current)
    {
        string[] oldWords = LexicalUnits(previous);
        string[] newWords = LexicalUnits(current);
        int prefix = 0;
        while (prefix < oldWords.Length &&
               prefix < newWords.Length &&
               string.Equals(oldWords[prefix], newWords[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }
        return Math.Max(oldWords.Length - prefix, newWords.Length - prefix);
    }

    public static bool HasNewTerminalPunctuation(string previous, string current)
    {
        string trimmed = current.TrimEnd();
        if (trimmed.Length == 0)
            return false;
        int lastStart = StringInfo.ParseCombiningCharacters(trimmed)[^1];
        string last = trimmed[lastStart..];
        if (!IsClausePunctuation(last))
            return false;

        int common = CommonPrefixLength(previous, current);
        string changedTail = current[common..];
        return StringInfo.GetTextElementEnumerator(changedTail)
            .AsEnumerable()
            .Any(IsClausePunctuation);
    }

    public static bool IsNewer(TranslationCandidate left, TranslationCandidate right)
    {
        if (left.TranscriptEpoch != right.TranscriptEpoch)
            return left.TranscriptEpoch > right.TranscriptEpoch;
        if (left.UtteranceId != right.UtteranceId)
            return left.UtteranceId > right.UtteranceId;
        return left.Revision > right.Revision ||
            left.Revision == right.Revision && left.IsSettled && !right.IsSettled;
    }

    private static string[] LexicalUnits(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static int CommonPrefixLength(string left, string right)
    {
        int length = Math.Min(left.Length, right.Length);
        int index = 0;
        while (index < length && left[index] == right[index])
            index++;
        if (index > 0 &&
            index < left.Length &&
            index < right.Length &&
            char.IsLowSurrogate(left[index]) &&
            char.IsHighSurrogate(left[index - 1]))
        {
            index--;
        }
        return index;
    }

    private static bool IsClausePunctuation(string element)
    {
        Rune rune = element.EnumerateRunes().First();
        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation;
    }

    private static IEnumerable<string> AsEnumerable(
        this TextElementEnumerator enumerator)
    {
        while (enumerator.MoveNext())
            yield return enumerator.GetTextElement();
    }
}

public enum TranslationCompletionDisposition
{
    PublishedIntermediate,
    PublishedFinal,
    DiscardedOldUtterance,
    DiscardedOldEpoch,
    DiscardedInvalidLifecycle,
    DiscardedOlderThanAcceptedWatermark,
    Cancelled,
    Failed
}

public sealed record RealtimeTranslationTelemetry(
    long TranscriptEpoch,
    long UtteranceId,
    long RequestedRevision,
    long? CurrentRevision,
    RealtimeSchedulingDecision SchedulingReason,
    TimeSpan CandidateAge,
    TimeSpan TranslationDuration,
    TranslationCompletionDisposition Disposition,
    bool NewerPendingCandidateExisted);

public sealed class RealtimeTranslationScheduler : IAsyncDisposable
{
    private readonly ITextTranslator _translator;
    private readonly IReadOnlyList<IOutputSink> _outputs;
    private readonly ResolvedRealtimeSettings _settings;
    private readonly IAppReporter _reporter;
    private readonly CancellationToken _applicationCancellation;
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TranslationCandidate? _current;
    private TranslationCandidate? _pending;
    private TranslationCandidate? _lastRequested;
    private TranslationCandidate? _lastAccepted;
    private DateTimeOffset? _lastRequestedAt;
    private ActiveTranslation? _active;
    private Task _outputTail = Task.CompletedTask;
    private long _knownEpoch;
    private long _lifecycleGeneration = 1;
    private (long Epoch, long Utterance)? _typingIdentity;
    private bool _disposed;

    public RealtimeTranslationScheduler(
        ITextTranslator translator,
        IReadOnlyList<IOutputSink> outputs,
        ResolvedRealtimeSettings settings,
        IAppReporter reporter,
        CancellationToken applicationCancellation = default,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        _translator = translator;
        _outputs = outputs;
        _settings = settings;
        _reporter = reporter;
        _applicationCancellation = applicationCancellation;
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task SubmitAsync(
        TranslationCandidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var outputs = new List<OutputOperation>();
        var reports = new List<AppEvent>();
        ActiveTranslation? start = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_knownEpoch > candidate.TranscriptEpoch ||
                _current is not null && RealtimeTranslationPolicy.IsNewer(_current, candidate))
            {
                return;
            }

            bool newEpoch = candidate.TranscriptEpoch > _knownEpoch;
            if (newEpoch)
            {
                if (_knownEpoch == 0 && _current is null && _active is null)
                    _knownEpoch = candidate.TranscriptEpoch;
                else
                    InvalidateLifecycleLocked(candidate.TranscriptEpoch, outputs, forceTypingOff: true);
            }

            bool newUtterance = _current is not null &&
                candidate.UtteranceId > _current.UtteranceId;
            if (newUtterance)
                InvalidateUtteranceLocked(outputs);

            _current = candidate;
            if (!candidate.IsSettled &&
                !string.IsNullOrWhiteSpace(candidate.SourceText) &&
                _typingIdentity != (candidate.TranscriptEpoch, candidate.UtteranceId))
            {
                if (_typingIdentity is not null)
                    ReserveTypingOffLocked(outputs);
                _typingIdentity = (candidate.TranscriptEpoch, candidate.UtteranceId);
                outputs.Add(ReserveOutputLocked(TranslationUpdate.Typing(true)));
            }

            bool handled = false;
            if (_active is not null &&
                SameSourceIdentity(_active.RequestedCandidate, candidate))
            {
                if (RealtimeTranslationPolicy.IsNewer(
                        candidate,
                        _active.LatestEquivalentCandidate) ||
                    SameCandidate(candidate, _active.LatestEquivalentCandidate))
                {
                    _active.LatestEquivalentCandidate = candidate;
                }
                if (_pending is not null &&
                    SameSourceIdentity(_pending, candidate))
                {
                    _pending = null;
                }
                handled = true;
            }

            if (!handled && _active is not null)
            {
                if (_pending is not null)
                    reports.Add(AppEvent.RealtimeTranslationCoalesced(candidate));
                _pending = candidate;
                handled = true;
            }

            if (!handled)
            {
                _pending = candidate;
                if (candidate.IsSettled &&
                    _lastAccepted is not null &&
                    SameSourceIdentity(_lastAccepted, candidate))
                {
                    _pending = null;
                    if (RealtimeTranslationPolicy.IsNewer(candidate, _lastAccepted))
                        _lastAccepted = candidate;
                    ReserveTypingOffLocked(outputs);
                }
                else
                {
                    start = TryPrepareStartLocked(now, outputs, reports);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
        await ExecuteEffectsAsync(start, outputs, reports);
    }

    public async Task TickAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var outputs = new List<OutputOperation>();
        var reports = new List<AppEvent>();
        ActiveTranslation? start = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_disposed && _active is null)
                start = TryPrepareStartLocked(now, outputs, reports);
        }
        finally
        {
            _gate.Release();
        }
        await ExecuteEffectsAsync(start, outputs, reports);
    }

    public async Task InvalidateTranscriptEpochAsync(
        long transcriptEpoch,
        CancellationToken cancellationToken = default)
    {
        var outputs = new List<OutputOperation>();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (transcriptEpoch <= _knownEpoch)
                return;
            InvalidateLifecycleLocked(
                transcriptEpoch,
                outputs,
                forceTypingOff: true);
        }
        finally
        {
            _gate.Release();
        }
        await ExecuteEffectsAsync(null, outputs, []);
    }

    public async ValueTask DisposeAsync()
    {
        var outputs = new List<OutputOperation>();
        Task activeCompletion;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _lifecycleGeneration++;
            _current = null;
            _pending = null;
            _active?.Cancellation.Cancel();
            activeCompletion = _active?.Completion.Task ?? Task.CompletedTask;
            _lastRequested = null;
            _lastAccepted = null;
            _lastRequestedAt = null;
            ReserveTypingOffLocked(outputs, force: true);
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(
            activeCompletion,
            ExecuteEffectsAsync(null, outputs, []));
        _gate.Dispose();
    }

    private ActiveTranslation? TryPrepareStartLocked(
        DateTimeOffset now,
        List<OutputOperation> outputs,
        List<AppEvent> reports)
    {
        if (_active is not null || _pending is null || _disposed)
            return null;
        if (_applicationCancellation.IsCancellationRequested)
        {
            _pending = null;
            return null;
        }
        TranslationCandidate candidate = _pending;
        RealtimeSchedulingDecision decision = RealtimeTranslationPolicy.Decide(
            candidate,
            _lastRequested,
            _lastRequestedAt,
            now,
            _settings);
        if (decision is RealtimeSchedulingDecision.Wait or RealtimeSchedulingDecision.Duplicate)
        {
            if (decision == RealtimeSchedulingDecision.Duplicate)
            {
                _pending = null;
                if (candidate.IsSettled)
                {
                    if (_lastAccepted is not null &&
                        SameSourceIdentity(candidate, _lastAccepted) &&
                        RealtimeTranslationPolicy.IsNewer(candidate, _lastAccepted))
                    {
                        _lastAccepted = candidate;
                    }
                    ReserveTypingOffLocked(outputs);
                }
            }
            return null;
        }

        _pending = null;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _applicationCancellation);
        TimeSpan candidateAge = NonNegative(now - candidate.ObservedAt);
        var active = new ActiveTranslation(
            candidate,
            cancellation,
            now,
            decision,
            candidateAge,
            _lifecycleGeneration);
        _active = active;
        _lastRequested = candidate;
        _lastRequestedAt = now;
        reports.Add(AppEvent.RealtimeTranslationStarted(
            candidate,
            decision,
            candidateAge));
        return active;
    }

    private async Task RunTranslationAsync(ActiveTranslation active)
    {
        string? translated = null;
        Exception? failure = null;
        try
        {
            translated = await _translator.TranslateAsync(
                active.RequestedCandidate.SourceText,
                active.Cancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            await CompleteTranslationAsync(active, translated, failure);
        }
        finally
        {
            active.Completion.TrySetResult();
        }
    }

    private async Task CompleteTranslationAsync(
        ActiveTranslation active,
        string? translated,
        Exception? failure)
    {
        DateTimeOffset completedAt = _getUtcNow();
        var outputs = new List<OutputOperation>();
        var reports = new List<AppEvent>();
        ActiveTranslation? start = null;
        TranslationCompletionDisposition disposition;
        long? currentRevision;
        bool newerPending;

        await _gate.WaitAsync();
        try
        {
            TranslationCandidate effective = active.LatestEquivalentCandidate;
            currentRevision = _current?.Revision;
            newerPending = _pending is not null &&
                RealtimeTranslationPolicy.IsNewer(
                    _pending,
                    active.RequestedCandidate);

            if (!ReferenceEquals(_active, active))
            {
                disposition = TranslationCompletionDisposition.DiscardedInvalidLifecycle;
            }
            else
            {
                _active = null;
                active.Cancellation.Dispose();
                disposition = CompletionDispositionLocked(active, effective, failure);

                if (disposition is TranslationCompletionDisposition.PublishedIntermediate or
                    TranslationCompletionDisposition.PublishedFinal)
                {
                    _lastAccepted = effective;
                    outputs.Add(ReserveOutputLocked(
                        TranslationUpdate.Translated(translated!)));
                    reports.Add(AppEvent.RealtimeTranslationPublished(
                        effective,
                        translated!));
                    if (disposition == TranslationCompletionDisposition.PublishedFinal)
                        ReserveTypingOffLocked(outputs);
                }
                else if (disposition == TranslationCompletionDisposition.Failed)
                {
                    if (_pending is not null &&
                        SameSourceIdentity(_pending, effective))
                    {
                        _pending = null;
                    }
                    if (effective.IsSettled)
                        ReserveTypingOffLocked(outputs);
                    reports.Add(FailureEvent(failure!));
                }

                start = TryPrepareStartLocked(completedAt, outputs, reports);
            }

            var telemetry = new RealtimeTranslationTelemetry(
                active.RequestedCandidate.TranscriptEpoch,
                active.RequestedCandidate.UtteranceId,
                active.RequestedCandidate.Revision,
                currentRevision,
                active.SchedulingReason,
                active.CandidateAge,
                NonNegative(completedAt - active.StartedAt),
                disposition,
                newerPending);
            reports.Add(AppEvent.RealtimeTranslationCompleted(telemetry));
        }
        finally
        {
            _gate.Release();
        }

        await ExecuteEffectsAsync(start, outputs, reports);
    }

    private TranslationCompletionDisposition CompletionDispositionLocked(
        ActiveTranslation active,
        TranslationCandidate effective,
        Exception? failure)
    {
        if (_disposed ||
            _applicationCancellation.IsCancellationRequested ||
            failure is OperationCanceledException)
        {
            return TranslationCompletionDisposition.Cancelled;
        }
        if (active.RequestedCandidate.TranscriptEpoch != _knownEpoch ||
            _current is null ||
            effective.TranscriptEpoch != _current.TranscriptEpoch)
        {
            return TranslationCompletionDisposition.DiscardedOldEpoch;
        }
        if (effective.UtteranceId != _current.UtteranceId)
            return TranslationCompletionDisposition.DiscardedOldUtterance;
        if (active.LifecycleGeneration != _lifecycleGeneration)
            return TranslationCompletionDisposition.DiscardedInvalidLifecycle;
        if (failure is not null)
            return TranslationCompletionDisposition.Failed;
        if (_lastAccepted is not null &&
            !RealtimeTranslationPolicy.IsNewer(effective, _lastAccepted))
        {
            return TranslationCompletionDisposition.DiscardedOlderThanAcceptedWatermark;
        }
        return effective.IsSettled
            ? TranslationCompletionDisposition.PublishedFinal
            : TranslationCompletionDisposition.PublishedIntermediate;
    }

    private void InvalidateLifecycleLocked(
        long transcriptEpoch,
        List<OutputOperation> outputs,
        bool forceTypingOff)
    {
        _knownEpoch = transcriptEpoch;
        _lifecycleGeneration++;
        _current = null;
        _pending = null;
        _lastRequested = null;
        _lastAccepted = null;
        _lastRequestedAt = null;
        _active?.Cancellation.Cancel();
        ReserveTypingOffLocked(outputs, forceTypingOff);
    }

    private void InvalidateUtteranceLocked(List<OutputOperation> outputs)
    {
        _lifecycleGeneration++;
        _pending = null;
        _lastRequested = null;
        _lastAccepted = null;
        _lastRequestedAt = null;
        _active?.Cancellation.Cancel();
        ReserveTypingOffLocked(outputs);
    }

    private void ReserveTypingOffLocked(
        List<OutputOperation> outputs,
        bool force = false)
    {
        if (!force && _typingIdentity is null)
            return;
        _typingIdentity = null;
        outputs.Add(ReserveOutputLocked(TranslationUpdate.Typing(false)));
    }

    private OutputOperation ReserveOutputLocked(TranslationUpdate update)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = new OutputOperation(update, _outputTail, completion);
        _outputTail = completion.Task;
        return operation;
    }

    private async Task ExecuteEffectsAsync(
        ActiveTranslation? start,
        IReadOnlyList<OutputOperation> outputs,
        IReadOnlyList<AppEvent> reports)
    {
        if (start is not null)
            _ = RunTranslationAsync(start);
        foreach (AppEvent report in reports)
            ReportSafely(report);
        if (outputs.Count != 0)
        {
            await Task.WhenAll(outputs.Select(ExecuteOutputOperationAsync));
        }
    }

    private async Task ExecuteOutputOperationAsync(OutputOperation operation)
    {
        try
        {
            await operation.Predecessor;
            await PublishSafelyAsync(operation.Update, CancellationToken.None);
        }
        finally
        {
            operation.Completion.TrySetResult();
        }
    }

    private async Task PublishSafelyAsync(
        TranslationUpdate update,
        CancellationToken cancellationToken)
    {
        foreach (IOutputSink output in _outputs)
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
                ReportSafely(AppEvent.OutputError(output.Name, exception.Message));
            }
        }
    }

    private void ReportSafely(AppEvent appEvent)
    {
        try
        {
            _reporter.Report(appEvent);
        }
        catch
        {
            // Reporting is an isolated external side effect.
        }
    }

    private static AppEvent FailureEvent(Exception failure) =>
        failure is OpenAiProviderException provider
            ? AppEvent.RealtimeTranslationFailed(
                provider.Operation,
                provider.Message)
            : AppEvent.RealtimeTranslationFailed(
                "text translation",
                failure.Message);

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static bool SameSourceIdentity(
        TranslationCandidate left,
        TranslationCandidate right) =>
        left.TranscriptEpoch == right.TranscriptEpoch &&
        left.UtteranceId == right.UtteranceId &&
        string.Equals(left.SourceText, right.SourceText, StringComparison.Ordinal);

    private static bool SameCandidate(
        TranslationCandidate left,
        TranslationCandidate right) =>
        SameSourceIdentity(left, right) &&
        left.Revision == right.Revision &&
        left.IsSettled == right.IsSettled;

    private sealed class ActiveTranslation(
        TranslationCandidate requestedCandidate,
        CancellationTokenSource cancellation,
        DateTimeOffset startedAt,
        RealtimeSchedulingDecision schedulingReason,
        TimeSpan candidateAge,
        long lifecycleGeneration)
    {
        public TranslationCandidate RequestedCandidate { get; } = requestedCandidate;
        public TranslationCandidate LatestEquivalentCandidate { get; set; } =
            requestedCandidate;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public RealtimeSchedulingDecision SchedulingReason { get; } = schedulingReason;
        public TimeSpan CandidateAge { get; } = candidateAge;
        public long LifecycleGeneration { get; } = lifecycleGeneration;
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record OutputOperation(
        TranslationUpdate Update,
        Task Predecessor,
        TaskCompletionSource Completion);
}
