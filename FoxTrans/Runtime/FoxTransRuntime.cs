public enum RuntimeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public interface IFoxTransRuntime : IAsyncDisposable
{
    RuntimeState State { get; }
    ResolvedExecutionPlan? ActivePlan { get; }
    Task Completion { get; }

    Task StartAsync(
        ResolvedExecutionPlan plan,
        IAppReporter reporter,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IRuntimePipelineSession : IAsyncDisposable
{
    Task RunAsync(IAppReporter reporter, CancellationToken cancellationToken);
}

public interface IRuntimePipelineSessionFactory
{
    ValueTask<IRuntimePipelineSession> CreateAsync(
        ResolvedExecutionPlan plan,
        CancellationToken cancellationToken);
}

public sealed class ProductionRuntimePipelineSessionFactory : IRuntimePipelineSessionFactory
{
    public ValueTask<IRuntimePipelineSession> CreateAsync(
        ResolvedExecutionPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IRuntimePipelineSession>(
            new ProductionRuntimePipelineSession(plan));
    }
}

public sealed class FoxTransRuntime : IFoxTransRuntime
{
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(5);

    private readonly IRuntimePipelineSessionFactory _sessionFactory;
    private readonly TimeSpan _stopTimeout;
    private readonly SemaphoreSlim _transition = new(1, 1);
    private RuntimeRun? _run;
    private int _state = (int)RuntimeState.Stopped;
    private long _generation;
    private int _disposed;

    public FoxTransRuntime(
        IRuntimePipelineSessionFactory? sessionFactory = null,
        TimeSpan? stopTimeout = null)
    {
        _sessionFactory = sessionFactory ??
            new ProductionRuntimePipelineSessionFactory();
        _stopTimeout = stopTimeout ?? DefaultStopTimeout;
        if (_stopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
    }

    public RuntimeState State => (RuntimeState)Volatile.Read(ref _state);
    public ResolvedExecutionPlan? ActivePlan => Volatile.Read(ref _run)?.Plan;
    public Task Completion => Volatile.Read(ref _run)?.Completion ??
        Task.CompletedTask;

    public async Task StartAsync(
        ResolvedExecutionPlan plan,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(reporter);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        IRuntimePipelineSession? session = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (State is RuntimeState.Starting or RuntimeState.Running or
                RuntimeState.Stopping)
            {
                throw new InvalidOperationException(
                    $"FoxTrans cannot start while the runtime is {State.ToString().ToLowerInvariant()}.");
            }

            RuntimeRun? prior = _run;
            if (prior is not null)
            {
                await prior.Completion.ConfigureAwait(false);
                _run = null;
            }

            long generation = Interlocked.Increment(ref _generation);
            SetState(RuntimeState.Starting, reporter, generation);
            try
            {
                session = await _sessionFactory
                    .CreateAsync(plan, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch
            {
                if (session is not null)
                    await session.DisposeAsync().ConfigureAwait(false);
                SetState(RuntimeState.Stopped, reporter, generation);
                throw;
            }

            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var run = new RuntimeRun(plan, reporter, session, lifetime, generation);
            _run = run;
            SetState(RuntimeState.Running, reporter, generation);
            run.Completion = Task.Run(
                () => ExecuteAsync(run),
                CancellationToken.None);
            session = null;
        }
        finally
        {
            _transition.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeRun? run = _run;
            if (run is null || State == RuntimeState.Stopped)
            {
                Volatile.Write(ref _state, (int)RuntimeState.Stopped);
                return;
            }

            if (run.Completion.IsCompleted)
            {
                await run.Completion.ConfigureAwait(false);
                if (State != RuntimeState.Faulted)
                    SetState(RuntimeState.Stopped, run.Reporter, run.Generation);
                return;
            }

            SetState(RuntimeState.Stopping, run.Reporter, run.Generation);
            run.Cancellation.Cancel();
            try
            {
                await run.Completion
                    .WaitAsync(_stopTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                SetState(
                    RuntimeState.Faulted,
                    run.Reporter,
                    run.Generation,
                    "shutdown_timeout");
                ReportSafely(
                    run.Reporter,
                    AppEvent.FatalError(
                        $"Runtime shutdown exceeded {_stopTimeout.TotalSeconds:F0} seconds."));
                throw;
            }

            if (State != RuntimeState.Faulted)
                SetState(RuntimeState.Stopped, run.Reporter, run.Generation);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task ExecuteAsync(RuntimeRun run)
    {
        Exception? failure = null;
        try
        {
            await run.Session
                .RunAsync(run.Reporter, run.Cancellation.Token)
                .ConfigureAwait(false);
            if (!run.Cancellation.IsCancellationRequested)
            {
                failure = new InvalidOperationException(
                    "The pipeline stopped unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (
            run.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                await run.DisposeResourcesAsync().ConfigureAwait(false);
            }
            catch (Exception disposalFailure)
            {
                failure ??= disposalFailure;
            }
        }

        if (failure is not null)
        {
            string category = FailureCategory(failure);
            SetState(
                RuntimeState.Faulted,
                run.Reporter,
                run.Generation,
                category);
            ReportSafely(run.Reporter, AppEvent.FatalError(SafeFailure(failure)));
        }
        else
        {
            SetState(RuntimeState.Stopped, run.Reporter, run.Generation);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        using var timeout = new CancellationTokenSource(_stopTimeout);
        try
        {
            await StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException)
        {
            RuntimeRun? run = _run;
            run?.Cancellation.Cancel();
        }
        finally
        {
            RuntimeRun? run = _run;
            if (run is not null && run.Completion.IsCompleted)
                await run.Completion.ConfigureAwait(false);
            _transition.Dispose();
        }
    }

    private void SetState(
        RuntimeState state,
        IAppReporter reporter,
        long generation,
        string? failureCategory = null)
    {
        Volatile.Write(ref _state, (int)state);
        ReportSafely(
            reporter,
            AppEvent.RuntimeStateChanged(state, generation, failureCategory));
    }

    private static void ReportSafely(IAppReporter reporter, AppEvent appEvent)
    {
        try
        {
            reporter.Report(appEvent);
        }
        catch
        {
            // A presentation observer cannot break resource ownership.
        }
    }

    private static string FailureCategory(Exception exception) => exception switch
    {
        AudioDeviceSelectionException => "microphone",
        OpenAiProviderException => "provider",
        VoxtralFoxException => "voxtral",
        RealtimeAudioQueueOverflowException => "audio_queue",
        _ => "runtime"
    };

    private static string SafeFailure(Exception exception)
    {
        string value = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return value.Length <= 1000 ? value : value[..1000] + "…";
    }

    private sealed class RuntimeRun(
        ResolvedExecutionPlan plan,
        IAppReporter reporter,
        IRuntimePipelineSession session,
        CancellationTokenSource cancellation,
        long generation)
    {
        private int _resourcesDisposed;

        public ResolvedExecutionPlan Plan { get; } = plan;
        public IAppReporter Reporter { get; } = reporter;
        public IRuntimePipelineSession Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long Generation { get; } = generation;
        public Task Completion { get; set; } = Task.CompletedTask;

        public async ValueTask DisposeResourcesAsync()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) != 0)
                return;
            try
            {
                await Session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}

internal sealed class ProductionRuntimePipelineSession(
    ResolvedExecutionPlan plan) : IRuntimePipelineSession
{
    private int _started;
    private int _disposed;

    public async Task RunAsync(
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A runtime pipeline session can run only once.");

        var outputs = new List<VrChatOscOutput>(plan.Outputs.Count);
        try
        {
            foreach (ResolvedOscEndpoint output in plan.Outputs)
                outputs.Add(new VrChatOscOutput(output));

            switch (plan.PipelineKind)
            {
                case PipelineKind.DirectAudioTranslation:
                    await RunDirectAsync(outputs, reporter, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PipelineKind.BatchTranscriptionTranslation:
                    await RunBatchAsync(outputs, reporter, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case PipelineKind.RealtimeTranscriptionTranslation:
                    await RunRealtimeAsync(outputs, reporter, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new ConfigurationException(
                        "No executable pipeline was selected.");
            }
        }
        finally
        {
            foreach (VrChatOscOutput output in outputs)
                await output.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task RunDirectAsync(
        IReadOnlyList<VrChatOscOutput> outputs,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        using var microphone = new NAudioMicrophoneSource(plan.Audio);
        var metered = new MeteredAudioSource(
            microphone,
            reporter,
            visualSink: reporter as IAudioVisualSink);
        using var segmenter = new WebRtcVadSegmenter(plan.Vad!);
        using var httpClient = new HttpClient();
        await FoxTransApp.RunDirectAudioPipelineAsync(
            metered,
            segmenter,
            new OpenAiAudioTranslator(httpClient, plan.Direct!),
            outputs,
            reporter,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task RunBatchAsync(
        IReadOnlyList<VrChatOscOutput> outputs,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        using var microphone = new NAudioMicrophoneSource(plan.Audio);
        var metered = new MeteredAudioSource(
            microphone,
            reporter,
            visualSink: reporter as IAudioVisualSink);
        using var segmenter = new WebRtcVadSegmenter(plan.Vad!);
        using var httpClient = new HttpClient();
        await FoxTransApp.RunBatchTranscriptionPipelineAsync(
            metered,
            segmenter,
            new OpenAiTranscriber(httpClient, plan.Transcription!),
            new OpenAiTextTranslator(httpClient, plan.Translation!),
            outputs,
            reporter,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task RunRealtimeAsync(
        IReadOnlyList<VrChatOscOutput> outputs,
        IAppReporter reporter,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient();
        var healthClient = new VoxtralFoxHealthClient(httpClient);
        VoxtralHealthInfo health = await healthClient.CheckAsync(
            plan.Voxtral!,
            cancellationToken).ConfigureAwait(false);
        reporter.Report(AppEvent.VoxtralHealthChecked(health));
        reporter.Report(AppEvent.VoxtralConnecting(plan.Voxtral!.RealtimeEndpoint));

        using var microphone = new NAudioMicrophoneSource(plan.Audio);
        var metered = new MeteredAudioSource(
            microphone,
            reporter,
            visualSink: reporter as IAudioVisualSink);
        await using var supervisor = new VoxtralConnectionSupervisor(
            plan.Audio.Format,
            (generation, sent) => new VoxtralFoxTranscriber(
                plan.Voxtral!,
                connectionGeneration: generation,
                audioSent: sent),
            async token =>
            {
                _ = await healthClient.CheckAsync(plan.Voxtral!, token)
                    .ConfigureAwait(false);
            });
        await FoxTransApp.RunRealtimeTranscriptionPipelineAsync(
            metered,
            supervisor,
            new OpenAiTextTranslator(httpClient, plan.Translation!),
            outputs,
            plan.Realtime!,
            reporter,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}
