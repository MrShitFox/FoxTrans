using System.Globalization;
using System.Text;

public sealed record TranslationCandidate(
    long TranscriptEpoch,
    long UtteranceId,
    long Revision,
    string SourceText,
    bool SourceWasTruncated,
    bool IsSettled,
    long ServerSequence,
    long AudioEndMs,
    DateTimeOffset UtteranceStartedAt,
    DateTimeOffset ObservedAt);

public sealed record LogicalUtteranceState(
    string LatestCumulativeText,
    string CommittedBoundary,
    string ActiveUtteranceText,
    long UtteranceId,
    long Revision,
    DateTimeOffset? UtteranceStartedAt,
    DateTimeOffset? LastMeaningfulTextChangeAt,
    long ServerSequence,
    long AudioEndMs,
    long TranscriptEpoch,
    bool IsActive,
    bool IsSettled)
{
    public static LogicalUtteranceState Initial { get; } = new(
        "", "", "", 0, 0, null, null, 0, 0, 1, false, false);
}

public enum UtteranceTransitionKind
{
    None,
    Started,
    Updated,
    Settled,
    LateAmendment,
    Resynchronized
}

public sealed record UtteranceTransition(
    LogicalUtteranceState State,
    UtteranceTransitionKind Kind = UtteranceTransitionKind.None,
    TranslationCandidate? Candidate = null,
    string? Warning = null)
{
    public bool TranscriptEpochChanged => Kind == UtteranceTransitionKind.Resynchronized;
}

public sealed record BoundedTextWindow(string Text, bool WasTruncated);

public static class UtteranceTracking
{
    public static LogicalUtteranceState StartNewTranscriptEpoch(
        LogicalUtteranceState state) =>
        LogicalUtteranceState.Initial with
        {
            TranscriptEpoch = state.TranscriptEpoch + 1,
            UtteranceId = state.UtteranceId
        };

    public static UtteranceTransition ReducePartial(
        LogicalUtteranceState state,
        StreamingPartialTranscript partial,
        DateTimeOffset now,
        int maxSourceCharacters)
    {
        string cumulative = partial.Text;
        if (cumulative.Length == 0)
            return new(state with
            {
                ServerSequence = partial.Sequence,
                AudioEndMs = partial.AudioEndMs
            });

        if (string.Equals(cumulative, state.LatestCumulativeText, StringComparison.Ordinal))
        {
            return new(state with
            {
                ServerSequence = partial.Sequence,
                AudioEndMs = partial.AudioEndMs
            });
        }

        if (state.CommittedBoundary.Length != 0 &&
            !cumulative.StartsWith(state.CommittedBoundary, StringComparison.Ordinal))
        {
            return Resynchronize(state, partial, now, maxSourceCharacters);
        }

        if (state.IsActive)
        {
            string activeText = ExtractSuffix(cumulative, state.CommittedBoundary);
            long revision = string.Equals(
                activeText,
                state.ActiveUtteranceText,
                StringComparison.Ordinal)
                ? state.Revision
                : state.Revision + 1;
            LogicalUtteranceState updated = state with
            {
                LatestCumulativeText = cumulative,
                ActiveUtteranceText = activeText,
                Revision = revision,
                LastMeaningfulTextChangeAt = now,
                ServerSequence = partial.Sequence,
                AudioEndMs = partial.AudioEndMs,
                IsSettled = false
            };
            if (revision == state.Revision || !HasLexicalContent(activeText))
                return new(updated);
            return new(
                updated,
                UtteranceTransitionKind.Updated,
                CreateCandidate(updated, false, now, maxSourceCharacters));
        }

        if (state.IsSettled)
        {
            string suffix = cumulative[state.CommittedBoundary.Length..];
            if (!HasLexicalContent(suffix))
            {
                string amendment = suffix.TrimStart();
                string amendedText = state.ActiveUtteranceText + amendment;
                bool visibleChange = amendment.Any(character => !char.IsWhiteSpace(character));
                LogicalUtteranceState amended = state with
                {
                    LatestCumulativeText = cumulative,
                    CommittedBoundary = cumulative,
                    ActiveUtteranceText = amendedText,
                    Revision = visibleChange ? state.Revision + 1 : state.Revision,
                    LastMeaningfulTextChangeAt = now,
                    ServerSequence = partial.Sequence,
                    AudioEndMs = partial.AudioEndMs
                };
                return visibleChange
                    ? new(
                        amended,
                        UtteranceTransitionKind.LateAmendment,
                        CreateCandidate(amended, true, now, maxSourceCharacters))
                    : new(amended);
            }
        }

        string nextText = ExtractSuffix(cumulative, state.CommittedBoundary);
        if (!HasLexicalContent(nextText))
        {
            return new(state with
            {
                LatestCumulativeText = cumulative,
                ServerSequence = partial.Sequence,
                AudioEndMs = partial.AudioEndMs,
                LastMeaningfulTextChangeAt = now
            });
        }

        LogicalUtteranceState started = state with
        {
            LatestCumulativeText = cumulative,
            ActiveUtteranceText = nextText,
            UtteranceId = state.UtteranceId + 1,
            Revision = 1,
            UtteranceStartedAt = now,
            LastMeaningfulTextChangeAt = now,
            ServerSequence = partial.Sequence,
            AudioEndMs = partial.AudioEndMs,
            IsActive = true,
            IsSettled = false
        };
        return new(
            started,
            UtteranceTransitionKind.Started,
            CreateCandidate(started, false, now, maxSourceCharacters));
    }

