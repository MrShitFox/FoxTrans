public sealed class ConsoleUi : IAppReporter, IDisposable
{
    private readonly object _gate = new();
    private readonly FoxTransConfig? _config;
    private readonly bool _interactive;
    private string _microphoneStatus = "Starting...";
    private string _apiStatus = "Waiting...";
    private string _lastTranslation = "None";
    private string _systemMessage = "Ready to rock.";
    private bool _disposed;

    public ConsoleUi(FoxTransConfig? config = null)
    {
        _config = config;
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
                case AppEventKind.TranslationCompleted:
                    _lastTranslation = appEvent.Message ?? "";
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
                    _systemMessage = $"OSC error: {appEvent.Message}";
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
                case AppEventKind.Stopped:
                    _microphoneStatus = "Stopped.";
                    _apiStatus = "Stopped.";
                    _systemMessage = "Application stopped.";
                    break;
                case AppEventKind.FatalError:
                    _microphoneStatus = "Stopped.";
                    _apiStatus = "Error";
                    _systemMessage = $"Fatal error: {appEvent.Message}";
                    break;
            }

            Draw();
        }
    }

    private void Draw()
    {
        if (!_interactive)
        {
            Console.WriteLine(
                $"[FoxTrans] Microphone={_microphoneStatus} API={_apiStatus} " +
                $"Result={_lastTranslation} Message={_systemMessage}");
            return;
        }

        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("========================================");
        Console.WriteLine("  FoxTrans");
        Console.WriteLine($"  Model: {_config?.EffectivePipeline.Speech?.GetType().Name ?? "not configured"}");
        Console.WriteLine("========================================\n");

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"[🎙️] Status: {_microphoneStatus}");
        Console.ForegroundColor = _apiStatus == "Error" ? ConsoleColor.Red : ConsoleColor.Magenta;
        Console.WriteLine($"[⚙️] API:    {_apiStatus}\n");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[💬] Result: {_lastTranslation}\n");
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
}
