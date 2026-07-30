using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

public readonly record struct TerminalViewport(int Width, int Height);
public sealed record TuiRenderCapabilities(bool Ansi, bool Unicode);

public interface IPipelineTuiRenderer
{
    IRenderable Render(
        PipelineTuiState state,
        TerminalViewport viewport,
        TuiRenderCapabilities capabilities);
}

/// <summary>
/// A deterministic, view-only counterpart to the Desktop Live Studio. It uses
/// the same resolved-plan identity and runtime state, but never accepts input
/// or mutates configuration.
/// </summary>
public sealed class PipelineTuiRenderer : IPipelineTuiRenderer
{
    public const int MinimumWidth = 80;
    public const int MinimumHeight = 24;
    private static readonly TimeSpan SuccessDuration = TimeSpan.FromMilliseconds(850);
    private readonly ConsoleTextReveal _sourceText = new();
    private readonly ConsoleTextReveal _translationText = new();

    public static bool SupportsLiveStudio(TerminalViewport viewport) =>
        viewport.Width >= MinimumWidth && viewport.Height >= MinimumHeight;

    public bool IsTextAnimating =>
        _sourceText.IsAnimating || _translationText.IsAnimating;

    public IRenderable Render(
        PipelineTuiState state,
        TerminalViewport viewport,
        TuiRenderCapabilities capabilities) =>
        RenderAt(state, viewport, capabilities, state.LastUpdated);

    public IRenderable RenderAt(
        PipelineTuiState state,
        TerminalViewport viewport,
        TuiRenderCapabilities capabilities,
        DateTimeOffset renderTime)
    {
        int width = Math.Max(1, viewport.Width);
        if (!SupportsLiveStudio(viewport))
            return TooSmall(viewport);

        StudioStatus status = Status(state, renderTime);
        int textCapacity = Math.Max(16, (width - 10) * 3);
        string? source = RenderText(
            _sourceText,
            state.Source.Text == "None" ? "" : state.Source.Text,
            IsSourceFinal(state),
            textCapacity,
            renderTime);
        string? translation = RenderText(
            _translationText,
            state.Translation.IsForCurrentSource && state.Translation.Text != "None"
                ? state.Translation.Text
                : "",
            state.Translation.IsForCurrentSource,
            textCapacity,
            renderTime);
        var rows = new List<IRenderable>
        {
            Header(state, width),
            new Text(""),
            VoiceRail(state, status, width),
            new Text("")
        };

        if (HasRecognition(state))
            rows.Add(TextPanel(
                " CURRENT RECOGNITION ",
                source ?? "Recognition will appear here",
                "grey"));

        rows.Add(TextPanel(
            " CURRENT TRANSLATION ",
            translation ?? "Translation will appear here",
            state.Translation.IsForCurrentSource ? "blue" : "grey"));

        if (LastNotification(state) is { } notification)
        {
            rows.Add(new Text(""));
            rows.Add(Align.Right(NotificationPanel(notification, width)));
        }

        return new Rows(rows);
    }

    private static IRenderable TooSmall(TerminalViewport viewport)
    {
        string current = viewport.Width > 0 && viewport.Height > 0
            ? $"Current size: {viewport.Width}×{viewport.Height}."
            : "The terminal size is unavailable.";
        return new Panel(new Rows([
            new Markup("[cyan]FOXTRANS CLI[/]"),
            new Markup("[yellow]Increase the terminal window to use Live Studio.[/]"),
            new Markup(Markup.Escape(
                $"{current} Minimum size: {MinimumWidth}×{MinimumHeight}."))
        ]))
        {
            Header = new PanelHeader(" LIVE STUDIO "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Color.Yellow),
            Expand = true
        };
    }

