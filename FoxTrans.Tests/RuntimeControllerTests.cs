using System.Collections.Concurrent;
using Xunit;

public sealed class RuntimeControllerTests
{
    [Fact]
    public async Task StartsFromStoppedAndRejectsDuplicateStart()
    {
        var factory = new FakeFactory();
        var reporter = new Reporter();
        await using var runtime = new FoxTransRuntime(factory);

        await runtime.StartAsync(Plan(PipelineKind.DirectAudioTranslation), reporter, TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.Running, runtime.State);
        Assert.NotNull(runtime.ActivePlan);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.StartAsync(Plan(PipelineKind.DirectAudioTranslation), reporter, TestContext.Current.CancellationToken));
        await runtime.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StopFromRunningIsGracefulAndDuplicateStopIsSafe()
    {
        var factory = new FakeFactory();
        await using var runtime = new FoxTransRuntime(factory);
        await runtime.StartAsync(Plan(PipelineKind.BatchTranscriptionTranslation), new Reporter(), TestContext.Current.CancellationToken);

        await runtime.StopAsync(TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.Stopped, runtime.State);
        Assert.True(runtime.Completion.IsCompleted);
        Assert.Equal(1, factory.Sessions.Single().DisposeCount);
    }

    [Fact]
    public async Task CancellationWhileSessionFactoryIsBlockedReturnsToStopped()
    {
        var factory = new BlockingFactory();
        await using var runtime = new FoxTransRuntime(factory);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        Task start = runtime.StartAsync(
            Plan(PipelineKind.DirectAudioTranslation),
            new Reporter(),
            cancellation.Token);
        await factory.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Equal(RuntimeState.Stopped, runtime.State);
    }

    [Fact]
    public async Task CancellationAfterSessionCreationDisposesThePartialSession()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var factory = new CancelAfterCreationFactory(cancellation);
        await using var runtime = new FoxTransRuntime(factory);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runtime.StartAsync(
                Plan(PipelineKind.DirectAudioTranslation),
                new Reporter(),
                cancellation.Token));

        Assert.Equal(RuntimeState.Stopped, runtime.State);
        Assert.Equal(1, factory.Session.DisposeCount);
        Assert.True(runtime.Completion.IsCompleted);
    }

    [Fact]
    public async Task ProviderFailureMovesToFaultedAndKeepsReadableTypedFailure()
    {
        var factory = new FakeFactory(call => new FakeSession(
            failure: new OpenAiProviderException("text translation", "provider unavailable")));
        var reporter = new Reporter();
        await using var runtime = new FoxTransRuntime(factory);

        await runtime.StartAsync(Plan(PipelineKind.BatchTranscriptionTranslation), reporter, TestContext.Current.CancellationToken);
        await runtime.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeState.Faulted, runtime.State);
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.RuntimeStateChanged &&
            item.Telemetry is RuntimeLifecycleTelemetry
            {
                State: RuntimeState.Faulted,
                FailureCategory: "provider"
            });
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.FatalError &&
            item.Message!.Contains("provider unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RuntimeRestartsAfterNormalStop()
    {
        var factory = new FakeFactory();
        await using var runtime = new FoxTransRuntime(factory);
        var reporter = new Reporter();

        await runtime.StartAsync(Plan(PipelineKind.DirectAudioTranslation), reporter, TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        await runtime.StartAsync(Plan(PipelineKind.DirectAudioTranslation), reporter, TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, factory.Sessions.Count);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.DisposeCount));
    }

    [Fact]
    public async Task RuntimeRestartsAfterHandledFault()
    {
        var factory = new FakeFactory(call => call == 1
            ? new FakeSession(failure: new VoxtralFoxException("server_not_ready", "offline"))
            : new FakeSession());
        await using var runtime = new FoxTransRuntime(factory);
        var reporter = new Reporter();

        await runtime.StartAsync(Plan(PipelineKind.RealtimeTranscriptionTranslation), reporter, TestContext.Current.CancellationToken);
        await runtime.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Faulted, runtime.State);

        await runtime.StartAsync(Plan(PipelineKind.RealtimeTranscriptionTranslation), reporter, TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Running, runtime.State);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(RuntimeState.Stopped, runtime.State);
    }

    [Theory]
    [InlineData(PipelineKind.DirectAudioTranslation)]
    [InlineData(PipelineKind.BatchTranscriptionTranslation)]
    [InlineData(PipelineKind.RealtimeTranscriptionTranslation)]
    public async Task EveryPipelineKindDisposesItsOwnedSessionExactlyOnce(
        PipelineKind kind)
    {
        var factory = new FakeFactory();
        await using var runtime = new FoxTransRuntime(factory);

        await runtime.StartAsync(Plan(kind), new Reporter(), TestContext.Current.CancellationToken);
        await runtime.StopAsync(TestContext.Current.CancellationToken);
        await runtime.DisposeAsync();

        Assert.Equal(1, factory.Sessions.Single().DisposeCount);
        Assert.True(runtime.Completion.IsCompleted);
    }

    [Fact]
    public async Task ApplicationCancellationStopsTheLifetimeWithoutLeakingTask()
    {
        var factory = new FakeFactory();
        await using var runtime = new FoxTransRuntime(factory);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        await runtime.StartAsync(Plan(PipelineKind.RealtimeTranscriptionTranslation), new Reporter(), lifetime.Token);
        lifetime.Cancel();
        await runtime.Completion.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(runtime.Completion.IsCompletedSuccessfully);
        Assert.Equal(RuntimeState.Stopped, runtime.State);
        Assert.Equal(1, factory.Sessions.Single().DisposeCount);
    }

    private static ResolvedExecutionPlan Plan(PipelineKind kind)
    {
        var format = new AudioFormat(16000, 16, 1);
        FoxTransConfig config = AppConfig.Default();
        return new(
            config,
            kind,
            new(0, "Test microphone", format),
            [new("127.0.0.1", 9000, true)]);
    }

    private sealed class FakeFactory(
        Func<int, FakeSession>? create = null) : IRuntimePipelineSessionFactory
    {
        private int _calls;
        public List<FakeSession> Sessions { get; } = [];

        public ValueTask<IRuntimePipelineSession> CreateAsync(
            ResolvedExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FakeSession session = create?.Invoke(Interlocked.Increment(ref _calls)) ??
                new FakeSession();
            Sessions.Add(session);
            return ValueTask.FromResult<IRuntimePipelineSession>(session);
        }
    }

    private sealed class BlockingFactory : IRuntimePipelineSessionFactory
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IRuntimePipelineSession> CreateAsync(
            ResolvedExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class CancelAfterCreationFactory(
        CancellationTokenSource cancellation) : IRuntimePipelineSessionFactory
    {
        public FakeSession Session { get; } = new();

        public ValueTask<IRuntimePipelineSession> CreateAsync(
            ResolvedExecutionPlan plan,
            CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return ValueTask.FromResult<IRuntimePipelineSession>(Session);
        }
    }

    private sealed class FakeSession(
        Exception? failure = null) : IRuntimePipelineSession
    {
        private int _disposed;
        public int DisposeCount => Volatile.Read(ref _disposed);

        public async Task RunAsync(
            IAppReporter reporter,
            CancellationToken cancellationToken)
        {
            if (failure is not null)
                throw failure;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Reporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }
}
