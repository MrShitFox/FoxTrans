using System.Collections.Concurrent;
using Xunit;

public sealed class RealtimeTranslationSchedulerTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly ResolvedRealtimeSettings Settings =
        new(100, 500, 2, 1000, 600);

    [Fact]
    public async Task ContinuousSpeechPublishesSupersededRevisionAndCoalescesLatestOnly()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "one two", observed: Start),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(Candidate(11, "one two three"), now, TestContext.Current.CancellationToken);
        await scheduler.SubmitAsync(Candidate(12, "one two three four"), now, TestContext.Current.CancellationToken);
        await scheduler.SubmitAsync(Candidate(13, "one two three four five"), now, TestContext.Current.CancellationToken);
        Assert.Single(translator.Calls);

        now = now.AddMilliseconds(842);
        translator.Calls[0].Complete("intermediate");
        await translator.WaitForCallsAsync(2);
        Assert.Equal(
            ["one two", "one two three four five"],
            translator.Calls.Select(call => call.Source).ToArray());
        Assert.Equal(1, translator.MaximumConcurrentCalls);
        await WaitUntilAsync(() => output.Translations.Contains("intermediate"));

        now = now.AddMilliseconds(300);
        translator.Calls[1].Complete("newest");
        await WaitUntilAsync(() => output.Translations.Contains("newest"));
        Assert.Equal(["intermediate", "newest"], output.Translations.ToArray());

        RealtimeTranslationTelemetry first = reporter.Telemetry.Single(item =>
            item.RequestedRevision == 10);
        Assert.Equal(13, first.CurrentRevision);
        Assert.Equal(TimeSpan.FromMilliseconds(100), first.CandidateAge);
        Assert.Equal(TimeSpan.FromMilliseconds(842), first.TranslationDuration);
        Assert.Equal(
            RealtimeSchedulingDecision.MinimumChangedWords,
            first.SchedulingReason);
        Assert.Equal(
            TranslationCompletionDisposition.PublishedIntermediate,
            first.Disposition);
        Assert.True(first.NewerPendingCandidateExisted);
    }

    [Fact]
    public async Task ExactDuplicateSourceIsNotRequestedTwice()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [], Settings, new Reporter(), getUtcNow: () => now);
        await scheduler.SubmitAsync(Candidate(1, "one two"), now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        translator.Calls[0].Complete("ok");
        await WaitUntilAsync(() => translator.ActiveCalls == 0);
        now = now.AddSeconds(1);
        await scheduler.SubmitAsync(Candidate(2, "one two"), now, TestContext.Current.CancellationToken);
        await scheduler.TickAsync(now, TestContext.Current.CancellationToken);
        Assert.Single(translator.Calls);
    }

    [Fact]
    public async Task NewUtteranceCancelsOldRequestAndSuppressesItsResult()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator(ignoreCancellation: true);
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);
        await scheduler.SubmitAsync(Candidate(1, "one two"), now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);

        now = now.AddMilliseconds(100);
        await scheduler.SubmitAsync(
            Candidate(1, "new utterance", utterance: 2),
            now,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => translator.Calls[0].CancellationRequested);
        translator.Calls[0].Complete("old result");
        await translator.WaitForCallsAsync(2);
        Assert.DoesNotContain("old result", output.Translations);

        translator.Calls[1].Complete("new result");
        await WaitUntilAsync(() => output.Translations.Contains("new result"));
        Assert.Equal(["new result"], output.Translations.ToArray());
    }

    [Fact]
    public async Task FailureDoesNotPublishOrBusyRetryAndNewerCandidateCanSucceed()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);
        await scheduler.SubmitAsync(Candidate(1, "one two"), now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        translator.Calls[0].Fail(new OpenAiProviderException("text translation", "offline"));
        await WaitUntilAsync(() => reporter.Events.Any(
            item => item.Kind == AppEventKind.RealtimeTranslationFailed));
        await scheduler.TickAsync(now.AddSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(translator.Calls);
        Assert.Empty(output.Translations);

        now = now.AddSeconds(1);
        await scheduler.SubmitAsync(Candidate(2, "one two three four"), now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(2);
        translator.Calls[1].Complete("recovered");
        await WaitUntilAsync(() => output.Translations.Contains("recovered"));
    }

    [Fact]
    public async Task SettledFailureTurnsTypingOff()
    {
        DateTimeOffset now = Start;
        var translator = new ControlledTranslator();
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);
        await scheduler.SubmitAsync(Candidate(1, "one"), now, TestContext.Current.CancellationToken);
        await scheduler.SubmitAsync(Candidate(1, "one", settled: true), now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        translator.Calls[0].Fail(new OpenAiProviderException("text translation", "failed"));
        await WaitUntilAsync(() => output.Typing.LastOrDefault() == false &&
                                   output.Typing.Count >= 2);
        Assert.Empty(output.Translations);
    }

    [Fact]
    public async Task ApplicationCancellationCancelsActiveRequest()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        using var cancellation = new CancellationTokenSource();
        var translator = new ControlledTranslator();
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator,
            [output],
            Settings,
            new Reporter(),
            cancellation.Token,
            () => now);
        await scheduler.SubmitAsync(Candidate(1, "one two"), now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        cancellation.Cancel();
        await WaitUntilAsync(() => translator.Calls[0].CancellationRequested);
        await WaitUntilAsync(() => translator.ActiveCalls == 0);
        Assert.Empty(output.Translations);
    }

    [Fact]
    public async Task TranscriptEpochInvalidationCancelsOldWorkAndResetsSourceContext()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator(ignoreCancellation: true);
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);
        await scheduler.SubmitAsync(
            Candidate(1, "old source"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);

        await scheduler.InvalidateTranscriptEpochAsync(
            2,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => translator.Calls[0].CancellationRequested);
        await WaitUntilAsync(() => output.Typing.Count >= 2);
        Assert.False(output.Typing.Last());
        translator.Calls[0].Complete("stale old translation");
        await WaitUntilAsync(() => translator.ActiveCalls == 0);
        Assert.Empty(output.Translations);

        now = now.AddMilliseconds(100);
        TranslationCandidate fresh = Candidate(1, "new source") with
        {
            TranscriptEpoch = 2,
            UtteranceId = 2,
            UtteranceStartedAt = now.AddMilliseconds(-100),
            ObservedAt = now
        };
        await scheduler.SubmitAsync(fresh, now, TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(2);
        translator.Calls[1].Complete("fresh translation");
        await WaitUntilAsync(() => output.Translations.Contains("fresh translation"));
        Assert.DoesNotContain("stale old translation", output.Translations);
    }

    [Fact]
    public async Task SameSourceSettlementUsesImmutableRequestedSnapshotAndPublishesFinalOnce()
    {
        DateTimeOffset now = Start.AddMilliseconds(200);
        var translator = new ControlledTranslator();
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(12, "hello everyone", observed: Start),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(
            Candidate(14, "hello everyone", settled: true),
            now,
            TestContext.Current.CancellationToken);
        Assert.Single(translator.Calls);

        now = now.AddMilliseconds(700);
        translator.Calls[0].Complete("final translation");
        await WaitUntilAsync(() => output.Translations.Count == 1 &&
                                   output.Typing.LastOrDefault() == false);

        Assert.Equal(["hello everyone"], translator.Calls.Select(call => call.Source));
        Assert.Equal(["final translation"], output.Translations);
        Assert.Equal([true, false], output.Typing);
        Assert.Single(reporter.Events, item =>
            item.Kind == AppEventKind.TranslationRequestStarted);
        Assert.Contains("/r12 started", reporter.Events.Single(item =>
            item.Kind == AppEventKind.TranslationRequestStarted).Message);
        RealtimeTranslationTelemetry telemetry = Assert.Single(reporter.Telemetry);
        Assert.Equal(12, telemetry.RequestedRevision);
        Assert.Equal(14, telemetry.CurrentRevision);
        Assert.Equal(TimeSpan.FromMilliseconds(200), telemetry.CandidateAge);
        Assert.Equal(TimeSpan.FromMilliseconds(700), telemetry.TranslationDuration);
        Assert.Equal(
            TranslationCompletionDisposition.PublishedFinal,
            telemetry.Disposition);
        AppEvent diagnostic = reporter.Events.Single(item =>
            item.Kind == AppEventKind.RealtimeTranslationCompleted);
        Assert.DoesNotContain("hello everyone", diagnostic.Message);
        Assert.DoesNotContain("Authorization", diagnostic.Message);

        translator.Calls[0].Complete("duplicate");
        await scheduler.TickAsync(now.AddSeconds(1), TestContext.Current.CancellationToken);
        Assert.Single(output.Translations);
        Assert.Single(translator.Calls);
    }

    [Fact]
    public async Task FailureKeepsNewestPendingCandidateAndDoesNotRetryUnchangedSource()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "one two"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(
            Candidate(11, "one two three"),
            now,
            TestContext.Current.CancellationToken);
        await scheduler.SubmitAsync(
            Candidate(13, "one two three four five"),
            now,
            TestContext.Current.CancellationToken);

        now = now.AddMilliseconds(100);
        translator.Calls[0].Fail(
            new OpenAiProviderException("text translation", "offline"));
        await translator.WaitForCallsAsync(2);
        Assert.Equal(
            ["one two", "one two three four five"],
            translator.Calls.Select(call => call.Source));
        Assert.Empty(output.Translations);

        now = now.AddMilliseconds(300);
        translator.Calls[1].Complete("recovered");
        await WaitUntilAsync(() => output.Translations.Contains("recovered"));
        Assert.Equal(1, translator.MaximumConcurrentCalls);
        Assert.Equal(
            TranslationCompletionDisposition.Failed,
            reporter.Telemetry.Single(item => item.RequestedRevision == 10).Disposition);

        await scheduler.SubmitAsync(
            Candidate(14, "one two three four five"),
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        await scheduler.TickAsync(
            now.AddSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public async Task OldUtteranceFailureIsSuppressedAndNewUtteranceTypingTransitionsOnce()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator(ignoreCancellation: true);
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "old utterance"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        now = now.AddMilliseconds(100);
        await scheduler.SubmitAsync(
            Candidate(1, "new utterance", utterance: 2),
            now,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => output.Typing.Count >= 3);
        Assert.Equal([true, false, true], output.Typing);

        translator.Calls[0].Fail(new InvalidOperationException("late old failure"));
        await translator.WaitForCallsAsync(2);
        Assert.DoesNotContain(reporter.Events, item =>
            item.Kind == AppEventKind.RealtimeTranslationFailed);
        Assert.Equal(
            TranslationCompletionDisposition.DiscardedOldUtterance,
            reporter.Telemetry.Single(item => item.UtteranceId == 1).Disposition);

        translator.Calls[1].Complete("new translation");
        await WaitUntilAsync(() => output.Translations.Contains("new translation"));
        Assert.DoesNotContain("late old failure", output.Translations);
    }

    [Fact]
    public async Task OldEpochCompletionIsTypedAsObsoleteAndNewEpochWatermarkStartsClean()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator(ignoreCancellation: true);
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "epoch one"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.InvalidateTranscriptEpochAsync(
            2,
            TestContext.Current.CancellationToken);
        TranslationCandidate epochTwo = Candidate(1, "epoch two", utterance: 2) with
        {
            TranscriptEpoch = 2
        };
        await scheduler.SubmitAsync(
            epochTwo,
            now,
            TestContext.Current.CancellationToken);

        translator.Calls[0].Complete("old epoch result");
        await translator.WaitForCallsAsync(2);
        Assert.Equal(
            TranslationCompletionDisposition.DiscardedOldEpoch,
            reporter.Telemetry.Single(item => item.TranscriptEpoch == 1).Disposition);
        Assert.DoesNotContain("old epoch result", output.Translations);

        translator.Calls[1].Complete("first epoch two result");
        await WaitUntilAsync(() =>
            output.Translations.Contains("first epoch two result"));
        Assert.Equal(["first epoch two result"], output.Translations);
    }

    [Fact]
    public async Task BlockedTranslationOutputDoesNotHoldStateGateOrSerializeNextHttpCall()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new BlockingTranslationOutput();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, reporter, getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "one two"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(
            Candidate(13, "one two three four five"),
            now,
            TestContext.Current.CancellationToken);

        now = now.AddMilliseconds(100);
        translator.Calls[0].Complete("revision 10");
        await output.Blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(2);
        Assert.Equal(1, translator.MaximumConcurrentCalls);

        Task submit = scheduler.SubmitAsync(
            Candidate(14, "one two three four five six"),
            now,
            TestContext.Current.CancellationToken);
        await submit.WaitAsync(TestContext.Current.CancellationToken);
        translator.Calls[1].Complete("revision 13");
        await WaitUntilAsync(() => translator.ActiveCalls == 0);
        Assert.Empty(output.Translations);

        output.Release.TrySetResult();
        await WaitUntilAsync(() => output.Translations.Count == 2);
        Assert.Equal(["revision 10", "revision 13"], output.Translations);
    }

    [Fact]
    public async Task ReporterCallbacksRunOutsideStateGate()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var reporter = new BlockingReporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [], Settings, reporter, getUtcNow: () => now);

        Task first = Task.Run(
            () => scheduler.SubmitAsync(
                Candidate(10, "one two"),
                now,
                TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        await reporter.Blocked.Task.WaitAsync(TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);

        Task second = scheduler.SubmitAsync(
            Candidate(13, "one two three four five"),
            now,
            TestContext.Current.CancellationToken);
        await second.WaitAsync(TestContext.Current.CancellationToken);
        reporter.Release.TrySetResult();
        await first.WaitAsync(TestContext.Current.CancellationToken);

        now = now.AddMilliseconds(100);
        translator.Calls[0].Complete("intermediate");
        await translator.WaitForCallsAsync(2);
        translator.Calls[1].Complete("newest");
        await WaitUntilAsync(() => translator.ActiveCalls == 0);
        Assert.Equal(1, translator.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task TypingDoesNotFlickerAcrossIntermediateResultsAndStopsOnSettlement()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "one two"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(
            Candidate(13, "one two three four five"),
            now,
            TestContext.Current.CancellationToken);
        now = now.AddMilliseconds(100);
        translator.Calls[0].Complete("first");
        await translator.WaitForCallsAsync(2);
        await WaitUntilAsync(() => output.Translations.Count >= 1);
        Assert.Equal([true], output.Typing);

        now = now.AddMilliseconds(100);
        translator.Calls[1].Complete("second");
        await WaitUntilAsync(() => output.Translations.Count == 2);
        Assert.Equal([true], output.Typing);

        await scheduler.SubmitAsync(
            Candidate(13, "one two three four five", settled: true),
            now,
            TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => output.Typing.Count >= 2);
        Assert.Equal([true, false], output.Typing);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public async Task SettledEquivalentFailureStopsTypingWithoutDuplicateRetry()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(20, "hello everyone"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(
            Candidate(21, "hello everyone", settled: true),
            now,
            TestContext.Current.CancellationToken);
        translator.Calls[0].Fail(
            new OpenAiProviderException("text translation", "failed"));
        await WaitUntilAsync(() =>
            output.Typing.Count >= 2 &&
            output.Typing.LastOrDefault() == false);

        await scheduler.SubmitAsync(
            Candidate(21, "hello everyone", settled: true),
            now,
            TestContext.Current.CancellationToken);
        await scheduler.TickAsync(now.AddSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(translator.Calls);
        Assert.Empty(output.Translations);
        Assert.Equal([true, false], output.Typing);
    }

    [Fact]
    public async Task AcceptedWatermarkNeverMovesBackwardOrPublishesEquivalentTwice()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "one two"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await scheduler.SubmitAsync(
            Candidate(13, "one two three four five"),
            now,
            TestContext.Current.CancellationToken);
        now = now.AddMilliseconds(100);
        translator.Calls[0].Complete("revision 10");
        await translator.WaitForCallsAsync(2);
        translator.Calls[1].Complete("revision 13");
        await WaitUntilAsync(() => output.Translations.Count == 2);

        await scheduler.SubmitAsync(
            Candidate(10, "one two"),
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        await scheduler.SubmitAsync(
            Candidate(13, "one two three four five"),
            now.AddSeconds(1),
            TestContext.Current.CancellationToken);
        translator.Calls[1].Complete("duplicate completion");
        await scheduler.TickAsync(
            now.AddSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(["revision 10", "revision 13"], output.Translations);
        Assert.Equal(2, translator.Calls.Count);
    }

    [Fact]
    public async Task DisposalForcesTypingFalse()
    {
        DateTimeOffset now = Start.AddMilliseconds(100);
        var translator = new ControlledTranslator();
        var output = new Output();
        var scheduler = new RealtimeTranslationScheduler(
            translator, [output], Settings, new Reporter(), getUtcNow: () => now);

        await scheduler.SubmitAsync(
            Candidate(10, "one two"),
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(1);
        await WaitUntilAsync(() => output.Typing.Count >= 1);
        Assert.Equal([true], output.Typing);

        await scheduler.DisposeAsync();
        Assert.Equal([true, false], output.Typing);
        Assert.True(translator.Calls[0].CancellationRequested);
    }

    [Fact]
    public void SchedulingTriggersAreDeterministic()
    {
        TranslationCandidate first = Candidate(
            1,
            "one",
            observed: Start,
            started: Start);
        Assert.Equal(
            RealtimeSchedulingDecision.Wait,
            Decide(first, null, Start.AddMilliseconds(99)));
        Assert.Equal(
            RealtimeSchedulingDecision.MaximumInterval,
            Decide(first, null, Start.AddMilliseconds(500)));

        TranslationCandidate enoughWords = Candidate(
            2,
            "one two",
            observed: Start.AddMilliseconds(100),
            started: Start);
        Assert.Equal(
            RealtimeSchedulingDecision.MinimumChangedWords,
            Decide(enoughWords, null, Start.AddMilliseconds(100)));

        TranslationCandidate punctuation = Candidate(
            2,
            "one.",
            observed: Start.AddMilliseconds(100),
            started: Start);
        Assert.Equal(
            RealtimeSchedulingDecision.Punctuation,
            Decide(punctuation, null, Start.AddMilliseconds(100)));

        Assert.Equal(
            RealtimeSchedulingDecision.Settled,
            Decide(first with { IsSettled = true }, null, Start));
    }

    [Theory]
    [InlineData("", "one two", 2)]
    [InlineData("one two", "one two three", 1)]
    [InlineData("one two", "one four", 1)]
    [InlineData("你好世界", "你好世界更新", 1)]
    public void ChangedWordCountingIsSmallAndUnicodeSafe(
        string previous,
        string current,
        int expected) =>
        Assert.Equal(
            expected,
            RealtimeTranslationPolicy.CountChangedWords(previous, current));

    private static RealtimeSchedulingDecision Decide(
        TranslationCandidate candidate,
        TranslationCandidate? previous,
        DateTimeOffset now) =>
        RealtimeTranslationPolicy.Decide(
            candidate,
            previous,
            previous?.ObservedAt,
            now,
            Settings);

    private static TranslationCandidate Candidate(
        long revision,
        string source,
        long utterance = 1,
        bool settled = false,
        DateTimeOffset? observed = null,
        DateTimeOffset? started = null) =>
        new(
            1,
            utterance,
            revision,
            source,
            false,
            settled,
            revision,
            revision * 80,
            started ?? Start,
            observed ?? Start.AddMilliseconds(100));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 20000 && !condition(); attempt++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.True(condition());
    }

    private sealed class ControlledTranslator(bool ignoreCancellation = false)
        : ITextTranslator
    {
        private int _active;
        private int _maximum;
        public List<Call> Calls { get; } = [];
        public int ActiveCalls => Volatile.Read(ref _active);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);

        public async Task<string> TranslateAsync(
            string sourceText,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            var call = new Call(sourceText, cancellationToken);
            lock (Calls)
                Calls.Add(call);
            try
            {
                return ignoreCancellation
                    ? await call.Completion.Task
                    : await call.Completion.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public async Task WaitForCallsAsync(int count)
        {
            await WaitUntilAsync(() =>
            {
                lock (Calls)
                    return Calls.Count >= count;
            });
        }

        private void UpdateMaximum(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maximum)) &&
                   Interlocked.CompareExchange(ref _maximum, value, current) != current)
            {
            }
        }
    }

    private sealed class Call(string source, CancellationToken cancellationToken)
    {
        public string Source { get; } = source;
        public TaskCompletionSource<string> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationRequested => cancellationToken.IsCancellationRequested;
        public void Complete(string result) => Completion.TrySetResult(result);
        public void Fail(Exception exception) => Completion.TrySetException(exception);
    }

    private sealed class Output : IOutputSink
    {
        public ConcurrentQueue<string> Translations { get; } = new();
        public ConcurrentQueue<bool> Typing { get; } = new();
        public string Name => "test";
        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            if (update.Kind == TranslationUpdateKind.Translation)
                Translations.Enqueue(update.Text!);
            else
                Typing.Enqueue(update.IsTyping);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingTranslationOutput : IOutputSink
    {
        private int _blocked;
        public ConcurrentQueue<string> Translations { get; } = new();
        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "blocked";

        public async Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            if (update.Kind != TranslationUpdateKind.Translation)
                return;
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Blocked.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            Translations.Enqueue(update.Text!);
        }
    }

    private sealed class Reporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public IEnumerable<RealtimeTranslationTelemetry> Telemetry =>
            Events.Select(item => item.TranslationTelemetry)
                .OfType<RealtimeTranslationTelemetry>();
        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }

    private sealed class BlockingReporter : IAppReporter
    {
        private int _blocked;
        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Report(AppEvent appEvent)
        {
            if (appEvent.Kind != AppEventKind.TranslationRequestStarted ||
                Interlocked.Exchange(ref _blocked, 1) != 0)
            {
                return;
            }
            Blocked.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
    }
}
