public sealed record ConsoleUiSnapshot(
    string Source,
    string Translation,
    bool TranslationIsForCurrentSource);

public sealed class ConsoleUi : IAppReporter, IDisposable
{
    private readonly object _gate = new();
    private readonly FoxTransConfig? _config;
    private readonly ResolvedAudioInput? _audio;
    private readonly bool _interactive;
    private string _microphoneStatus = "Starting...";
    private string _apiStatus = "Waiting...";
    private string _lastTranslation = "None";
    private string _lastTranscript = "None";
    private bool _translationIsForCurrentSource = true;
    private string _systemMessage = "Ready to rock.";
    private bool _disposed;

    public ConsoleUi(
        FoxTransConfig? config = null,
        ResolvedAudioInput? audio = null)
    {
        _config = config;
        _audio = audio;
        _interactive = !Console.IsOutputRedirected;
        if (_interactive)
        {
            Console.CursorVisible = false;
        }
    }

    public void Report(AppEvent appEvent)
    {
        lock (_gate)
        {
            switch (appEvent.Kind)
            {
                case AppEventKind.Listening:
                    _microphoneStatus = "Listening...";
                    break;
                case AppEventKind.SpeechStarted:
                    _microphoneStatus = "Recording...";
                    break;
                case AppEventKind.SegmentCompleted:
                    _microphoneStatus = $"Captured {appEvent.Duration?.TotalMilliseconds:F0}ms.";
                    break;
                case AppEventKind.ProcessingStarted:
                    _apiStatus = "Processing...";
                    break;
                case AppEventKind.ProcessingCompleted:
                    _apiStatus = "Waiting...";
                    break;
                case AppEventKind.TranscriptionStarted:
                    _apiStatus = "Transcribing...";
                    break;
                case AppEventKind.TranscriptionCompleted:
                    _lastTranscript = appEvent.Message ?? "";
                    _translationIsForCurrentSource = true;
                    _systemMessage = "Transcription completed.";
                    break;
                case AppEventKind.TextTranslationStarted:
                    _apiStatus = "Translating...";
                    break;
                case AppEventKind.TranslationCompleted:
                    _lastTranslation = appEvent.Message ?? "";
                    _translationIsForCurrentSource = true;
                    _systemMessage = "Translation published.";
                    break;
                case AppEventKind.ShortPhraseIgnored:
                    _systemMessage =
                        $"Ignored short noise ({appEvent.Duration?.TotalMilliseconds:F0}ms).";
                    break;
                case AppEventKind.ApiError:
                    _apiStatus = "Error";
                    _systemMessage = $"API error: {appEvent.Message}";
                    break;
                case AppEventKind.OutputError:
                    _systemMessage = $"Output error: {appEvent.Message}";
                    break;
                case AppEventKind.QueueOverflow:
                    _systemMessage = $"Queue overflow: {appEvent.Message}";
                    break;
                case AppEventKind.ConfigCreated:
                    if (_interactive)
                    {
                        Console.Clear();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                    }

                    Console.WriteLine(
                        $"[SYS] Created default {appEvent.Message}. Please set your API key and restart.");
                    if (_interactive)
                    {
                        Console.ResetColor();
                    }

                    return;
                case AppEventKind.ConfigMigrated:
                    Console.WriteLine($"[SYS] Migrated legacy configuration to {appEvent.Message}. Review it, then restart.");
                    return;
                case AppEventKind.ConfigWarning:
                    Console.WriteLine($"[SYS] Configuration warning: {appEvent.Message}");
                    return;
                case AppEventKind.ConfigError:
                    Console.Error.WriteLine($"[SYS] Configuration error: {appEvent.Message}");
                    return;
                case AppEventKind.UnsupportedValidPipeline:
                    Console.WriteLine($"[SYS] {appEvent.Message}");
                    return;
                case AppEventKind.VoxtralHealthChecked:
                    _apiStatus = "Health checked";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralConnecting:
                    _apiStatus = "Connecting...";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.RealtimeAudioRouteActivated:
                    _apiStatus = "Realtime audio ready";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralSessionStarted:
                    _apiStatus = "Realtime session active";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralConnectionLost:
                    _apiStatus = "Connection lost";
                    ClearRealtimeDisplay();
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralReconnectScheduled:
                    _apiStatus = "Reconnect waiting";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralReconnectAttempt:
                    _apiStatus = "Reconnecting...";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralReconnected:
                    _apiStatus = "Realtime session active";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.RealtimeAudioGapStarted:
                    _microphoneStatus = "Listening (audio gap)";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.RealtimeAudioGapCompleted:
                    _microphoneStatus = "Listening...";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.LogicalUtteranceStarted:
                    _lastTranscript = appEvent.Message ?? "";
                    _translationIsForCurrentSource = _lastTranslation == "None";
                    _apiStatus = "Realtime source active";
                    _systemMessage = "A new client-side logical utterance started.";
                    break;
                case AppEventKind.LogicalUtteranceUpdated:
                    _lastTranscript = appEvent.Message ?? "";
                    _systemMessage = "Current bounded source window updated.";
                    break;
                case AppEventKind.LogicalUtteranceSettled:
                    _systemMessage = appEvent.Message ?? "Logical utterance settled.";
                    break;
                case AppEventKind.TranslationRequestStarted:
                    _apiStatus = "Translating...";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.TranslationRequestCoalesced:
                    _apiStatus = "Translation update pending";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.RealtimeTranslationAccepted:
                    _lastTranslation = appEvent.Message ?? "";
                    _translationIsForCurrentSource = true;
                    _apiStatus = "Realtime session active";
                    _systemMessage =
                        "Latest realtime translation accepted for output.";
                    break;
                case AppEventKind.RealtimeTranslationFailed:
                    _apiStatus = "Translation error";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.RealtimeTranslationCompleted:
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.RealtimeOutputTranslationCoalesced:
                case AppEventKind.RealtimeOutputDiscarded:
                    // Detailed lifecycle diagnostics stay reporter-visible without
                    // replacing the main dashboard state on normal coalescing.
                    return;
                case AppEventKind.RealtimeOutputTimedOut:
                case AppEventKind.RealtimeOutputQuarantined:
                case AppEventKind.RealtimeOutputControlOverflow:
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.TranscriptEpochResynchronized:
                    _apiStatus = "Realtime session active";
                    ClearRealtimeDisplay();
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralWarning:
                    _apiStatus = "Processing warning";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.VoxtralSessionCancelled:
                    _apiStatus = "Session cancelled";
                    _systemMessage = appEvent.Message ?? "";
                    break;
                case AppEventKind.Stopped:
                    _microphoneStatus = "Stopped.";
                    _apiStatus = "Stopped.";
                    ClearRealtimeDisplay();
                    _systemMessage = "Application stopped.";
                    break;
                case AppEventKind.FatalError:
                    _microphoneStatus = "Stopped.";
                    _apiStatus = "Error";
                    ClearRealtimeDisplay();
                    _systemMessage = $"Fatal error: {appEvent.Message}";
                    break;
            }

            Draw();
        }
    }

