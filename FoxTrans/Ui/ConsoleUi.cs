// Compatibility snapshot retained for integrations compiled against the 6.x reporter.
// New application composition uses PipelineTuiHost.
public sealed record ConsoleUiSnapshot(
    string Source,
    string Translation,
    bool TranslationIsForCurrentSource);

public sealed class ConsoleUi : IAppReporter, IDisposable
{
    private readonly object _gate = new();
    private string _source = "None";
    private string _translation = "None";
    private bool _current = true;

    public ConsoleUi(
        FoxTransConfig? config = null,
        ResolvedAudioInput? audio = null)
    {
    }

    public void Report(AppEvent appEvent)
    {
        lock (_gate)
        {
            switch (appEvent.Kind)
            {
                case AppEventKind.TranscriptionCompleted:
                    _source = appEvent.Message ?? "";
                    _current = true;
                    break;
                case AppEventKind.LogicalUtteranceStarted:
                {
                    string text = appEvent.Message ?? "";
                    int colon = text.IndexOf(':');
                    _source = colon >= 0 ? text[(colon + 1)..].TrimStart() : text;
                    _current = _translation == "None";
                    break;
                }
                case AppEventKind.LogicalUtteranceUpdated:
                    _source = appEvent.Message ?? "";
                    break;
                case AppEventKind.TranslationCompleted:
                case AppEventKind.RealtimeTranslationAccepted:
                    _translation = appEvent.Message ?? "";
                    _current = true;
                    break;
                case AppEventKind.TranscriptEpochResynchronized:
                case AppEventKind.VoxtralConnectionLost:
                case AppEventKind.Stopped:
                case AppEventKind.FatalError:
                    _source = "None";
                    _translation = "None";
                    _current = true;
                    break;
            }
        }
    }

    public ConsoleUiSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return new(_source, _translation, _current);
        }
    }

    public void Dispose()
    {
    }
}
