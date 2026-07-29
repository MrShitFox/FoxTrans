using System.Threading.Channels;
using Spectre.Console;
using Spectre.Console.Rendering;

public enum UiMode { Auto, Rich, Plain }

public sealed record TerminalDetection(
    bool OutputRedirected,
    bool Interactive,
    bool Ansi,
    bool Unicode,
    TerminalViewport Viewport);

public interface ITerminalEnvironment
{
    TerminalDetection Detect();
    void Restore();
}

public sealed class SystemTerminalEnvironment : ITerminalEnvironment
{
    public TerminalDetection Detect()
    {
        bool outputRedirected;
        try
        {
            outputRedirected = Console.IsOutputRedirected;
        }
        catch
        {
            return new(true, false, false, false, new(0, 0));
        }

        int width = 0;
        int height = 0;
        try
        {
            width = Console.WindowWidth;
            height = Console.WindowHeight;
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentOutOfRangeException or ObjectDisposedException or
            InvalidOperationException)
        {
        }
        bool interactive = !outputRedirected && width > 0 && height > 0;
        bool unicode = false;
        try
        {
            unicode = Environment.GetEnvironmentVariable("WT_SESSION") is not null ||
                Console.OutputEncoding.CodePage == 65001;
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentOutOfRangeException or ObjectDisposedException or
            InvalidOperationException)
        {
        }
        return new(
            outputRedirected,
            interactive,
            interactive,
            unicode,
            new(width, height));
    }

    public void Restore()
    {
        try { Console.ResetColor(); } catch { }
        try { Console.CursorVisible = true; } catch { }
    }
}

public sealed record UiModeSelection(
    UiMode EffectiveMode,
    TuiRenderCapabilities Capabilities,
    TerminalViewport Viewport,
    string? Warning = null);

public static class UiModeSelector
{
    public static UiModeSelection Select(UiMode requested, TerminalDetection terminal)
    {
        bool usable = terminal.Interactive &&
            !terminal.OutputRedirected &&
            terminal.Ansi &&
            terminal.Viewport.Width >= 30 &&
            terminal.Viewport.Height >= 8;
        if (requested == UiMode.Plain)
            return Plain(terminal);
        if (usable)
        {
            return new(
                UiMode.Rich,
                new(true, terminal.Unicode),
                terminal.Viewport);
        }
        return new(
            UiMode.Plain,
            new(false, false),
            terminal.Viewport,
            requested == UiMode.Rich
                ? "Rich UI requested, but this terminal is redirected, unavailable, or too small; using plain output."
                : null);
    }

    private static UiModeSelection Plain(TerminalDetection terminal) =>
        new(UiMode.Plain, new(false, false), terminal.Viewport);
}

