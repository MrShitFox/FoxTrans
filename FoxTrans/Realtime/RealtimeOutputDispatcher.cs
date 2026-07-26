public sealed record RealtimeOutputTranslation(
    long TranscriptEpoch,
    long UtteranceId,
    long Revision,
    bool IsSettled,
    string Text);

public enum RealtimeOutputTelemetryKind
{
    Pending,
    Delivered,
    TranslationCoalesced,
    DiscardedOldUtterance,
    DiscardedOldEpoch,
    TimedOut,
    Quarantined,
    ControlOverflow
}

public sealed record RealtimeOutputTelemetry(
    RealtimeOutputTelemetryKind Kind,
    string OutputName,
    long TranscriptEpoch,
    long UtteranceId,
    long Revision,
    long OperationSequence,
    int CoalescedCount = 0,
    TimeSpan? Timeout = null,
    int OutputIndex = 0,
    TranslationUpdateKind UpdateKind = TranslationUpdateKind.Translation,
    TimeSpan? Duration = null);

public sealed record RealtimeOutputDispatcherTiming(
    Func<TimeSpan, CancellationToken, Task> Delay)
{
    public static RealtimeOutputDispatcherTiming System { get; } = new(Task.Delay);
}

public sealed record RealtimeOutputSinkSnapshot(
    string OutputName,
    int ActiveCalls,
    int PendingTranslations,
    int PendingControls,
    bool IsQuarantined);

public sealed class RealtimeOutputDispatcher : IAsyncDisposable
{
    public static readonly TimeSpan DefaultPublicationTimeout =
        TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultCancellationGracePeriod =
        TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan DefaultShutdownTimeout =
        TimeSpan.FromMilliseconds(2500);
    public const int ControlCapacity = 16;

    private readonly SinkWorker[] _workers;
    private readonly TimeSpan _shutdownTimeout;
    private long _sequence;
    private int _disposed;

