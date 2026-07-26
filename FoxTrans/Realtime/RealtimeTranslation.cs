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
        bool lastRequestFailed,
        DateTimeOffset? lastRequestedAt,
        DateTimeOffset now,
        ResolvedRealtimeSettings settings)
    {
        if (lastRequested is not null &&
            string.Equals(candidate.SourceText, lastRequested.SourceText, StringComparison.Ordinal))
        {
            bool newerAfterFailure =
                lastRequestFailed &&
                IsNewer(candidate, lastRequested);
            if (!newerAfterFailure)
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
    private long _knownEpoch;
    private long _typingUtterance;
    private bool _lastRequestFailed;
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
            bool newUtterance = _current is null ||
                newEpoch ||
                candidate.UtteranceId > _current.UtteranceId;
            if (newEpoch)
                _knownEpoch = candidate.TranscriptEpoch;
            if (newUtterance &&
                _active is not null &&
                (_active.Candidate.TranscriptEpoch != candidate.TranscriptEpoch ||
                 _active.Candidate.UtteranceId != candidate.UtteranceId))
            {
                _active.Cancellation.Cancel();
            }

            _current = candidate;
            if (newUtterance && !candidate.IsSettled &&
                _typingUtterance != candidate.UtteranceId)
            {
                _typingUtterance = candidate.UtteranceId;
                await PublishSafelyAsync(
                    TranslationUpdate.Typing(true),
                    CancellationToken.None);
            }

            if (_active is not null &&
                SameSourceIdentity(_active.Candidate, candidate))
            {
                _active.Candidate = candidate;
                if (_pending is not null &&
                    SameSourceIdentity(_pending, candidate))
                {
                    _pending = null;
                }
                return;
            }

            if (_active is not null)
            {
                if (_pending is not null)
                    _reporter.Report(AppEvent.RealtimeTranslationCoalesced(candidate));
                _pending = candidate;
                return;
            }

            _pending = candidate;
            if (candidate.IsSettled &&
                _lastAccepted is not null &&
                SameSourceIdentity(_lastAccepted, candidate))
            {
                _pending = null;
                await PublishSafelyAsync(
                    TranslationUpdate.Typing(false),
                    CancellationToken.None);
                return;
            }
            TryStartLocked(now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TickAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_disposed && _active is null)
                TryStartLocked(now);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateTranscriptEpochAsync(
        long transcriptEpoch,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (transcriptEpoch <= _knownEpoch)
                return;
            _knownEpoch = transcriptEpoch;
            _current = null;
            _pending = null;
            _active?.Cancellation.Cancel();
            await PublishSafelyAsync(
                TranslationUpdate.Typing(false),
                CancellationToken.None);
            _typingUtterance = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? activeTask;
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            _pending = null;
            _active?.Cancellation.Cancel();
            activeTask = _active?.Task;
        }
        finally
        {
            _gate.Release();
        }

        if (activeTask is not null)
        {
            try
            {
                await activeTask;
            }
            catch
            {
                // Completion is reported and suppressed by RunTranslationAsync.
            }
        }

        await PublishSafelyAsync(
            TranslationUpdate.Typing(false),
            CancellationToken.None);
        _gate.Dispose();
    }

    private void TryStartLocked(DateTimeOffset now)
    {
        if (_active is not null || _pending is null || _disposed)
            return;
        if (_applicationCancellation.IsCancellationRequested)
        {
            _pending = null;
            return;
        }
        TranslationCandidate candidate = _pending;
        RealtimeSchedulingDecision decision = RealtimeTranslationPolicy.Decide(
            candidate,
            _lastRequested,
            _lastRequestFailed,
            _lastRequestedAt,
            now,
            _settings);
        if (decision is RealtimeSchedulingDecision.Wait or RealtimeSchedulingDecision.Duplicate)
        {
            if (decision == RealtimeSchedulingDecision.Duplicate)
                _pending = null;
            return;
        }

        _pending = null;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _applicationCancellation);
        var active = new ActiveTranslation(candidate, cancellation);
        _active = active;
        _lastRequested = candidate;
        _lastRequestedAt = now;
        _lastRequestFailed = false;
        _reporter.Report(AppEvent.RealtimeTranslationStarted(candidate, decision));
        active.Task = RunTranslationAsync(active);
    }

    private async Task RunTranslationAsync(ActiveTranslation active)
    {
        string? translated = null;
        Exception? failure = null;
        try
        {
            translated = await _translator.TranslateAsync(
                active.Candidate.SourceText,
                active.Cancellation.Token);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        TranslationCandidate completedCandidate;
        await _gate.WaitAsync();
        try
        {
            completedCandidate = active.Candidate;
            if (!ReferenceEquals(_active, active))
                return;
            _active = null;
            active.Cancellation.Dispose();

            bool applicationCancelled = _applicationCancellation.IsCancellationRequested;
            bool requestCancelled = failure is OperationCanceledException;
            bool stale = IsStaleLocked(completedCandidate);
            if (failure is null && !stale && !applicationCancelled)
            {
                _lastAccepted = completedCandidate;
                _lastRequestFailed = false;
                await PublishSafelyAsync(
                    TranslationUpdate.Translated(translated!),
                    CancellationToken.None);
                _reporter.Report(AppEvent.RealtimeTranslationPublished(
                    completedCandidate,
                    translated!));
                if (completedCandidate.IsSettled)
                {
                    await PublishSafelyAsync(
                        TranslationUpdate.Typing(false),
                        CancellationToken.None);
                }
            }
            else if (failure is not null && !requestCancelled && !applicationCancelled)
            {
                _lastRequestFailed = true;
                if (failure is OpenAiProviderException provider)
                {
                    _reporter.Report(AppEvent.RealtimeTranslationFailed(
                        provider.Operation,
                        provider.Message));
                }
                else
                {
                    _reporter.Report(AppEvent.RealtimeTranslationFailed(
                        "text translation",
                        failure.Message));
                }
                if (!stale && completedCandidate.IsSettled)
                {
                    await PublishSafelyAsync(
                        TranslationUpdate.Typing(false),
                        CancellationToken.None);
                }
                if (_pending is not null &&
                    SameCandidate(_pending, completedCandidate))
                {
                    _pending = null;
                }
            }
            else if (stale && !applicationCancelled)
            {
                _reporter.Report(AppEvent.StaleTranslationDiscarded(completedCandidate));
            }
        }
        finally
        {
            _gate.Release();
        }

        await TickAsync(_getUtcNow());
    }

    private bool IsStaleLocked(TranslationCandidate candidate)
    {
        if (_current is null ||
            candidate.TranscriptEpoch != _current.TranscriptEpoch ||
            candidate.UtteranceId != _current.UtteranceId ||
            candidate.Revision < _current.Revision ||
            !string.Equals(candidate.SourceText, _current.SourceText, StringComparison.Ordinal))
        {
            return true;
        }
        return _lastAccepted is not null &&
            RealtimeTranslationPolicy.IsNewer(_lastAccepted, candidate);
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
                _reporter.Report(AppEvent.OutputError(output.Name, exception.Message));
            }
        }
    }

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
        TranslationCandidate candidate,
        CancellationTokenSource cancellation)
    {
        public TranslationCandidate Candidate { get; set; } = candidate;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task? Task { get; set; }
    }
}