    public static UtteranceTransition CheckInactivity(
        LogicalUtteranceState state,
        DateTimeOffset now,
        int newUtteranceAfterMs,
        int maxSourceCharacters)
    {
        if (!state.IsActive ||
            state.ActiveUtteranceText.Length == 0 ||
            state.LastMeaningfulTextChangeAt is null ||
            now - state.LastMeaningfulTextChangeAt <
                TimeSpan.FromMilliseconds(newUtteranceAfterMs))
        {
            return new(state);
        }

        LogicalUtteranceState settled = state with
        {
            CommittedBoundary = state.LatestCumulativeText,
            IsActive = false,
            IsSettled = true
        };
        return new(
            settled,
            UtteranceTransitionKind.Settled,
            CreateCandidate(settled, true, now, maxSourceCharacters));
    }

    public static string ExtractSuffix(string cumulative, string committedBoundary) =>
        cumulative[committedBoundary.Length..].TrimStart();

    public static bool HasLexicalContent(string text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            if (category is
                UnicodeCategory.UppercaseLetter or
                UnicodeCategory.LowercaseLetter or
                UnicodeCategory.TitlecaseLetter or
                UnicodeCategory.ModifierLetter or
                UnicodeCategory.OtherLetter or
                UnicodeCategory.DecimalDigitNumber or
                UnicodeCategory.LetterNumber or
                UnicodeCategory.OtherNumber)
            {
                return true;
            }
        }
        return false;
    }

    private static UtteranceTransition Resynchronize(
        LogicalUtteranceState state,
        StreamingPartialTranscript partial,
        DateTimeOffset now,
        int maxSourceCharacters)
    {
        bool active = HasLexicalContent(partial.Text);
        LogicalUtteranceState resynchronized = new(
            partial.Text,
            "",
            active ? partial.Text.TrimStart() : "",
            active ? state.UtteranceId + 1 : state.UtteranceId,
            active ? 1 : 0,
            active ? now : null,
            active ? now : null,
            partial.Sequence,
            partial.AudioEndMs,
            state.TranscriptEpoch + 1,
            active,
            false);
        return new(
            resynchronized,
            UtteranceTransitionKind.Resynchronized,
            active ? CreateCandidate(resynchronized, false, now, maxSourceCharacters) : null,
            "The cumulative transcript changed before the committed boundary. " +
            "Pending translations were invalidated and a new client transcript epoch was started.");
    }

    private static TranslationCandidate CreateCandidate(
        LogicalUtteranceState state,
        bool settled,
        DateTimeOffset now,
        int maxSourceCharacters)
    {
        BoundedTextWindow window = TranslationWindows.Build(
            state.ActiveUtteranceText,
            maxSourceCharacters);
        return new(
            state.TranscriptEpoch,
            state.UtteranceId,
            state.Revision,
            window.Text,
            window.WasTruncated,
            settled,
            state.ServerSequence,
            state.AudioEndMs,
            state.UtteranceStartedAt ?? now,
            now);
    }
}

public static class TranslationWindows
{
    private const string Ellipsis = "…";

    public static BoundedTextWindow Build(string text, int maxTextElements)
    {
        if (maxTextElements < 1)
            throw new ArgumentOutOfRangeException(nameof(maxTextElements));
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length <= maxTextElements)
            return new(text, false);

        int suffixBudget = maxTextElements - 1;
        if (suffixBudget == 0)
            return new(Ellipsis, true);
        string[] elements = starts
            .Select((start, index) => text.Substring(
                start,
                (index + 1 < starts.Length ? starts[index + 1] : text.Length) - start))
            .ToArray();
        int minimumStart = elements.Length - suffixBudget;
        int selectedStart =
            FindBoundary(elements, minimumStart, IsSentencePunctuation) ??
            FindBoundary(elements, minimumStart, IsPunctuation) ??
            FindWordBoundary(elements, minimumStart) ??
            minimumStart;
        while (selectedStart < elements.Length && IsWhitespace(elements[selectedStart]))
            selectedStart++;
        if (selectedStart >= elements.Length)
            selectedStart = minimumStart;

        string suffix = string.Concat(elements[selectedStart..]);
        return new(Ellipsis + suffix, true);
    }

    public static int TextElementCount(string text) =>
        StringInfo.ParseCombiningCharacters(text).Length;

    private static int? FindBoundary(
        IReadOnlyList<string> elements,
        int minimumStart,
        Func<string, bool> predicate)
    {
        for (int index = Math.Max(0, minimumStart - 1); index < elements.Count - 1; index++)
        {
            if (!predicate(elements[index]))
                continue;
            int start = index + 1;
            while (start < elements.Count && IsWhitespace(elements[start]))
                start++;
            if (start >= minimumStart && start < elements.Count)
                return start;
        }
        return null;
    }

    private static int? FindWordBoundary(IReadOnlyList<string> elements, int minimumStart)
    {
        for (int index = minimumStart; index < elements.Count; index++)
        {
            if (!IsWhitespace(elements[index]))
                continue;
            int start = index + 1;
            while (start < elements.Count && IsWhitespace(elements[start]))
                start++;
            if (start < elements.Count)
                return start;
        }
        return null;
    }

    private static bool IsSentencePunctuation(string element) =>
        element is "." or "!" or "?" or "。" or "！" or "？" or "؟";

    private static bool IsPunctuation(string element)
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

    private static bool IsWhitespace(string element) =>
        element.EnumerateRunes().All(Rune.IsWhiteSpace);
}