    public ConsoleUiSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new(
                    _lastTranscript,
                    _lastTranslation,
                    _translationIsForCurrentSource);
            }
        }
    }

    private void Draw()
    {
        if (!_interactive)
        {
            string resultLabel = _translationIsForCurrentSource
                ? "Result"
                : "Result(previous)";
            Console.WriteLine(
                $"[FoxTrans] Microphone={_microphoneStatus} API={_apiStatus} " +
                $"Source={_lastTranscript} {resultLabel}={_lastTranslation} Message={_systemMessage}");
            return;
        }

        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("========================================");
        Console.WriteLine("  FoxTrans");
        Console.WriteLine($"  Model: {_config?.EffectivePipeline.Speech?.GetType().Name ?? "not configured"}");
        if (_audio is not null)
            Console.WriteLine($"  Microphone: {_audio.DeviceNumber}  {_audio.DisplayName}");
        Console.WriteLine("========================================\n");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[🎙️] Status: {_microphoneStatus}");
        Console.ForegroundColor = _apiStatus == "Error" ? ConsoleColor.Red : ConsoleColor.Magenta;
        Console.WriteLine($"[⚙️] API:    {_apiStatus}\n");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[Source] {_lastTranscript}");
        Console.WriteLine(
            _translationIsForCurrentSource
                ? $"[💬] Result: {_lastTranslation}\n"
                : $"[💬] Result (previous): {_lastTranslation}\n");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[SYS] {_systemMessage}");
        Console.ResetColor();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_interactive)
        {
            Console.ResetColor();
            Console.CursorVisible = true;
        }
    }

    private void ClearRealtimeDisplay()
    {
        _lastTranscript = "None";
        _lastTranslation = "None";
        _translationIsForCurrentSource = true;
    }
}
