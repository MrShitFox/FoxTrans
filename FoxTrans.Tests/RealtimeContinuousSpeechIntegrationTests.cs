using System.Collections.Concurrent;
using Xunit;

public sealed class RealtimeContinuousSpeechIntegrationTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwentySecondsOfUninterruptedSpeechPublishesLaggingFullWindows()
    {
        const int maxSourceCharacters = 48;
        DateTimeOffset now = Start;
        LogicalUtteranceState tracker = LogicalUtteranceState.Initial;
        var candidates = new Dictionary<long, TranslationCandidate>();
        var requestedRevisions = new List<long>();
        var translator = new ControlledTranslator(
            source =>
            {
                long revision = candidates.Single(item =>
                    item.Value.SourceText == source).Key;
                requestedRevisions.Add(revision);
            },
            ignoreCancellation: true);
        var slowOutput = new BlockingFirstTranslationOutput();
        var output = new Output();
        var reporter = new Reporter();
        await using var scheduler = new RealtimeTranslationScheduler(
            translator,
            [slowOutput, output],
            new ResolvedRealtimeSettings(
                100,
                500,
                2,
                1000,
                maxSourceCharacters),
            reporter,
            getUtcNow: () => now);

        string cumulative = "";
        for (int second = 0; second <= 20; second++)
        {
            now = Start.AddSeconds(second);
            cumulative +=
                (cumulative.Length == 0 ? "" : " ") +
                $"word{second:D2} detail{second:D2}";
            UtteranceTransition transition = UtteranceTracking.ReducePartial(
                tracker,
                new StreamingPartialTranscript(
                    second + 1,
                    cumulative,
                    (second + 1) * 1000),
                now,
                maxSourceCharacters);
            tracker = transition.State;
            TranslationCandidate candidate = transition.Candidate!;
            candidates[candidate.Revision] = candidate;
            Assert.Equal(
                TranslationWindows.Build(
                    tracker.ActiveUtteranceText,
                    maxSourceCharacters),
                new BoundedTextWindow(
                    candidate.SourceText,
                    candidate.SourceWasTruncated));

            await scheduler.SubmitAsync(
                candidate,
                now,
                TestContext.Current.CancellationToken);
            if (second == 0)
            {
                now = now.AddMilliseconds(100);
                await scheduler.TickAsync(
                    now,
                    TestContext.Current.CancellationToken);
                await translator.WaitForCallsAsync(1);
            }

            if (candidate.Revision % 3 == 0)
            {
                int currentCallCount = translator.Calls.Count;
                ControlledTranslator.Call active = translator.Calls[^1];
                long requestedRevision = candidates.Single(item =>
                    item.Value.SourceText == active.Source).Key;
                Assert.True(requestedRevision < candidate.Revision);
                active.Complete($"translation r{requestedRevision}");
                await translator.WaitForCallsAsync(currentCallCount + 1);
                await SpinUntilAsync(() =>
                    output.Translations.Contains(
                        $"translation r{requestedRevision}"));
                if (requestedRevision == 1)
                {
                    await slowOutput.Blocked.Task.WaitAsync(
                        TestContext.Current.CancellationToken);
                }
            }
        }

        Assert.Equal(1, tracker.UtteranceId);
        Assert.True(now - tracker.UtteranceStartedAt!.Value >= TimeSpan.FromSeconds(20));
        Assert.Equal([1, 3, 6, 9, 12, 15, 18, 21], requestedRevisions);
        Assert.Equal(1, translator.MaximumConcurrentCalls);
        Assert.Equal(7, output.Translations.Count);
        Assert.All(
            reporter.Telemetry.Where(item =>
                item.Disposition ==
                TranslationCompletionDisposition.PublishedIntermediate),
            item => Assert.True(item.CurrentRevision > item.RequestedRevision));

        UtteranceTransition settled = UtteranceTracking.CheckInactivity(
            tracker,
            now.AddMilliseconds(1000),
            1000,
            maxSourceCharacters);
        Assert.Equal(UtteranceTransitionKind.Settled, settled.Kind);
        tracker = settled.State;
        now = now.AddMilliseconds(1000);
        await scheduler.SubmitAsync(
            settled.Candidate!,
            now,
            TestContext.Current.CancellationToken);
        Assert.Equal(8, translator.Calls.Count);
        translator.Calls[^1].Complete("translation r21 final");
        await SpinUntilAsync(() =>
            output.Translations.Contains("translation r21 final") &&
            output.Typing.LastOrDefault() == false);
        await slowOutput.Blocked.Task.WaitAsync(
            TestContext.Current.CancellationToken);
        slowOutput.Release.TrySetResult();
        await SpinUntilAsync(() =>
            slowOutput.Translations.Count == 2 &&
            slowOutput.Typing.LastOrDefault() == false);
        Assert.Equal(
            ["translation r1", "translation r21 final"],
            slowOutput.Translations);

        Assert.Equal(8, translator.Calls.Count);
        Assert.Equal(1, translator.MaximumConcurrentCalls);
        Assert.Equal(
            TranslationCompletionDisposition.PublishedFinal,
            reporter.Telemetry.Single(item =>
                item.RequestedRevision == 21).Disposition);
        Assert.Equal(
            requestedRevisions.Order().ToArray(),
            requestedRevisions.ToArray());
        Assert.All(translator.Calls, call =>
        {
            TranslationCandidate requested = candidates.Single(item =>
                item.Value.SourceText == call.Source).Value;
            Assert.Equal(
                TranslationWindows.Build(
                    requested.SourceText,
                    maxSourceCharacters).Text,
                call.Source);
            Assert.True(
                call.Source.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries).Length > 1);
        });

        now = now.AddMilliseconds(100);
        cumulative += ".";
        UtteranceTransition amendment = UtteranceTracking.ReducePartial(
            tracker,
            new StreamingPartialTranscript(22, cumulative, 22000),
            now,
            maxSourceCharacters);
        tracker = amendment.State;
        Assert.Equal(UtteranceTransitionKind.LateAmendment, amendment.Kind);
        candidates[amendment.Candidate!.Revision] = amendment.Candidate;
        await scheduler.SubmitAsync(
            amendment.Candidate,
            now,
            TestContext.Current.CancellationToken);
        await translator.WaitForCallsAsync(9);

        now = now.AddMilliseconds(100);
        cumulative += " next utterance words";
        UtteranceTransition next = UtteranceTracking.ReducePartial(
            tracker,
            new StreamingPartialTranscript(23, cumulative, 23000),
            now,
            maxSourceCharacters);
        tracker = next.State;
        Assert.Equal(UtteranceTransitionKind.Started, next.Kind);
        Assert.Equal(2, next.Candidate!.UtteranceId);
        candidates[next.Candidate.Revision] = next.Candidate;
        await scheduler.SubmitAsync(
            next.Candidate,
            now,
            TestContext.Current.CancellationToken);

        now = now.AddMilliseconds(100);
        translator.Calls[8].Complete("obsolete utterance one result");
        await translator.WaitForCallsAsync(10);
        await SpinUntilAsync(() => reporter.Telemetry.Any(item =>
            item.UtteranceId == 1 &&
            item.RequestedRevision == amendment.Candidate.Revision));
        Assert.DoesNotContain("obsolete utterance one result", output.Translations);
        Assert.Equal(
            TranslationCompletionDisposition.DiscardedOldUtterance,
            reporter.Telemetry.Single(item =>
                item.UtteranceId == 1 &&
                item.RequestedRevision == amendment.Candidate.Revision).Disposition);

        translator.Calls[9].Complete("utterance two translation");
        await SpinUntilAsync(() =>
            output.Translations.Contains("utterance two translation"));
        Assert.Equal(1, translator.MaximumConcurrentCalls);
    }

    private static async Task SpinUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 20000 && !condition(); attempt++)
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
        Assert.True(condition());
    }

    private sealed class ControlledTranslator(
        Action<string> onStarted,
        bool ignoreCancellation = false)
        : ITextTranslator
    {
        private int _active;
        private int _maximum;
        public List<Call> Calls { get; } = [];
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);

        public async Task<string> TranslateAsync(
            string sourceText,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            var call = new Call(sourceText);
            lock (Calls)
                Calls.Add(call);
            onStarted(sourceText);
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

        public async Task WaitForCallsAsync(int count) =>
            await SpinUntilAsync(() =>
            {
                lock (Calls)
                    return Calls.Count >= count;
            });

        private void UpdateMaximum(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maximum)) &&
                   Interlocked.CompareExchange(ref _maximum, value, current) != current)
            {
            }
        }

        public sealed class Call(string source)
        {
            public string Source { get; } = source;
            public TaskCompletionSource<string> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Complete(string result) => Completion.TrySetResult(result);
        }
    }

    private sealed class Output : IOutputSink
    {
        public ConcurrentQueue<string> Translations { get; } = new();
        public ConcurrentQueue<bool> Typing { get; } = new();
        public string Name => "integration";

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

    private sealed class BlockingFirstTranslationOutput : IOutputSink
    {
        private int _blocked;
        public ConcurrentQueue<string> Translations { get; } = new();
        public ConcurrentQueue<bool> Typing { get; } = new();
        public TaskCompletionSource Blocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "slow integration";

        public async Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            if (update.Kind == TranslationUpdateKind.Typing)
            {
                Typing.Enqueue(update.IsTyping);
                return;
            }
            if (Interlocked.CompareExchange(ref _blocked, 1, 0) == 0)
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
}
