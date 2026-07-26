using Xunit;

public sealed class UtteranceTrackingTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EmptyPartialDoesNotCreateUtterance()
    {
        UtteranceTransition result = Partial(LogicalUtteranceState.Initial, "", 1, Start);
        Assert.Equal(UtteranceTransitionKind.None, result.Kind);
        Assert.False(result.State.IsActive);
        Assert.Null(result.Candidate);
    }

    [Fact]
    public void FirstMeaningfulPartialStartsUtteranceOneAndPreservesMetadata()
    {
        UtteranceTransition result = Partial(
            LogicalUtteranceState.Initial,
            "hello",
            7,
            Start,
            880);
        Assert.Equal(UtteranceTransitionKind.Started, result.Kind);
        Assert.Equal(1, result.State.UtteranceId);
        Assert.Equal(1, result.State.Revision);
        Assert.Equal("hello", result.State.ActiveUtteranceText);
        Assert.Equal(7, result.Candidate!.ServerSequence);
        Assert.Equal(880, result.Candidate.AudioEndMs);
    }

    [Fact]
    public void AppendAndCorrectionUpdateSameUtterance()
    {
        LogicalUtteranceState state =
            Partial(LogicalUtteranceState.Initial, "hello", 1, Start).State;
        UtteranceTransition appended =
            Partial(state, "hello everyone", 2, Start.AddMilliseconds(100));
        UtteranceTransition corrected =
            Partial(appended.State, "hello everybody", 3, Start.AddMilliseconds(200));
        Assert.Equal(1, corrected.State.UtteranceId);
        Assert.Equal(3, corrected.State.Revision);
        Assert.Equal("hello everybody", corrected.State.ActiveUtteranceText);
        Assert.Equal(UtteranceTransitionKind.Updated, corrected.Kind);
    }

    [Fact]
    public void IdenticalPartialDoesNotIncrementRevisionOrResetInactivity()
    {
        LogicalUtteranceState state =
            Partial(LogicalUtteranceState.Initial, "hello", 1, Start).State;
        UtteranceTransition duplicate =
            Partial(state, "hello", 2, Start.AddSeconds(10), 500);
        Assert.Equal(1, duplicate.State.Revision);
        Assert.Equal(Start, duplicate.State.LastMeaningfulTextChangeAt);
        Assert.Equal(2, duplicate.State.ServerSequence);
        Assert.Equal(500, duplicate.State.AudioEndMs);
    }

    [Fact]
    public void InactivitySettlesExactlyOnceAndWarningsCannotAffectIt()
    {
        LogicalUtteranceState active =
            Partial(LogicalUtteranceState.Initial, "hello", 1, Start).State;
        DateTimeOffset deadline = Start.AddMilliseconds(1000);
        UtteranceTransition settled =
            UtteranceTracking.CheckInactivity(active, deadline, 1000, 600);
        UtteranceTransition repeated =
            UtteranceTracking.CheckInactivity(settled.State, deadline.AddSeconds(5), 1000, 600);
        Assert.Equal(UtteranceTransitionKind.Settled, settled.Kind);
        Assert.True(settled.Candidate!.IsSettled);
        Assert.Equal(UtteranceTransitionKind.None, repeated.Kind);
        Assert.Null(repeated.Candidate);
        Assert.Equal(Start, active.LastMeaningfulTextChangeAt);
    }

    [Fact]
    public void LexicalSuffixAfterPauseStartsNextUtteranceWithoutChangingEpoch()
    {
        LogicalUtteranceState state =
            Partial(LogicalUtteranceState.Initial, "hello", 1, Start).State;
        state = Partial(state, "hello everyone", 2, Start.AddMilliseconds(100)).State;
        state = UtteranceTracking.CheckInactivity(
            state,
            Start.AddMilliseconds(1100),
            1000,
            600).State;
        UtteranceTransition next =
            Partial(state, "hello everyone   today I want", 3, Start.AddMilliseconds(1200));
        Assert.Equal(UtteranceTransitionKind.Started, next.Kind);
        Assert.Equal(2, next.State.UtteranceId);
        Assert.Equal(1, next.State.TranscriptEpoch);
        Assert.Equal("today I want", next.State.ActiveUtteranceText);
        Assert.DoesNotContain("hello everyone", next.Candidate!.SourceText);
    }

    [Fact]
    public void LatePunctuationAmendsSettledUtterance()
    {
        LogicalUtteranceState active =
            Partial(LogicalUtteranceState.Initial, "hello everyone", 1, Start).State;
        LogicalUtteranceState settled = UtteranceTracking.CheckInactivity(
            active,
            Start.AddSeconds(2),
            1000,
            600).State;
        UtteranceTransition amendment =
            Partial(settled, "hello everyone.", 2, Start.AddSeconds(3));
        Assert.Equal(UtteranceTransitionKind.LateAmendment, amendment.Kind);
        Assert.Equal(1, amendment.State.UtteranceId);
        Assert.Equal("hello everyone.", amendment.State.ActiveUtteranceText);
        Assert.True(amendment.Candidate!.IsSettled);
    }

    [Fact]
    public void CommittedPrefixViolationWarnsAndStartsSafeNewEpoch()
    {
        LogicalUtteranceState active =
            Partial(LogicalUtteranceState.Initial, "hello everyone", 1, Start).State;
        LogicalUtteranceState settled = UtteranceTracking.CheckInactivity(
            active,
            Start.AddSeconds(2),
            1000,
            600).State;
        UtteranceTransition result =
            Partial(settled, "unrelated corrected text", 2, Start.AddSeconds(3));
        Assert.Equal(UtteranceTransitionKind.Resynchronized, result.Kind);
        Assert.NotNull(result.Warning);
        Assert.Equal(2, result.State.TranscriptEpoch);
        Assert.Equal(2, result.State.UtteranceId);
        Assert.Equal("unrelated corrected text", result.State.ActiveUtteranceText);
        Assert.DoesNotContain("hello everyone", result.Candidate!.SourceText);
    }

    [Fact]
    public void StateIsBoundedAndContainsNoTranscriptHistoryCollection()
    {
        LogicalUtteranceState state = LogicalUtteranceState.Initial;
        for (int index = 1; index <= 1000; index++)
            state = Partial(state, "word " + index, index, Start.AddMilliseconds(index)).State;
        Assert.DoesNotContain(
            typeof(LogicalUtteranceState).GetProperties(),
            property => typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType) &&
                        property.PropertyType != typeof(string));
        Assert.Equal("word 1000", state.LatestCumulativeText);
    }

    private static UtteranceTransition Partial(
        LogicalUtteranceState state,
        string text,
        long sequence,
        DateTimeOffset time,
        long audioEndMs = 80) =>
        UtteranceTracking.ReducePartial(
            state,
            new StreamingPartialTranscript(sequence, text, audioEndMs),
            time,
            600);
}
