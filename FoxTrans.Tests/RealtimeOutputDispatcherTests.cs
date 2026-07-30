using System.Collections.Concurrent;
using Xunit;

public sealed class RealtimeOutputDispatcherTests
{
    [Fact]
    public async Task SlowSinkCoalescesToLatestWhileFastSinkContinuesIndependently()
    {
        var slow = new ControlledSink("slow", blockFirstTranslation: true);
        var fast = new ControlledSink("fast");
        var reporter = new Reporter();
        await using var dispatcher = new RealtimeOutputDispatcher(
            [slow, fast],
            reporter);

        dispatcher.SubmitTranslation(Update(10));
        await slow.WaitForInvocationsAsync(1);
        await fast.WaitForDeliveredTranslationsAsync(1);

        dispatcher.SubmitTranslation(Update(11));
        await fast.WaitForDeliveredTranslationsAsync(2);
        dispatcher.SubmitTranslation(Update(12));
        await fast.WaitForDeliveredTranslationsAsync(3);
        dispatcher.SubmitTranslation(Update(13));
        await fast.WaitForDeliveredTranslationsAsync(4);

        RealtimeOutputSinkSnapshot slowState = dispatcher.Snapshot[0];
        Assert.Equal(1, slowState.ActiveCalls);
        Assert.Equal(1, slowState.PendingTranslations);
        Assert.Equal(1, slow.MaximumConcurrentCalls);

        slow.ReleaseFirst();
        await slow.WaitForDeliveredTranslationsAsync(2);
        Assert.Equal(["r10", "r13"], slow.DeliveredTranslations);
        Assert.DoesNotContain("r11", slow.InvokedTranslations);
        Assert.DoesNotContain("r12", slow.InvokedTranslations);
        Assert.Equal(["r10", "r11", "r12", "r13"], fast.DeliveredTranslations);
        Assert.Equal(1, slow.MaximumConcurrentCalls);
        Assert.Contains(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.TranslationCoalesced &&
            item.OutputName == "slow" &&
            item.Revision == 13 &&
            item.CoalescedCount == 2);
    }

