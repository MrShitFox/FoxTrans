namespace FoxTrans.Desktop.Models;

public enum VoiceVisualizationMode
{
    Idle,
    Listening,
    Speech,
    Processing,
    Success,
    Error,
    Stopping
}

public enum DesktopNotificationSeverity
{
    Information,
    Warning,
    Error
}

public sealed record DesktopNotification(
    DesktopNotificationSeverity Severity,
    string Title,
    string Detail,
    DateTimeOffset ObservedAt);

public sealed record DesktopRuntimeSnapshot(
    PipelineTuiState Pipeline,
    RuntimeState RuntimeState,
    VoiceVisualizationMode VoiceMode,
    DesktopNotification? Notification,
    DateTimeOffset? TransientModeUntil,
    long EventCount,
    DateTimeOffset LastUpdated)
{
    public static DesktopRuntimeSnapshot Create(
        PipelineViewDefinition definition,
        DateTimeOffset now) =>
        new(
            PipelineTuiState.Create(definition, now),
            RuntimeState.Stopped,
            VoiceVisualizationMode.Idle,
            null,
            null,
            0,
            now);
}

public static class DesktopRuntimeReducer
{
    private static readonly TimeSpan SuccessDuration =
        TimeSpan.FromMilliseconds(850);

    public static DesktopRuntimeSnapshot Reduce(
        DesktopRuntimeSnapshot current,
        AppEvent appEvent,
        DateTimeOffset now)
    {
        PipelineTuiState pipeline =
            PipelineTuiReducer.Reduce(current.Pipeline, appEvent, now);
        RuntimeState runtimeState = current.RuntimeState;
        VoiceVisualizationMode mode = current.VoiceMode;
        DateTimeOffset? transientUntil = current.TransientModeUntil;
        DesktopNotification? notification = current.Notification;

        if (appEvent.Telemetry is RuntimeLifecycleTelemetry lifecycle)
        {
            runtimeState = lifecycle.State;
            mode = lifecycle.State switch
            {
                RuntimeState.Stopped => VoiceVisualizationMode.Idle,
                RuntimeState.Starting => VoiceVisualizationMode.Processing,
                RuntimeState.Running => VoiceVisualizationMode.Listening,
                RuntimeState.Stopping => VoiceVisualizationMode.Stopping,
                _ => VoiceVisualizationMode.Error
            };
            transientUntil = null;
        }

        switch (appEvent.Kind)
        {
            case AppEventKind.Listening:
                mode = VoiceVisualizationMode.Listening;
                transientUntil = null;
                break;
            case AppEventKind.SpeechStarted:
                mode = VoiceVisualizationMode.Speech;
                transientUntil = null;
                break;
            case AppEventKind.SegmentCompleted:
            case AppEventKind.ProcessingStarted:
            case AppEventKind.TranscriptionStarted:
            case AppEventKind.TextTranslationStarted:
            case AppEventKind.TranslationRequestStarted:
                mode = VoiceVisualizationMode.Processing;
                transientUntil = null;
                break;
            case AppEventKind.TranslationCompleted:
            case AppEventKind.RealtimeTranslationAccepted:
                mode = VoiceVisualizationMode.Success;
                transientUntil = now + SuccessDuration;
                break;
            case AppEventKind.ApiError:
            case AppEventKind.RealtimeTranslationFailed:
                mode = VoiceVisualizationMode.Error;
                transientUntil = null;
                notification = Notice(
                    DesktopNotificationSeverity.Error,
                    "Provider request failed",
                    appEvent.Message,
                    now);
                break;
            case AppEventKind.OutputError:
            case AppEventKind.RealtimeOutputTimedOut:
            case AppEventKind.RealtimeOutputQuarantined:
            case AppEventKind.RealtimeOutputControlOverflow:
                notification = Notice(
                    DesktopNotificationSeverity.Error,
                    "Output unavailable",
                    appEvent.Message,
                    now);
                break;
            case AppEventKind.VoxtralConnectionLost:
            case AppEventKind.VoxtralReconnectScheduled:
                notification = Notice(
                    DesktopNotificationSeverity.Warning,
                    "Reconnecting",
                    appEvent.Message,
                    now);
                break;
            case AppEventKind.VoxtralReconnected:
                mode = VoiceVisualizationMode.Listening;
                notification = Notice(
                    DesktopNotificationSeverity.Information,
                    "Connection restored",
                    appEvent.Message,
                    now);
                break;
            case AppEventKind.ConfigWarning:
                notification = Notice(
                    DesktopNotificationSeverity.Warning,
                    "Configuration warning",
                    appEvent.Message,
                    now);
                break;
            case AppEventKind.FatalError:
                runtimeState = RuntimeState.Faulted;
                mode = VoiceVisualizationMode.Error;
                transientUntil = null;
                notification = Notice(
                    DesktopNotificationSeverity.Error,
                    "Pipeline stopped",
                    appEvent.Message,
                    now);
                break;
            case AppEventKind.Stopped when runtimeState != RuntimeState.Faulted:
                mode = VoiceVisualizationMode.Idle;
                transientUntil = null;
                break;
        }

        return current with
        {
            Pipeline = pipeline,
            RuntimeState = runtimeState,
            VoiceMode = mode,
            Notification = notification,
            TransientModeUntil = transientUntil,
            EventCount = current.EventCount == long.MaxValue
                ? long.MaxValue
                : current.EventCount + 1,
            LastUpdated = now
        };
    }

    public static DesktopRuntimeSnapshot Advance(
        DesktopRuntimeSnapshot current,
        DateTimeOffset now)
    {
        if (current.TransientModeUntil is not { } deadline || now < deadline)
            return current;
        return current with
        {
            VoiceMode = current.RuntimeState == RuntimeState.Running
                ? VoiceVisualizationMode.Listening
                : VoiceVisualizationMode.Idle,
            TransientModeUntil = null,
            LastUpdated = now
        };
    }

    private static DesktopNotification Notice(
        DesktopNotificationSeverity severity,
        string title,
        string? detail,
        DateTimeOffset now)
    {
        string safe = DiagnosticText.Safe(
            detail ?? "No further detail was provided.",
            800);
        return new(severity, title, safe, now);
    }
}