    public RealtimeOutputDispatcher(
        IReadOnlyList<IOutputSink> outputs,
        IAppReporter reporter,
        TimeSpan? publicationTimeout = null,
        TimeSpan? cancellationGracePeriod = null,
        TimeSpan? shutdownTimeout = null,
        RealtimeOutputDispatcherTiming? timing = null)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(reporter);
        TimeSpan resolvedPublicationTimeout =
            publicationTimeout ?? DefaultPublicationTimeout;
        TimeSpan resolvedGrace =
            cancellationGracePeriod ?? DefaultCancellationGracePeriod;
        _shutdownTimeout = shutdownTimeout ?? DefaultShutdownTimeout;
        if (resolvedPublicationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(publicationTimeout));
        if (resolvedGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cancellationGracePeriod));
        if (_shutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));

        RealtimeOutputDispatcherTiming resolvedTiming =
            timing ?? RealtimeOutputDispatcherTiming.System;
        _workers = outputs
            .Select((output, index) => new SinkWorker(
                output,
                index,
                reporter,
                resolvedPublicationTimeout,
                resolvedGrace,
                resolvedTiming))
            .ToArray();
    }

    public IReadOnlyList<RealtimeOutputSinkSnapshot> Snapshot =>
        _workers.Select(worker => worker.Snapshot).ToArray();

    public void SubmitTranslation(RealtimeOutputTranslation translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (string.IsNullOrWhiteSpace(translation.Text))
            return;
        long sequence = Interlocked.Increment(ref _sequence);
        foreach (SinkWorker worker in _workers)
            worker.SubmitTranslation(translation, sequence);
    }

    public void SubmitTyping(
        long transcriptEpoch,
        long utteranceId,
        bool isTyping)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        long sequence = Interlocked.Increment(ref _sequence);
        var control = new ControlCommand(
            sequence,
            transcriptEpoch,
            utteranceId,
            TranslationUpdate.Typing(isTyping));
        foreach (SinkWorker worker in _workers)
            worker.SubmitControl(control);
    }

    public void InvalidateUtterance(
        long transcriptEpoch,
        long newUtteranceId,
        bool queueTypingFalse)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        long? typingSequence = queueTypingFalse
            ? Interlocked.Increment(ref _sequence)
            : null;
        foreach (SinkWorker worker in _workers)
        {
            worker.Invalidate(
                transcriptEpoch,
                newUtteranceId,
                epochChanged: false,
                typingSequence);
        }
    }

    public void InvalidateEpoch(long transcriptEpoch, bool queueTypingFalse)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        long? typingSequence = queueTypingFalse
            ? Interlocked.Increment(ref _sequence)
            : null;
        foreach (SinkWorker worker in _workers)
        {
            worker.Invalidate(
                transcriptEpoch,
                0,
                epochChanged: true,
                typingSequence);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        long sequence = Interlocked.Increment(ref _sequence);
        foreach (SinkWorker worker in _workers)
            worker.BeginShutdown(sequence);

        Task allWorkers = Task.WhenAll(_workers.Select(worker => worker.Completion));
        try
        {
            await allWorkers.WaitAsync(_shutdownTimeout);
        }
        catch (TimeoutException)
        {
            foreach (SinkWorker worker in _workers)
                worker.ForceShutdown();
            await allWorkers.WaitAsync(DefaultCancellationGracePeriod);
        }
        finally
        {
            foreach (SinkWorker worker in _workers)
                worker.DisposeSynchronization();
        }
    }

    private sealed class SinkWorker
    {
        private readonly object _gate = new();
        private readonly IOutputSink _output;
        private readonly int _outputIndex;
        private readonly IAppReporter _reporter;
        private readonly TimeSpan _publicationTimeout;
        private readonly TimeSpan _cancellationGracePeriod;
        private readonly RealtimeOutputDispatcherTiming _timing;
        private readonly Queue<ControlCommand> _controls = new(ControlCapacity);
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly CancellationTokenSource _workerCancellation = new();
        private PendingTranslation? _pendingTranslation;
        private ActiveCommand? _active;
        private long _currentEpoch;
        private long _currentUtterance;
        private bool _stopping;
        private bool _quarantined;
        private bool _quarantineReported;

        public SinkWorker(
            IOutputSink output,
            int outputIndex,
            IAppReporter reporter,
            TimeSpan publicationTimeout,
            TimeSpan cancellationGracePeriod,
            RealtimeOutputDispatcherTiming timing)
        {
            _output = output;
            _outputIndex = outputIndex;
            _reporter = reporter;
            _publicationTimeout = publicationTimeout;
            _cancellationGracePeriod = cancellationGracePeriod;
            _timing = timing;
            Completion = Task.Run(RunAsync);
        }

        public Task Completion { get; }

        public RealtimeOutputSinkSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return new(
                        _output.Name,
                        _active is null ? 0 : 1,
                        _pendingTranslation is null ? 0 : 1,
                        _controls.Count,
                        _quarantined);
                }
            }
        }

        public void SubmitTranslation(
            RealtimeOutputTranslation translation,
            long sequence)
        {
            RealtimeOutputTelemetry? discarded = null;
            bool accepted = false;
            lock (_gate)
            {
                if (_stopping || _quarantined)
                    return;
                if (translation.TranscriptEpoch < _currentEpoch)
                {
                    discarded = Telemetry(
                        RealtimeOutputTelemetryKind.DiscardedOldEpoch,
                        translation,
                        sequence);
                }
                else if (translation.TranscriptEpoch == _currentEpoch &&
                         translation.UtteranceId < _currentUtterance)
                {
                    discarded = Telemetry(
                        RealtimeOutputTelemetryKind.DiscardedOldUtterance,
                        translation,
                        sequence);
                }
                else
                {
                    _currentEpoch = Math.Max(_currentEpoch, translation.TranscriptEpoch);
                    if (translation.TranscriptEpoch == _currentEpoch)
                    {
                        _currentUtterance = Math.Max(
                            _currentUtterance,
                            translation.UtteranceId);
                    }

                    int coalesced = _pendingTranslation is null
                        ? 0
                        : _pendingTranslation.CoalescedCount + 1;
                    _pendingTranslation =
                        new(sequence, translation, coalesced);
                    accepted = true;
                    SignalLocked();
                }
            }
            if (discarded is not null)
                ReportSafely(AppEvent.RealtimeOutput(discarded));
            if (accepted)
            {
                ReportSafely(AppEvent.RealtimeOutput(new(
                    RealtimeOutputTelemetryKind.Pending,
                    _output.Name,
                    translation.TranscriptEpoch,
                    translation.UtteranceId,
                    translation.Revision,
                    sequence,
                    OutputIndex: _outputIndex)));
            }
        }

        internal void SubmitControl(ControlCommand control)
        {
            bool overflow = false;
            lock (_gate)
            {
                if (_stopping || _quarantined)
                    return;
                if (_controls.Count == ControlCapacity)
                {
                    overflow = true;
                    QuarantineLocked();
                }
                else
                {
                    _controls.Enqueue(control);
                    SignalLocked();
                }
            }
            if (overflow)
            {
                ReportSafely(AppEvent.RealtimeOutput(new(
                    RealtimeOutputTelemetryKind.ControlOverflow,
                    _output.Name,
                    control.TranscriptEpoch,
                    control.UtteranceId,
                    0,
                    control.Sequence)));
                ReportQuarantineOnce(DispatchCommand.ControlCommand(control));
            }
        }

        public void Invalidate(
            long transcriptEpoch,
            long newUtteranceId,
            bool epochChanged,
            long? typingSequence)
        {
            RealtimeOutputTelemetry? discarded = null;
            bool overflow = false;
            lock (_gate)
            {
                if (_stopping || _quarantined)
                    return;
                _currentEpoch = transcriptEpoch;
                _currentUtterance = newUtteranceId;
                if (_pendingTranslation is { } pending &&
                    (pending.Translation.TranscriptEpoch < transcriptEpoch ||
                     !epochChanged &&
                     pending.Translation.TranscriptEpoch == transcriptEpoch &&
                     pending.Translation.UtteranceId < newUtteranceId))
                {
                    discarded = Telemetry(
                        epochChanged
                            ? RealtimeOutputTelemetryKind.DiscardedOldEpoch
                            : RealtimeOutputTelemetryKind.DiscardedOldUtterance,
                        pending.Translation,
                        pending.Sequence);
                    _pendingTranslation = null;
                }

                if (_active is { IsTranslation: true } active &&
                    (active.TranscriptEpoch < transcriptEpoch ||
                     !epochChanged &&
                     active.TranscriptEpoch == transcriptEpoch &&
                     active.UtteranceId < newUtteranceId))
                {
                    active.Cancellation.Cancel();
                }

                if (typingSequence is long sequence)
                {
                    if (_controls.Count == ControlCapacity)
                    {
                        overflow = true;
                        QuarantineLocked();
                    }
                    else
                    {
                        _controls.Enqueue(new(
                            sequence,
                            transcriptEpoch,
                            newUtteranceId,
                            TranslationUpdate.Typing(false)));
                    }
                }
                SignalLocked();
            }
            if (discarded is not null)
                ReportSafely(AppEvent.RealtimeOutput(discarded));
            if (overflow)
            {
                var control = new ControlCommand(
                    typingSequence!.Value,
                    transcriptEpoch,
                    newUtteranceId,
                    TranslationUpdate.Typing(false));
                ReportSafely(AppEvent.RealtimeOutput(new(
                    RealtimeOutputTelemetryKind.ControlOverflow,
                    _output.Name,
                    transcriptEpoch,
                    newUtteranceId,
                    0,
                    control.Sequence)));
                ReportQuarantineOnce(DispatchCommand.ControlCommand(control));
            }
        }

        public void BeginShutdown(long typingSequence)
        {
            bool overflow = false;
            lock (_gate)
            {
                if (_stopping)
                    return;
                _stopping = true;
                _pendingTranslation = null;
                _active?.Cancellation.Cancel();
                if (!_quarantined)
                {
                    if (_controls.Count == ControlCapacity)
                    {
                        overflow = true;
                        QuarantineLocked();
                    }
                    else
                    {
                        _controls.Enqueue(new(
                            typingSequence,
                            _currentEpoch,
                            _currentUtterance,
                            TranslationUpdate.Typing(false)));
                    }
                }
                SignalLocked();
            }
            if (overflow)
            {
                var command = new ControlCommand(
                    typingSequence,
                    _currentEpoch,
                    _currentUtterance,
                    TranslationUpdate.Typing(false));
                ReportSafely(AppEvent.RealtimeOutput(new(
                    RealtimeOutputTelemetryKind.ControlOverflow,
                    _output.Name,
                    _currentEpoch,
                    _currentUtterance,
                    0,
                    typingSequence)));
                ReportQuarantineOnce(DispatchCommand.ControlCommand(command));
            }
        }

        public void ForceShutdown()
        {
            lock (_gate)
            {
                QuarantineLocked();
                _workerCancellation.Cancel();
                SignalLocked();
            }
        }

        public void DisposeSynchronization()
        {
            _signal.Dispose();
            _workerCancellation.Dispose();
        }

        private async Task RunAsync()
        {
            try
            {
                while (true)
                {
                    await _signal.WaitAsync(_workerCancellation.Token);
                    while (true)
                    {
                        DispatchCommand? command;
                        lock (_gate)
                        {
                            command = TakeNextLocked();
                            if (command is null)
                            {
                                if (_stopping || _quarantined)
                                    return;
                                break;
                            }
                            _active = new ActiveCommand(command);
                        }
                        if (command.IsTranslation && command.CoalescedCount > 0)
                        {
                            ReportSafely(AppEvent.RealtimeOutput(new(
                                RealtimeOutputTelemetryKind.TranslationCoalesced,
                                _output.Name,
                                command.TranscriptEpoch,
                                command.UtteranceId,
                                command.Revision,
                                command.Sequence,
                                command.CoalescedCount)));
                        }

                        bool continueRunning = await PublishAsync(command);
                        lock (_gate)
                        {
                            _active?.Cancellation.Dispose();
                            _active = null;
                            if (!continueRunning)
                                QuarantineLocked();
                        }
                        if (!continueRunning)
                        {
                            ReportQuarantineOnce(command);
                            return;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (
                _workerCancellation.IsCancellationRequested)
            {
            }
        }

        private DispatchCommand? TakeNextLocked()
        {
            if (_quarantined)
                return null;
            ControlCommand? control =
                _controls.Count == 0 ? null : _controls.Peek();
            PendingTranslation? translation = _pendingTranslation;
            if (control is null && translation is null)
                return null;
            if (translation is not null &&
                (control is null || translation.Sequence < control.Sequence))
            {
                _pendingTranslation = null;
                return DispatchCommand.TranslationCommand(translation);
            }

            return DispatchCommand.ControlCommand(_controls.Dequeue());
        }

        private async Task<bool> PublishAsync(DispatchCommand command)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            CancellationTokenSource operationCancellation;
            lock (_gate)
                operationCancellation = _active!.Cancellation;

            Task publish;
            try
            {
                publish = _output.PublishAsync(
                    command.Update,
                    operationCancellation.Token);
            }
            catch (Exception exception)
            {
                ReportSafely(AppEvent.OutputError(_output.Name, exception.Message));
                return true;
            }

            using var timeoutCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    _workerCancellation.Token);
            Task timeout = _timing.Delay(
                _publicationTimeout,
                timeoutCancellation.Token);
            Task first = await Task.WhenAny(publish, timeout);
            if (ReferenceEquals(first, publish))
            {
                timeoutCancellation.Cancel();
                await ObservePublishAsync(publish, operationCancellation.Token);
                stopwatch.Stop();
                if (publish.Status == TaskStatus.RanToCompletion)
                {
                    ReportSafely(AppEvent.RealtimeOutput(new(
                        RealtimeOutputTelemetryKind.Delivered,
                        _output.Name,
                        command.TranscriptEpoch,
                        command.UtteranceId,
                        command.Revision,
                        command.Sequence,
                        command.CoalescedCount,
                        OutputIndex: _outputIndex,
                        UpdateKind: command.Update.Kind,
                        Duration: stopwatch.Elapsed)));
                }
                return true;
            }

            if (_workerCancellation.IsCancellationRequested)
                return false;
            await timeout;
            operationCancellation.Cancel();
            ReportSafely(AppEvent.RealtimeOutput(new(
                RealtimeOutputTelemetryKind.TimedOut,
                _output.Name,
                command.TranscriptEpoch,
                command.UtteranceId,
                command.Revision,
                command.Sequence,
                command.CoalescedCount,
                _publicationTimeout)));

            if (publish.IsCompleted)
            {
                await ObservePublishAsync(publish, operationCancellation.Token);
                stopwatch.Stop();
                if (publish.Status == TaskStatus.RanToCompletion)
                {
                    ReportSafely(AppEvent.RealtimeOutput(new(
                        RealtimeOutputTelemetryKind.Delivered,
                        _output.Name,
                        command.TranscriptEpoch,
                        command.UtteranceId,
                        command.Revision,
                        command.Sequence,
                        command.CoalescedCount,
                        OutputIndex: _outputIndex,
                        UpdateKind: command.Update.Kind,
                        Duration: stopwatch.Elapsed)));
                }
                return true;
            }

            Task grace = _timing.Delay(
                _cancellationGracePeriod,
                _workerCancellation.Token);
            first = await Task.WhenAny(publish, grace);
            if (ReferenceEquals(first, publish))
            {
                await ObservePublishAsync(publish, operationCancellation.Token);
                return true;
            }

            _ = publish.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }

        private async Task ObservePublishAsync(
            Task publish,
            CancellationToken operationCancellation)
        {
            try
            {
                await publish;
            }
            catch (OperationCanceledException) when (
                operationCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ReportSafely(AppEvent.OutputError(_output.Name, exception.Message));
            }
        }

        private void QuarantineLocked()
        {
            _quarantined = true;
            _pendingTranslation = null;
            _controls.Clear();
            _active?.Cancellation.Cancel();
            SignalLocked();
        }

        private void ReportQuarantineOnce(DispatchCommand command)
        {
            bool report;
            lock (_gate)
            {
                report = !_quarantineReported;
                _quarantineReported = true;
            }
            if (report)
            {
                ReportSafely(AppEvent.RealtimeOutput(new(
                    RealtimeOutputTelemetryKind.Quarantined,
                    _output.Name,
                    command.TranscriptEpoch,
                    command.UtteranceId,
                    command.Revision,
                    command.Sequence,
                    command.CoalescedCount,
                    _publicationTimeout)));
            }
        }

        private void SignalLocked()
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }

        private RealtimeOutputTelemetry Telemetry(
            RealtimeOutputTelemetryKind kind,
            RealtimeOutputTranslation translation,
            long sequence) =>
            new(
                kind,
                _output.Name,
                translation.TranscriptEpoch,
                translation.UtteranceId,
                translation.Revision,
                sequence,
                OutputIndex: _outputIndex);

        private void ReportSafely(AppEvent appEvent)
        {
            try
            {
                _reporter.Report(appEvent);
            }
            catch
            {
                // Reporting is isolated from output dispatch.
            }
        }

        private sealed record PendingTranslation(
            long Sequence,
            RealtimeOutputTranslation Translation,
            int CoalescedCount);

        private sealed record DispatchCommand(
            long Sequence,
            long TranscriptEpoch,
            long UtteranceId,
            long Revision,
            int CoalescedCount,
            bool IsSettled,
            bool IsTranslation,
            TranslationUpdate Update)
        {
            public static DispatchCommand TranslationCommand(
                PendingTranslation pending) =>
                new(
                    pending.Sequence,
                    pending.Translation.TranscriptEpoch,
                    pending.Translation.UtteranceId,
                    pending.Translation.Revision,
                    pending.CoalescedCount,
                    pending.Translation.IsSettled,
                    true,
                    TranslationUpdate.Translated(pending.Translation.Text));

            public static DispatchCommand ControlCommand(
                RealtimeOutputDispatcher.ControlCommand control) =>
                new(
                    control.Sequence,
                    control.TranscriptEpoch,
                    control.UtteranceId,
                    0,
                    0,
                    false,
                    false,
                    control.Update);
        }

        private sealed class ActiveCommand(DispatchCommand command)
        {
            public long TranscriptEpoch { get; } = command.TranscriptEpoch;
            public long UtteranceId { get; } = command.UtteranceId;
            public bool IsTranslation { get; } = command.IsTranslation;
            public CancellationTokenSource Cancellation { get; } = new();
        }
    }

    internal sealed record ControlCommand(
        long Sequence,
        long TranscriptEpoch,
        long UtteranceId,
        TranslationUpdate Update);
}