    [Fact]
    public async Task TypingControlsStayOrderedAroundLatestCoalescedTranslation()
    {
        var sink = new ControlledSink("ordered", blockFirstOperation: true);
        await using var dispatcher = new RealtimeOutputDispatcher(
            [sink],
            new Reporter());

        dispatcher.SubmitTyping(1, 1, true);
        await sink.WaitForInvocationsAsync(1);
        dispatcher.SubmitTranslation(Update(10));
        dispatcher.SubmitTranslation(Update(11));
        dispatcher.SubmitTranslation(Update(12, settled: true));
        dispatcher.SubmitTyping(1, 1, false);

        Assert.Equal(1, dispatcher.Snapshot.Single().PendingTranslations);
        Assert.Equal(1, dispatcher.Snapshot.Single().PendingControls);
        sink.ReleaseFirst();
        await sink.WaitForDeliveredOperationsAsync(3);

        Assert.Equal(
            ["typing:true", "translation:r12", "typing:false"],
            sink.DeliveredOperations);
        Assert.Equal(1, sink.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task NewUtteranceCancelsActiveAndDiscardsPendingOldTranslation()
    {
        var sink = new ControlledSink(
            "lifecycle",
            blockFirstTranslation: true,
            honorCancellation: true);
        var reporter = new Reporter();
        await using var dispatcher = new RealtimeOutputDispatcher([sink], reporter);

        dispatcher.SubmitTyping(1, 1, true);
        await sink.WaitForDeliveredOperationsAsync(1);
        dispatcher.SubmitTranslation(Update(10, utterance: 1));
        await sink.WaitForInvocationsAsync(2);
        dispatcher.SubmitTranslation(Update(11, utterance: 1));

        dispatcher.InvalidateUtterance(1, 2, queueTypingFalse: true);
        dispatcher.SubmitTyping(1, 2, true);
        dispatcher.SubmitTranslation(Update(1, utterance: 2));

        await sink.WaitForDeliveredTranslationsAsync(1);
        Assert.True(sink.FirstBlockedCancellationRequested);
        Assert.DoesNotContain("r10", sink.DeliveredTranslations);
        Assert.DoesNotContain("r11", sink.InvokedTranslations);
        Assert.Equal(["r1"], sink.DeliveredTranslations);
        Assert.Equal(
            ["typing:true", "typing:false", "typing:true", "translation:r1"],
            sink.DeliveredOperations);
        Assert.Contains(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.DiscardedOldUtterance &&
            item.Revision == 11);
    }

    [Fact]
    public async Task EpochInvalidationCancelsActiveAndRemovesPendingOldTranslation()
    {
        var sink = new ControlledSink(
            "epoch",
            blockFirstTranslation: true,
            honorCancellation: true);
        var reporter = new Reporter();
        await using var dispatcher = new RealtimeOutputDispatcher([sink], reporter);

        dispatcher.SubmitTranslation(Update(10, epoch: 1));
        await sink.WaitForInvocationsAsync(1);
        dispatcher.SubmitTranslation(Update(11, epoch: 1));
        dispatcher.InvalidateEpoch(2, queueTypingFalse: true);
        dispatcher.SubmitTyping(2, 2, true);
        dispatcher.SubmitTranslation(Update(1, epoch: 2, utterance: 2));

        await sink.WaitForDeliveredTranslationsAsync(1);
        Assert.True(sink.FirstBlockedCancellationRequested);
        Assert.Equal(["r1"], sink.DeliveredTranslations);
        Assert.DoesNotContain("r11", sink.InvokedTranslations);
        Assert.Contains(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.DiscardedOldEpoch &&
            item.Revision == 11);
    }

    [Fact]
    public async Task CancellationIgnoringTimeoutQuarantinesOnlyThatSink()
    {
        var timing = new ManualTiming();
        var stuck = new ControlledSink(
            "stuck",
            blockFirstTranslation: true,
            honorCancellation: false);
        var fast = new ControlledSink("fast");
        var reporter = new Reporter();
        var dispatcher = new RealtimeOutputDispatcher(
            [stuck, fast],
            reporter,
            publicationTimeout: TimeSpan.FromSeconds(2),
            cancellationGracePeriod: TimeSpan.FromMilliseconds(100),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            timing: timing.Value);

        dispatcher.SubmitTranslation(Update(1));
        await stuck.WaitForInvocationsAsync(1);
        await fast.WaitForDeliveredTranslationsAsync(1);
        await timing.WaitForPendingAsync(1);
        timing.CompleteNext();
        await WaitUntilAsync(() => stuck.FirstBlockedCancellationRequested);
        await timing.WaitForPendingAsync(1);
        timing.CompleteNext();
        await WaitUntilAsync(() => dispatcher.Snapshot[0].IsQuarantined);

        dispatcher.SubmitTranslation(Update(2));
        await fast.WaitForDeliveredTranslationsAsync(2);
        await Task.Yield();
        Assert.Single(stuck.InvokedTranslations);
        Assert.Equal(1, stuck.MaximumConcurrentCalls);
        Assert.Equal(["r1", "r2"], fast.DeliveredTranslations);
        Assert.Single(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.TimedOut &&
            item.OutputName == "stuck");
        Assert.Single(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.Quarantined &&
            item.OutputName == "stuck");

        await dispatcher.DisposeAsync();
        stuck.ReleaseFirst();
    }

    [Fact]
    public async Task ResponsiveCancellationAfterTimeoutDoesNotQuarantine()
    {
        var timing = new ManualTiming();
        var sink = new ControlledSink(
            "responsive",
            blockFirstTranslation: true,
            honorCancellation: true);
        var reporter = new Reporter();
        await using var dispatcher = new RealtimeOutputDispatcher(
            [sink],
            reporter,
            timing: timing.Value);

        dispatcher.SubmitTranslation(Update(1));
        await sink.WaitForInvocationsAsync(1);
        await timing.WaitForPendingAsync(1);
        timing.CompleteNext();
        await WaitUntilAsync(() => sink.FirstBlockedCancellationRequested);
        await WaitUntilAsync(() => dispatcher.Snapshot.Single().ActiveCalls == 0);

        dispatcher.SubmitTranslation(Update(2));
        await sink.WaitForDeliveredTranslationsAsync(1);
        Assert.Equal(["r2"], sink.DeliveredTranslations);
        Assert.False(dispatcher.Snapshot.Single().IsQuarantined);
        Assert.DoesNotContain(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.Quarantined);
    }

    [Fact]
    public async Task FailureIsReportedAndWorkerContinuesSafely()
    {
        var failing = new ThrowingSink();
        var fast = new ControlledSink("fast");
        var reporter = new Reporter();
        await using var dispatcher = new RealtimeOutputDispatcher(
            [failing, fast],
            reporter);

        dispatcher.SubmitTranslation(Update(1));
        await fast.WaitForDeliveredTranslationsAsync(1);
        await WaitUntilAsync(() => reporter.Events.Any(item =>
            item.Kind == AppEventKind.OutputError));
        dispatcher.SubmitTranslation(Update(2));
        await fast.WaitForDeliveredTranslationsAsync(2);
        await WaitUntilAsync(() => failing.CallCount == 2);

        Assert.Equal(2, failing.CallCount);
        Assert.Equal(1, failing.MaximumConcurrentCalls);
        Assert.False(dispatcher.Snapshot[0].IsQuarantined);
    }

    [Fact]
    public async Task ThousandsOfUpdatesKeepOnePendingSlotAndNoTaskChain()
    {
        var sink = new ControlledSink("bounded", blockFirstTranslation: true);
        var reporter = new Reporter();
        await using var dispatcher = new RealtimeOutputDispatcher([sink], reporter);

        dispatcher.SubmitTranslation(Update(1));
        await sink.WaitForInvocationsAsync(1);
        for (int revision = 2; revision <= 5000; revision++)
            dispatcher.SubmitTranslation(Update(revision));

        RealtimeOutputSinkSnapshot snapshot = dispatcher.Snapshot.Single();
        Assert.Equal(1, snapshot.ActiveCalls);
        Assert.Equal(1, snapshot.PendingTranslations);
        Assert.InRange(snapshot.PendingControls, 0, RealtimeOutputDispatcher.ControlCapacity);
        sink.ReleaseFirst();
        await sink.WaitForDeliveredTranslationsAsync(2);

        Assert.Equal(["r1", "r5000"], sink.DeliveredTranslations);
        RealtimeOutputTelemetry telemetry = Assert.Single(
            reporter.OutputTelemetry,
            item => item.Kind ==
                RealtimeOutputTelemetryKind.TranslationCoalesced);
        Assert.Equal(4998, telemetry.CoalescedCount);
        Assert.Equal(5000, telemetry.Revision);
    }

    [Fact]
    public async Task DisposalAttemptsTypingFalseAndDoesNotOwnSinkDisposal()
    {
        var sink = new DisposableSink();
        var dispatcher = new RealtimeOutputDispatcher([sink], new Reporter());
        dispatcher.SubmitTyping(1, 1, true);
        await sink.WaitForOperationsAsync(1);
        await dispatcher.DisposeAsync();

        Assert.Equal(["typing:true", "typing:false"], sink.Operations);
        Assert.Equal(0, sink.DisposeCount);
        await sink.DisposeAsync();
        Assert.Equal(1, sink.DisposeCount);
        await dispatcher.DisposeAsync();
        Assert.Equal(1, sink.DisposeCount);
    }

    [Fact]
    public async Task DisposalIsBoundedForCancellationIgnoringPublication()
    {
        var timing = new ManualTiming();
        var sink = new ControlledSink(
            "ignored",
            blockFirstOperation: true,
            honorCancellation: false);
        var dispatcher = new RealtimeOutputDispatcher(
            [sink],
            new Reporter(),
            shutdownTimeout: TimeSpan.FromSeconds(1),
            timing: timing.Value);
        dispatcher.SubmitTyping(1, 1, true);
        await sink.WaitForInvocationsAsync(1);
        await timing.WaitForPendingAsync(1);

        Task dispose = dispatcher.DisposeAsync().AsTask();
        timing.CompleteNext();
        await timing.WaitForPendingAsync(1);
        timing.CompleteNext();
        await dispose.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, sink.MaximumConcurrentCalls);
        Assert.Single(sink.Invocations);
        sink.ReleaseFirst();
    }

    [Fact]
    public async Task ControlBoundViolationIsExplicitAndQuarantinesAffectedSink()
    {
        var blocked = new ControlledSink(
            "control overflow",
            blockFirstOperation: true,
            honorCancellation: false);
        var reporter = new Reporter();
        var dispatcher = new RealtimeOutputDispatcher(
            [blocked],
            reporter,
            shutdownTimeout: TimeSpan.FromMilliseconds(100));
        dispatcher.SubmitTyping(1, 1, true);
        await blocked.WaitForInvocationsAsync(1);
        for (int index = 0; index <= RealtimeOutputDispatcher.ControlCapacity; index++)
            dispatcher.SubmitTyping(1, 1, index % 2 == 0);

        await WaitUntilAsync(() => dispatcher.Snapshot[0].IsQuarantined);
        Assert.InRange(
            dispatcher.Snapshot[0].PendingControls,
            0,
            RealtimeOutputDispatcher.ControlCapacity);
        Assert.Contains(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.ControlOverflow &&
            item.OutputName == "control overflow");
        Assert.Contains(reporter.OutputTelemetry, item =>
            item.Kind == RealtimeOutputTelemetryKind.Quarantined &&
            item.OutputName == "control overflow");

        blocked.ReleaseFirst();
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task WorkerSnapshotsRetainConfigurationOrderAndLocalRevisionOrder()
    {
        var first = new ControlledSink("first");
        var second = new ControlledSink("second");
        await using var dispatcher = new RealtimeOutputDispatcher(
            [first, second],
            new Reporter());
        Assert.Equal(
            ["first", "second"],
            dispatcher.Snapshot.Select(item => item.OutputName));

        for (int revision = 1; revision <= 20; revision++)
        {
            dispatcher.SubmitTranslation(Update(revision));
            await first.WaitForDeliveredTranslationsAsync(revision);
            await second.WaitForDeliveredTranslationsAsync(revision);
        }

        Assert.Equal(
            Enumerable.Range(1, 20).Select(value => $"r{value}"),
            first.DeliveredTranslations);
        Assert.Equal(
            Enumerable.Range(1, 20).Select(value => $"r{value}"),
            second.DeliveredTranslations);
    }

    private static RealtimeOutputTranslation Update(
        long revision,
        long epoch = 1,
        long utterance = 1,
        bool settled = false) =>
        new(epoch, utterance, revision, settled, $"r{revision}");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 2000 && !condition(); attempt++)
            await Task.Delay(1, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }

    private sealed class ControlledSink(
        string name,
        bool blockFirstTranslation = false,
        bool blockFirstOperation = false,
        bool honorCancellation = true) : IOutputSink
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        private int _maximum;
        private int _blocked;
        private CancellationToken _blockedCancellation;

        public string Name => name;
        public ConcurrentQueue<TranslationUpdate> Invocations { get; } = new();
        public ConcurrentQueue<TranslationUpdate> Delivered { get; } = new();
        public IEnumerable<string> InvokedTranslations =>
            Invocations.Where(item => item.Kind == TranslationUpdateKind.Translation)
                .Select(item => item.Text!);
        public IEnumerable<string> DeliveredTranslations =>
            Delivered.Where(item => item.Kind == TranslationUpdateKind.Translation)
                .Select(item => item.Text!);
        public IEnumerable<string> DeliveredOperations =>
            Delivered.Select(Format);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);
        public bool FirstBlockedCancellationRequested =>
            _blockedCancellation.IsCancellationRequested;

