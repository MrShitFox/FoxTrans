using System.Threading.Channels;
using FoxTrans.Desktop.Models;

namespace FoxTrans.Desktop.Services;

public sealed class DesktopEventBridge :
    IAppReporter,
    IAudioVisualSink,
    IAsyncDisposable
{
    public const int EventCapacity = 256;

    private readonly Channel<BridgeMessage> _events =
        Channel.CreateBounded<BridgeMessage>(
            new BoundedChannelOptions(EventCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
                AllowSynchronousContinuations = false
            });
    private readonly CancellationTokenSource _cancellation = new();
    private readonly LatestAudioVisualFrame _audio = new();
    private readonly DesktopSecretRedactor _redactor;
    private readonly object _snapshotGate = new();
    private readonly Func<DateTimeOffset> _getUtcNow;
    private readonly Task _pump;
    private DesktopRuntimeSnapshot _snapshot;
    private AppEvent? _latestPartial;
    private int _partialPending;
    private int _disposed;
    private long _droppedOrCoalesced;

    public DesktopEventBridge(
        PipelineViewDefinition definition,
        ResolvedExecutionPlan? plan = null,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        _getUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        _redactor = new DesktopSecretRedactor(plan);
        _snapshot = DesktopRuntimeSnapshot.Create(definition, _getUtcNow());
        _pump = Task.Run(PumpAsync);
    }

    public DesktopRuntimeSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
                return _snapshot;
        }
    }

    public AudioVisualFrame? LatestAudioFrame => _audio.Latest;
    public long AudioFramesPublished => _audio.PublishedCount;
    public long DroppedOrCoalescedCount =>
        Interlocked.Read(ref _droppedOrCoalesced);
    public int MaximumQueuedEvents => EventCapacity;

    public void Report(AppEvent appEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        appEvent = _redactor.Redact(appEvent);

        if (appEvent.Kind == AppEventKind.LogicalUtteranceUpdated)
        {
            if (Interlocked.Exchange(ref _latestPartial, appEvent) is not null)
                Interlocked.Increment(ref _droppedOrCoalesced);
            if (Interlocked.Exchange(ref _partialPending, 1) == 0 &&
                !_events.Writer.TryWrite(BridgeMessage.Partial))
            {
                Volatile.Write(ref _partialPending, 0);
                Interlocked.Increment(ref _droppedOrCoalesced);
            }
            return;
        }

        if (!_events.Writer.TryWrite(new(appEvent)))
            Interlocked.Increment(ref _droppedOrCoalesced);
    }

    public void Publish(AudioVisualFrame frame)
    {
        if (Volatile.Read(ref _disposed) == 0)
            _audio.Publish(frame);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (BridgeMessage message in _events.Reader
                .ReadAllAsync(_cancellation.Token))
            {
                AppEvent? appEvent = message.IsPartial
                    ? Interlocked.Exchange(ref _latestPartial, null)
                    : message.Event;
                if (message.IsPartial)
                {
                    Volatile.Write(ref _partialPending, 0);
                    if (Volatile.Read(ref _latestPartial) is not null &&
                        Interlocked.Exchange(ref _partialPending, 1) == 0)
                    {
                        _events.Writer.TryWrite(BridgeMessage.Partial);
                    }
                }
                if (appEvent is null)
                    continue;
                lock (_snapshotGate)
                {
                    _snapshot = DesktopRuntimeReducer.Reduce(
                        _snapshot,
                        appEvent,
                        _getUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _events.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }

    private readonly record struct BridgeMessage(
        AppEvent? Event,
        bool IsPartial = false)
    {
        public static BridgeMessage Partial { get; } = new(null, true);
    }
}