    private static IRenderable Header(PipelineTuiState state, int width)
    {
        PipelinePresentation presentation = Presentation(state);
        int lineCapacity = Math.Max(20, width - 8);
        return new Panel(new Rows([
            new Markup("[cyan]FOXTRANS CLI[/]"),
            new Markup($"[white]{Markup.Escape(Clip(presentation.Mode, lineCapacity))}[/]"),
            new Markup($"[grey]{Markup.Escape(Clip(presentation.ModelLine, lineCapacity))}[/]")
        ]))
        {
            Border = BoxBorder.None,
            Expand = true
        };
    }

    private static IRenderable VoiceRail(
        PipelineTuiState state,
        StudioStatus status,
        int width)
    {
        int meterWidth = Math.Clamp(width - 34, 20, 36);
        string levelBar = LevelMeter(state.AudioLevel, meterWidth);
        string meter = MeterDetail(state.AudioLevel);
        return new Panel(new Rows([
            new Markup($"[{status.Color}]{Markup.Escape(levelBar)}[/] [grey]{Markup.Escape(meter)}[/]"),
            new Markup($"[{status.Color}]{Markup.Escape(status.Title)}[/]"),
            new Markup($"[grey]{Markup.Escape(Clip(status.Detail, Math.Max(20, width - 10)))}[/]")
        ]))
        {
            Header = new PanelHeader(" LIVE "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(StatusColor(status.Color)),
            Expand = true
        };
    }

    private static IRenderable TextPanel(string title, string text, string color) =>
        new Panel(new Markup(Markup.Escape(text)))
        {
            Header = new PanelHeader(title),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(StatusColor(color)),
            Expand = true
        };

    private static IRenderable NotificationPanel(UiLogEntry notification, int width)
    {
        string title = notification.Severity == UiEventSeverity.Error
            ? "NEEDS ATTENTION"
            : "WARNING";
        string color = notification.Severity == UiEventSeverity.Error ? "red" : "yellow";
        int capacity = Math.Max(20, Math.Min(64, width - 18));
        return new Panel(new Markup(Markup.Escape(
            Clip(notification.MessageWithRepeat(), capacity))))
        {
            Header = new PanelHeader($" {title} "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(StatusColor(color))
        };
    }

    private static PipelinePresentation Presentation(PipelineTuiState state)
    {
        PipelineViewDefinition definition = state.Definition;
        if (definition.Presentation is { } presentation)
            return presentation;
        string model = definition.PipelineKind switch
        {
            PipelineKind.DirectAudioTranslation => ModelOf(definition, "audio-llm"),
            PipelineKind.BatchTranscriptionTranslation => JoinModels(
                ModelOf(definition, "batch-stt"),
                ModelOf(definition, "text-translation")),
            PipelineKind.RealtimeTranscriptionTranslation => JoinModels(
                "Voxtral realtime", ModelOf(definition, "text-translation")),
            _ => ""
        };
        string mode = definition.PipelineKind switch
        {
            PipelineKind.DirectAudioTranslation => "Audio LLM",
            PipelineKind.BatchTranscriptionTranslation => "Whisper + LLM",
            PipelineKind.RealtimeTranscriptionTranslation => "Voxtral + LLM",
            _ => definition.Title
        };
        return new(mode, model);
    }

    private static string ModelOf(PipelineViewDefinition definition, string id) =>
        definition.Nodes.FirstOrDefault(node => node.Id.Value == id)?.Settings
            .FirstOrDefault(setting => setting.Name == "Model")?.Value ?? "";

    private static string JoinModels(string first, string second) =>
        string.Join("  ", new[] { first, second }.Where(value =>
            !string.IsNullOrWhiteSpace(value)));

    private static StudioStatus Status(PipelineTuiState state, DateTimeOffset now)
    {
        if (state.IsStopping)
            return new("Stopping", "Releasing the microphone", "yellow");

        PipelineNodeState? reconnecting = state.Nodes.Values.FirstOrDefault(
            node => node.Status == PipelineNodeStatus.Reconnecting);
        if (reconnecting is not null)
            return new("Reconnecting", "Restoring the realtime connection", "yellow");

        UiLogEntry? error = state.RecentEvents.LastOrDefault(
            item => item.Severity == UiEventSeverity.Error);
        if (error is not null || state.Nodes.Values.Any(IsError))
            return new("Needs attention", error?.MessageWithRepeat() ?? "The pipeline needs attention", "red");

        if (state.Nodes.Values.Any(node => node.Status == PipelineNodeStatus.Publishing))
            return new("Sending to VRChat", "Delivering the translation", "blue");

        if (IsActive(state, "batch-stt"))
            return new("Transcribing", "Turning speech into text", "blue");

        if (IsActive(state, "audio-llm") || IsActive(state, "text-translation"))
            return new("Translating", "Preparing the current translation", "blue");

        if (IsActive(state, "vad") || IsActive(state, "logical-utterance"))
            return new("Hearing you", "Listening to the current phrase", "green");

        if (state.Nodes.Values.Any(node => node.Status == PipelineNodeStatus.Active) ||
            state.Nodes.Values.Any(node => node.Status is PipelineNodeStatus.Buffering or
                PipelineNodeStatus.Queued))
        {
            return new("Translating", "Preparing the current translation", "blue");
        }

        if (RecentTranslationSuccess(state, now))
            return new("Translation ready", "The latest phrase was delivered", "green");

        return new("Listening", "The microphone is open", "cyan");
    }

    private static bool RecentTranslationSuccess(PipelineTuiState state, DateTimeOffset now) =>
        state.Nodes.Values
            .Select(node => node.LastSuccess)
            .Where(value => value is not null && now >= value.Value)
            .Any(value => now - value!.Value <= SuccessDuration) &&
        state.Translation.Text is not "" and not "None";

    private static bool IsActive(PipelineTuiState state, string id) =>
        state.Nodes.TryGetValue(new(id), out PipelineNodeState? node) &&
        node.Status is PipelineNodeStatus.Active or PipelineNodeStatus.Recording or
            PipelineNodeStatus.Receiving;

    private static bool IsError(PipelineNodeState node) => node.Status is
        PipelineNodeStatus.Error or PipelineNodeStatus.TimedOut or PipelineNodeStatus.Quarantined;

    private static bool HasRecognition(PipelineTuiState state) =>
        state.Definition.PipelineKind != PipelineKind.DirectAudioTranslation;

    private static UiLogEntry? LastNotification(PipelineTuiState state) =>
        state.RecentEvents.LastOrDefault(item => item.Severity is
            UiEventSeverity.Warning or UiEventSeverity.Error);

    private static string? RenderText(
        ConsoleTextReveal reveal,
        string? text,
        bool isFinal,
        int capacity,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "None")
        {
            reveal.SetTarget("", true, now);
            return null;
        }

        reveal.SetTarget(Clip(text, capacity), isFinal, now);
        reveal.Advance(now);
        return reveal.DisplayedText;
    }

    private static bool IsSourceFinal(PipelineTuiState state) =>
        state.Nodes.TryGetValue(new("logical-utterance"), out PipelineNodeState? logical) &&
        logical.Status == PipelineNodeStatus.Settled;

    private static string LevelMeter(
        AudioLevelTelemetry? meter,
        int width)
    {
        if (meter is not { IsAvailable: true })
            return $"[{new string('.', width)}]";

        int filled = Math.Clamp(
            (int)Math.Round((meter.RmsDb + 60) / 60 * width),
            0,
            width);
        return $"[{new string('#', filled)}{new string('.', width - filled)}]";
    }

    private static string MeterDetail(AudioLevelTelemetry? meter)
    {
        if (meter is null)
            return "Waiting for microphone audio";
        if (!meter.IsAvailable)
            return "Audio meter unavailable";
        return $"MIC LEVEL {meter.RmsDb:F0} dB" +
            (meter.IsClipping ? "  CLIPPING" : "");
    }

    private static Color StatusColor(string color) => color switch
    {
        "green" => Color.Green,
        "yellow" => Color.Yellow,
        "red" => Color.Red,
        "blue" => Color.Blue,
        _ => Color.Cyan1
    };

    internal static string Clip(string? text, int textElements)
    {
        text ??= "";
        text = text.Replace('\r', ' ').Replace('\n', ' ');
        if (textElements <= 0)
            return "";
        int[] starts = StringInfo.ParseCombiningCharacters(text);
        if (starts.Length <= textElements)
            return text;
        int keep = Math.Max(1, textElements - 16);
        int charEnd = keep >= starts.Length ? text.Length : starts[keep];
        return text[..charEnd] + $"... [{starts.Length - keep} hidden]";
    }

    /// <summary>
    /// Text-only counterpart to the Desktop streaming presenter. It retains a
    /// stable prefix across realtime revisions, but deliberately skips opacity
    /// and motion effects that conventional terminals cannot render cleanly.
    /// </summary>
    private sealed class ConsoleTextReveal
    {
        private const double NormalElementsPerSecond = 42;
        private const double CatchUpElementsPerSecond = 140;
        private static readonly TimeSpan FinalSettlementBound =
            TimeSpan.FromMilliseconds(450);
        private string[] _target = [];
        private string[] _displayed = [];
        private DateTimeOffset? _lastAdvanced;
        private DateTimeOffset _targetUpdatedAt;
        private double _revealBudget;
        private bool _isFinal;

        public string DisplayedText { get; private set; } = "";
        public bool IsAnimating => _displayed.Length < _target.Length;

        public void SetTarget(string text, bool isFinal, DateTimeOffset now)
        {
            if (string.Equals(text, string.Concat(_target), StringComparison.Ordinal))
            {
                _isFinal |= isFinal;
                return;
            }

            string[] next = TextElements(text);
            int common = LongestCommonPrefix(_target, next);
            int retained = Math.Min(common, _displayed.Length);
            _target = next;
            _displayed = _displayed[..retained];
            DisplayedText = string.Concat(_displayed);
            _targetUpdatedAt = now;
            _isFinal = isFinal;
            _revealBudget = 0;
            _lastAdvanced ??= now;
        }

        public void Advance(DateTimeOffset now)
        {
            DateTimeOffset previous = _lastAdvanced ?? now;
            _lastAdvanced = now;
            int pending = _target.Length - _displayed.Length;
            if (pending <= 0)
            {
                DisplayedText = string.Concat(_target);
                return;
            }

            if (_isFinal && now - _targetUpdatedAt >= FinalSettlementBound)
            {
                Reveal(pending);
                return;
            }

            TimeSpan elapsed = now >= previous ? now - previous : TimeSpan.Zero;
            double rate = pending > 18
                ? CatchUpElementsPerSecond
                : NormalElementsPerSecond;
            if (_isFinal)
                rate = Math.Max(rate, pending / FinalSettlementBound.TotalSeconds);
            _revealBudget += elapsed.TotalSeconds * rate;
            int count = Math.Min(pending, (int)_revealBudget);
            if (count > 0)
            {
                _revealBudget -= count;
                Reveal(count);
            }
        }

        private void Reveal(int count)
        {
            int nextLength = Math.Min(_target.Length, _displayed.Length + count);
            _displayed = _target[..nextLength];
            DisplayedText = string.Concat(_displayed);
        }

        private static string[] TextElements(string text)
        {
            var elements = new List<string>();
            TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
            while (enumerator.MoveNext())
                elements.Add(enumerator.GetTextElement());
            return elements.ToArray();
        }

        private static int LongestCommonPrefix(
            IReadOnlyList<string> left,
            IReadOnlyList<string> right)
        {
            int common = 0;
            while (common < left.Count && common < right.Count &&
                string.Equals(left[common], right[common], StringComparison.Ordinal))
            {
                common++;
            }
            return common;
        }
    }

    private sealed record StudioStatus(string Title, string Detail, string Color);
}

internal static class UiLogEntryExtensions
{
    public static string MessageWithRepeat(this UiLogEntry entry) =>
        entry.RepeatCount <= 1 ? entry.Message : $"{entry.Message} x{entry.RepeatCount}";
}