        public async Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            Invocations.Enqueue(update);
            bool eligibleToBlock =
                blockFirstOperation ||
                blockFirstTranslation &&
                update.Kind == TranslationUpdateKind.Translation;
            bool shouldBlock =
                eligibleToBlock &&
                Interlocked.CompareExchange(ref _blocked, 1, 0) == 0;
            try
            {
                if (shouldBlock)
                {
                    _blockedCancellation = cancellationToken;
                    if (honorCancellation)
                        await _release.Task.WaitAsync(cancellationToken);
                    else
                        await _release.Task;
                }
                Delivered.Enqueue(update);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public void ReleaseFirst() => _release.TrySetResult();

        public async Task WaitForInvocationsAsync(int count) =>
            await WaitUntilAsync(() => Invocations.Count >= count);

        public async Task WaitForDeliveredTranslationsAsync(int count) =>
            await WaitUntilAsync(() => DeliveredTranslations.Count() >= count);

        public async Task WaitForDeliveredOperationsAsync(int count) =>
            await WaitUntilAsync(() => Delivered.Count >= count);

        private void UpdateMaximum(int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref _maximum)) &&
                   Interlocked.CompareExchange(ref _maximum, value, current) != current)
            {
            }
        }

        private static string Format(TranslationUpdate update) =>
            update.Kind == TranslationUpdateKind.Translation
                ? $"translation:{update.Text}"
                : $"typing:{update.IsTyping.ToString().ToLowerInvariant()}";
    }

    private sealed class ThrowingSink : IOutputSink
    {
        private int _active;
        private int _maximum;
        private int _calls;
        public string Name => "throwing";
        public int CallCount => Volatile.Read(ref _calls);
        public int MaximumConcurrentCalls => Volatile.Read(ref _maximum);

        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            Interlocked.Exchange(ref _maximum, Math.Max(_maximum, active));
            Interlocked.Increment(ref _calls);
            Interlocked.Decrement(ref _active);
            throw new InvalidOperationException("expected output failure");
        }
    }

    private sealed class DisposableSink : IOutputSink, IAsyncDisposable
    {
        public string Name => "disposable";
        public ConcurrentQueue<string> Operations { get; } = new();
        public int DisposeCount { get; private set; }

        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            Operations.Enqueue(
                update.Kind == TranslationUpdateKind.Typing
                    ? $"typing:{update.IsTyping.ToString().ToLowerInvariant()}"
                    : $"translation:{update.Text}");
            return Task.CompletedTask;
        }

        public async Task WaitForOperationsAsync(int count) =>
            await WaitUntilAsync(() => Operations.Count >= count);

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualTiming
    {
        private readonly ConcurrentQueue<DelayCall> _calls = new();
        public TimeProvider Value =>
            new DelegateTimeProvider(delay: DelayAsync);

        public async Task WaitForPendingAsync(int count) =>
            await WaitUntilAsync(() =>
                _calls.Count(call => !call.Completion.Task.IsCompleted) >= count);

        public void CompleteNext()
        {
            while (_calls.TryDequeue(out DelayCall? call))
            {
                if (call.Completion.TrySetResult())
                    return;
            }
            throw new InvalidOperationException("No pending delay.");
        }

        private Task DelayAsync(
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));
            _calls.Enqueue(new(completion));
            return completion.Task;
        }

        private sealed record DelayCall(TaskCompletionSource Completion);
    }

    private sealed class Reporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public IEnumerable<RealtimeOutputTelemetry> OutputTelemetry =>
            Events.Select(item => item.OutputTelemetry)
                .OfType<RealtimeOutputTelemetry>();
        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }
}