public sealed class PipelineTuiHost : IAppReporter, IAsyncDisposable, IDisposable
{
    private readonly object _gate = new();
    private readonly IPipelineTuiRenderer _renderer;
    private readonly IAnsiConsole _console;
    private readonly TextWriter _plain;
    private readonly ITerminalEnvironment _terminal;
    private readonly SemaphoreSlim _invalidation = new(0, 1);
    private int _invalidationPending;
    private readonly Channel<AppEvent> _plainEvents = Channel.CreateBounded<AppEvent>(
        new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _uiCancellation = new();
    private readonly Task _renderTask;
    private PipelineTuiState _state;
    private UiMode _mode;
    private TuiRenderCapabilities _capabilities;
    private int _disposed;
    private int _plainWarningEmitted;

    public PipelineTuiHost(
        PipelineViewDefinition definition,
        UiMode requestedMode,
        IAnsiConsole console,
        TextWriter? plainWriter = null,
        ITerminalEnvironment? terminal = null,
        IPipelineTuiRenderer? renderer = null,
        Func<DateTimeOffset>? getUtcNow = null)
    {
        _terminal = terminal ?? new SystemTerminalEnvironment();
        _renderer = renderer ?? new PipelineTuiRenderer();
        _console = console;
        _plain = plainWriter ?? Console.Out;
        GetUtcNow = getUtcNow ?? (() => DateTimeOffset.UtcNow);
        UiModeSelection selection = UiModeSelector.Select(requestedMode, _terminal.Detect());
        _mode = selection.EffectiveMode;
        _capabilities = selection.Capabilities;
        _state = PipelineTuiState.Create(definition, GetUtcNow());
        if (selection.Warning is not null)
            _plain.WriteLine($"FoxTrans UI warning: {selection.Warning}");
        _renderTask = _mode == UiMode.Rich
            ? Task.Run(RichLoopAsync)
            : Task.Run(PlainLoopAsync);
    }

    private Func<DateTimeOffset> GetUtcNow { get; }
    public UiMode EffectiveMode => _mode;
    public PipelineTuiState Snapshot
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public void Report(AppEvent appEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        lock (_gate)
            _state = PipelineTuiReducer.Reduce(_state, appEvent, GetUtcNow());
        if (_mode == UiMode.Plain)
            _plainEvents.Writer.TryWrite(appEvent);
        else
            Signal();
    }

    private async Task RichLoopAsync()
    {
        try
        {
            TerminalDetection first = _terminal.Detect();
            IRenderable initial = _renderer.Render(RenderSnapshot(), first.Viewport, _capabilities);
            await _console.Live(initial).AutoClear(false).StartAsync(async context =>
            {
                DateTimeOffset lastFrame = DateTimeOffset.MinValue;
                while (!_uiCancellation.IsCancellationRequested)
                {
                    PipelineTuiState snapshot = Snapshot;
                    bool animate = IsAnimating(snapshot);
                    TimeSpan wake = animate
                        ? TimeSpan.FromMilliseconds(166)
                        : TimeSpan.FromSeconds(12);
                    try
                    {
                        _ = await _invalidation.WaitAsync(
                            wake,
                            _uiCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    while (_invalidation.CurrentCount > 0)
                        _invalidation.Wait(0);
                    Volatile.Write(ref _invalidationPending, 0);
                    TimeSpan remaining = TimeSpan.FromMilliseconds(166) -
                        (GetUtcNow() - lastFrame);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, _uiCancellation.Token);
                    TerminalDetection detection = _terminal.Detect();
                    if (!detection.Interactive ||
                        detection.Viewport.Width <= 0 ||
                        detection.Viewport.Height <= 0)
                    {
                        SwitchToPlain("Rich terminal became unavailable; continuing in plain mode.");
                        break;
                    }
                    context.UpdateTarget(_renderer.Render(
                        RenderSnapshot(),
                        detection.Viewport,
                        _capabilities with { Unicode = detection.Unicode }));
                    context.Refresh();
                    lastFrame = GetUtcNow();
                }
                context.UpdateTarget(_renderer.Render(
                    RenderSnapshot(),
                    _terminal.Detect().Viewport,
                    _capabilities));
                context.Refresh();
            });
        }
        catch (OperationCanceledException) when (_uiCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentOutOfRangeException or
            ObjectDisposedException or InvalidOperationException)
        {
            SwitchToPlain($"Rich renderer failed ({exception.GetType().Name}); continuing in plain mode.");
            await PlainLoopAsync();
        }
        finally
        {
            _terminal.Restore();
        }
    }

    private async Task PlainLoopAsync()
    {
        try
        {
            await foreach (AppEvent appEvent in _plainEvents.Reader
                .ReadAllAsync(_uiCancellation.Token))
            {
                string? line = PlainEventFormatter.Format(appEvent, GetUtcNow());
                if (line is not null)
                    await _plain.WriteLineAsync(line);
            }
        }
        catch (OperationCanceledException) when (_uiCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SwitchToPlain(string warning)
    {
        _mode = UiMode.Plain;
        _capabilities = new(false, false);
        _terminal.Restore();
        if (Interlocked.Exchange(ref _plainWarningEmitted, 1) == 0)
            _plain.WriteLine($"FoxTrans UI warning: {warning}");
    }

    private void Signal()
    {
        if (Interlocked.Exchange(ref _invalidationPending, 1) == 0)
        {
            try { _invalidation.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static bool IsAnimating(PipelineTuiState state) =>
        state.Nodes.Values.Any(node => node.Status is
            PipelineNodeStatus.Starting or PipelineNodeStatus.Listening or
            PipelineNodeStatus.Receiving or PipelineNodeStatus.Recording or
            PipelineNodeStatus.Buffering or PipelineNodeStatus.Queued or
            PipelineNodeStatus.Active or
            PipelineNodeStatus.Publishing or
            PipelineNodeStatus.Reconnecting ||
            node.Status == PipelineNodeStatus.Waiting &&
            node.Detail?.Contains("pending", StringComparison.OrdinalIgnoreCase) == true) ||
        state.Edges.Values.Any(edge => edge.LastActivity is not null &&
            GetUtcNowStatic() - edge.LastActivity.Value <= TimeSpan.FromMilliseconds(1500));

    private static DateTimeOffset GetUtcNowStatic() => DateTimeOffset.UtcNow;

    private PipelineTuiState RenderSnapshot() =>
        Snapshot with { LastUpdated = GetUtcNow() };

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_mode == UiMode.Plain)
        {
            _plainEvents.Writer.TryComplete();
            try { await _renderTask; } catch (OperationCanceledException) { }
            _uiCancellation.Cancel();
        }
        else
        {
            Signal();
            await Task.Delay(20);
            _plainEvents.Writer.TryComplete();
            _uiCancellation.Cancel();
            try { await _renderTask; } catch (OperationCanceledException) { }
        }
        _terminal.Restore();
        _uiCancellation.Dispose();
        _invalidation.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

public static class PlainEventFormatter
{
    public static string? Format(AppEvent appEvent, DateTimeOffset timestamp)
    {
        if (appEvent.Kind == AppEventKind.Telemetry &&
            appEvent.Telemetry is AudioLevelTelemetry)
            return null;
        if (appEvent.Kind == AppEventKind.OutputDelivery &&
            appEvent.Telemetry is OutputDeliveryTelemetry
            {
                Phase: OutputDeliveryPhase.Delivered,
                UpdateKind: TranslationUpdateKind.Typing
            })
            return null;
        if (appEvent.Kind == AppEventKind.Telemetry &&
            appEvent.Telemetry is OutputDeliveryTelemetry
            {
                UpdateKind: TranslationUpdateKind.Typing
            })
            return null;
        (string level, string stage, string message) = Describe(appEvent);
        message = PipelineTuiRenderer.Clip(message, 500);
        return $"{timestamp:HH:mm:ss.fff}  {level,-7} {stage,-18} {message}";
    }

    private static (string Level, string Stage, string Message) Describe(AppEvent appEvent)
    {
        string message = appEvent.Message ?? appEvent.Kind.ToString();
        return appEvent.Kind switch
        {
            AppEventKind.Listening => ("INFO", "microphone", "listening"),
            AppEventKind.SpeechStarted => ("ACTIVE", "vad", "speech detected"),
            AppEventKind.SegmentCompleted => ("DONE", "segment",
                $"{appEvent.Duration?.TotalMilliseconds:F0} ms"),
            AppEventKind.ShortPhraseIgnored => ("WARN", "vad",
                $"short phrase ignored ({appEvent.Duration?.TotalMilliseconds:F0} ms)"),
            AppEventKind.ProcessingStarted => ("ACTIVE", "audio-llm", "WAV request"),
            AppEventKind.ProcessingCompleted => ("DONE", "provider", "processing complete"),
            AppEventKind.TranscriptionStarted => ("ACTIVE", "transcription", "request"),
            AppEventKind.TranscriptionCompleted => ("SOURCE", "transcription", message),
            AppEventKind.TextTranslationStarted => ("ACTIVE", "translation", "request"),
            AppEventKind.TranslationCompleted or AppEventKind.RealtimeTranslationAccepted =>
                ("RESULT", "translation", message),
            AppEventKind.OutputDelivery when appEvent.Telemetry is OutputDeliveryTelemetry
                { Phase: OutputDeliveryPhase.Pending } pending => ("FLOW",
                    pending.NodeId.Value, "pending for sink"),
            AppEventKind.OutputDelivery => ("DONE",
                (appEvent.Telemetry as OutputDeliveryTelemetry)?.NodeId.Value ?? "output",
                message),
            AppEventKind.ApiError or AppEventKind.RealtimeTranslationFailed =>
                ("ERROR", "provider", message),
            AppEventKind.OutputError => ("ERROR", "output", message),
            AppEventKind.QueueOverflow => ("WARN", "queue", message),
            AppEventKind.VoxtralConnecting => ("ACTIVE", "voxtral", message),
            AppEventKind.VoxtralReconnectScheduled or AppEventKind.VoxtralReconnectAttempt =>
                ("WARN", "voxtral", message),
            AppEventKind.VoxtralConnectionLost => ("ERROR", "voxtral", message),
            AppEventKind.VoxtralSessionStarted or AppEventKind.VoxtralReconnected =>
                ("DONE", "voxtral", message),
            AppEventKind.LogicalUtteranceStarted or AppEventKind.LogicalUtteranceUpdated =>
                ("FLOW", "utterance", message),
            AppEventKind.LogicalUtteranceSettled => ("DONE", "utterance", message),
            AppEventKind.TranslationRequestStarted => ("ACTIVE", "translation", message),
            AppEventKind.TranslationRequestCoalesced or
            AppEventKind.RealtimeOutputTranslationCoalesced =>
                ("WARN", "coalescing", message),
            AppEventKind.RealtimeOutputTimedOut or
            AppEventKind.RealtimeOutputQuarantined or
            AppEventKind.RealtimeOutputControlOverflow =>
                ("ERROR", "output", message),
            AppEventKind.ConfigWarning or AppEventKind.VoxtralWarning =>
                ("WARN", "system", message),
            AppEventKind.FatalError => ("ERROR", "fatal", message),
            AppEventKind.Stopped => ("DONE", "system", "stopped"),
            AppEventKind.Telemetry when appEvent.Telemetry is QueueTelemetry queue =>
                ("FLOW", "segment-queue",
                    $"{queue.Count}/{queue.Capacity}" +
                    (queue.Backpressured ? " backpressure" : "")),
            AppEventKind.Telemetry when appEvent.Telemetry is OutputDeliveryTelemetry delivery =>
                ("FLOW", delivery.NodeId.Value, "pending for sink"),
            _ => ("INFO", "system", message)
        };
    }
}
