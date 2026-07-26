using Xunit;

public sealed class ConsoleUiTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SettlementRetainsSourceAndAcceptedResultUntilNewStateSupersedesThem()
    {
        using var ui = new ConsoleUi();
        TranslationCandidate first = Candidate(1, 1, "first source");
        ui.Report(AppEvent.LogicalUtteranceStarted(first));
        ui.Report(AppEvent.RealtimeTranslationAccepted(first, "first translation"));
        ui.Report(AppEvent.LogicalUtteranceSettled(first with { IsSettled = true }));

        ConsoleUiSnapshot settled = ui.Snapshot;
        Assert.Contains("first source", settled.Source);
        Assert.Equal("first translation", settled.Translation);
        Assert.True(settled.TranslationIsForCurrentSource);

        TranslationCandidate next = Candidate(2, 1, "next active source");
        ui.Report(AppEvent.LogicalUtteranceStarted(next));
        ConsoleUiSnapshot nextActive = ui.Snapshot;
        Assert.Contains("next active source", nextActive.Source);
        Assert.Equal("first translation", nextActive.Translation);
        Assert.False(nextActive.TranslationIsForCurrentSource);

        ui.Report(AppEvent.RealtimeTranslationAccepted(next, "next translation"));
        ConsoleUiSnapshot replaced = ui.Snapshot;
        Assert.Equal("next translation", replaced.Translation);
        Assert.True(replaced.TranslationIsForCurrentSource);
    }

    [Fact]
    public void EpochResetClearsDisplaySoOldResultIsNotLabelledCurrent()
    {
        using var ui = new ConsoleUi();
        TranslationCandidate candidate = Candidate(1, 1, "source");
        ui.Report(AppEvent.LogicalUtteranceStarted(candidate));
        ui.Report(AppEvent.RealtimeTranslationAccepted(candidate, "translation"));

        ui.Report(AppEvent.TranscriptEpochResynchronized("test epoch reset"));
        ConsoleUiSnapshot snapshot = ui.Snapshot;
        Assert.Equal("None", snapshot.Source);
        Assert.Equal("None", snapshot.Translation);
        Assert.True(snapshot.TranslationIsForCurrentSource);
    }

    private static TranslationCandidate Candidate(
        long utterance,
        long revision,
        string source) =>
        new(
            1,
            utterance,
            revision,
            source,
            false,
            false,
            revision,
            revision * 80,
            Start,
            Start);
}
