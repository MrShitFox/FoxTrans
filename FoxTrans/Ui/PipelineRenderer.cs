using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

public readonly record struct TerminalViewport(int Width, int Height);
public sealed record TuiRenderCapabilities(bool Ansi, bool Unicode);
public enum PipelineLayoutMode { Full, Normal, Compact, Tiny }

public interface IPipelineTuiRenderer
{
    IRenderable Render(
        PipelineTuiState state,
        TerminalViewport viewport,
        TuiRenderCapabilities capabilities);
}

/// <summary>
/// A deterministic, view-only rendering of the resolved pipeline.
/// Animation is derived from snapshot timestamps; it is never stored in state.
/// </summary>
public sealed class PipelineTuiRenderer : IPipelineTuiRenderer
{
    private static readonly TimeSpan EdgePulseDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan CompletionFlashDuration = TimeSpan.FromMilliseconds(1200);

    public static PipelineLayoutMode SelectLayout(TerminalViewport viewport) =>
        viewport.Width >= 100 && viewport.Height >= 35
            ? PipelineLayoutMode.Full
            : viewport.Width >= 80 && viewport.Height >= 25
                ? PipelineLayoutMode.Normal
                : viewport.Width >= 55 && viewport.Height >= 18
                    ? PipelineLayoutMode.Compact
                    : PipelineLayoutMode.Tiny;

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
        int width = Math.Max(10, viewport.Width);
        int height = Math.Max(5, viewport.Height);
        PipelineLayoutMode mode = SelectLayout(new(width, Math.Max(5, viewport.Height)));
        bool compressed = height <= 40;
        var rows = new List<IRenderable>
        {
            Header(state, mode == PipelineLayoutMode.Full, width)
        };

        bool outputHeadingShown = false;
        for (int index = 0; index < state.Definition.Nodes.Count; index++)
        {
            PipelineNodeDefinition node = state.Definition.Nodes[index];
            if (node.Kind == PipelineNodeKind.Output && !outputHeadingShown &&
                state.Definition.Nodes.Count(item => item.Kind == PipelineNodeKind.Output) > 1)
            {
                rows.Add(new Markup("[blue]OUTPUTS[/]"));
                outputHeadingShown = true;
            }

            if (index > 0)
            {
                PipelineEdgeDefinition? edge = state.Definition.Edges
                    .FirstOrDefault(item => item.To == node.Id);
                rows.Add(edge is null
                    ? new Text("    |\n    v")
                    : Connector(state, edge, renderTime, width, compressed));
            }

            rows.Add(StageCard(state, node, width, mode, renderTime, compressed));
        }

        if (compressed)
        {
            rows.Add(new Markup($"[white]SRC:[/] {Markup.Escape(Clip(state.Source.Text, Math.Max(8, width - 5)))}"));
            rows.Add(new Markup($"[white]OUT{(state.Translation.IsForCurrentSource ? "" : " (previous)")}:[/] " +
                Markup.Escape(Clip(state.Translation.Text, Math.Max(8, width - 10)))));
        }
        else
        {
            rows.Add(TextPanel("SOURCE", state.Source.Text, width, TextHeight(mode)));
            rows.Add(TextPanel(
                state.Translation.IsForCurrentSource ? "RESULT" : "RESULT (previous)",
                state.Translation.Text,
                width,
                TextHeight(mode)));
        }
        rows.Add(ActivitySummary(state, mode, width, compressed));
        return new Rows(rows);
    }

    private static IRenderable Header(PipelineTuiState state, bool full, int width)
    {
        string status = state.IsStopping ? "STOPPING" : "RUNNING";
        string errorCount = (state.Statistics.ProviderFailures + state.Statistics.OutputFailures)
            .ToString(CultureInfo.InvariantCulture);
        string title = Clip(state.Definition.Title.ToUpperInvariant(), Math.Max(20, width - 34));
        string line = full
            ? $"[cyan]FOXTRANS[/]  [blue]{Markup.Escape(title)}[/]  " +
              $"[{(state.IsStopping ? "yellow" : "green")}]{status}[/]"
            : $"[cyan]FOXTRANS[/]  {Markup.Escape(title)}  " +
              $"[{(state.IsStopping ? "yellow" : "green")}]{status}[/]";
        string details = full
            ? $"[grey]uptime {Uptime(state)} | segments {state.Statistics.SpeechSegmentsCompleted} | errors {errorCount}[/]"
            : $"[grey]errors {errorCount}[/]";
        return new Panel(new Markup(line + "\n" + details))
        {
            Header = full ? new PanelHeader(" FOXTRANS ") : null,
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Blue),
            Expand = true
        };
    }

    private static IRenderable StageCard(
        PipelineTuiState state,
        PipelineNodeDefinition definition,
        int width,
        PipelineLayoutMode mode,
        DateTimeOffset renderTime,
        bool compressed)
    {
        PipelineNodeState runtime = state.Nodes[definition.Id];
        int innerWidth = Math.Max(18, width - 6);
        if (compressed)
        {
            string compactSetting = definition.Subtitle;
            if (definition.Settings.Count > 0)
                compactSetting += " | " + definition.Settings[0].Value;
            return new Markup(
                $"[blue][[{StageNumber(state, definition)}]][/] " +
                $"[cyan]{Markup.Escape(definition.Kind == PipelineNodeKind.Output
                    ? definition.Title.ToUpperInvariant() : TinyTitle(definition.Kind))}[/] " +
                $"{Markup.Escape(Clip(LiveLineText(state, definition, runtime, renderTime, innerWidth / 2), innerWidth / 2))} " +
                $"[grey]{Markup.Escape(Clip(compactSetting, innerWidth / 2))}[/]");
        }
        if (mode == PipelineLayoutMode.Tiny)
        {
            string title = TinyTitle(definition.Kind);
            string live = LiveLine(state, definition, runtime, renderTime, innerWidth);
            string setting = Clip(definition.Subtitle + " | " +
                string.Join(" | ", definition.Settings.Take(2).Select(item => item.Value)),
                innerWidth);
            return new Markup(
                $"[blue][[{StageNumber(state, definition)}]][/] [cyan]{Markup.Escape(title)}[/] " +
                $"{live}\n    [grey]{Markup.Escape(setting)}[/]");
        }

        List<string> config = mode == PipelineLayoutMode.Full
            ? definition.Settings.Select(item =>
                $"{Markup.Escape(item.Name),-20} {Markup.Escape(item.Value)}").ToList()
            : PackedSettings(definition, innerWidth, mode == PipelineLayoutMode.Normal ? 2 : 1);
        if (config.Count == 0)
            config.Add(Markup.Escape(definition.Subtitle));

        var content = new List<IRenderable>
        {
            new Markup($"[blue][[{StageNumber(state, definition)}]][/] " +
                $"[cyan]{Markup.Escape(definition.Title.ToUpperInvariant())}[/]"),
            new Markup($"[grey]CONFIG[/]  {config[0]}")
        };
        foreach (string line in config.Skip(1))
            content.Add(new Markup("         " + line));
        content.Add(new Markup($"[bold]{Markup.Escape(LiveLineText(state, definition, runtime, renderTime, innerWidth))}[/]"));

        return new Panel(new Rows(content))
        {
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(StatusColor(runtime.Status)),
            Expand = true,
            Header = new PanelHeader($" {Markup.Escape(StageNumber(state, definition))} ")
        };
    }

    private static List<string> PackedSettings(
        PipelineNodeDefinition definition,
        int width,
        int lines)
    {
        var values = definition.Settings.Select(item =>
            $"{item.Name.ToUpperInvariant()} {item.Value}").ToList();
        if (values.Count == 0)
            values.Add(definition.Subtitle);
        var result = new List<string>();
        string current = "";
        foreach (string value in values)
        {
            string next = string.IsNullOrEmpty(current) ? value : current + " | " + value;
            if (next.Length <= width || string.IsNullOrEmpty(current))
                current = next;
            else
            {
                result.Add(Markup.Escape(Clip(current, width)));
                current = value;
            }
        }
        if (!string.IsNullOrEmpty(current))
            result.Add(Markup.Escape(Clip(current, width)));
        while (result.Count < lines)
            result.Add(Markup.Escape(Clip(definition.Subtitle, width)));
        return result.Take(lines).ToList();
    }

    private static IRenderable Connector(
        PipelineTuiState state,
        PipelineEdgeDefinition edge,
        DateTimeOffset renderTime,
        int width,
        bool compressed)
    {
        PipelineEdgeState edgeState = state.Edges[edge.Id];
        bool continuous = edge.From.Value == "audio-input" &&
            state.Nodes[edge.From].Status is PipelineNodeStatus.Listening or
                PipelineNodeStatus.Receiving;
        bool active = continuous || IsRecent(edgeState.LastActivity, renderTime, EdgePulseDuration);
        string label = Clip(DataLabel(edge.DataKind, edgeState.Identity), Math.Max(8, width - 8));
        string[] marker = active
            ? MovingMarker(edgeState.LastActivity ?? renderTime, renderTime)
            : ["|", "|", "v"];
        string color = active ? "green" : "grey";
        if (compressed)
            return new Markup($"    [{color}]{marker[0]}[/] {Markup.Escape(label)}\n" +
                $"    [{color}]{marker[2]}[/]");
        return new Markup(
            $"    [{color}]{marker[0]}[/]\n" +
            $" {Markup.Escape(label)} [{color}]{marker[1]}[/]\n" +
            $"    [{color}]{marker[2]}[/]");
    }

    private static string[] MovingMarker(DateTimeOffset activity, DateTimeOffset now)
    {
        double elapsed = Math.Max(0, (now - activity).TotalMilliseconds);
        return ((int)(elapsed / 250) % 3) switch
        {
            0 => ["o", "|", "v"],
            1 => ["|", "o", "v"],
            _ => ["|", "|", "O"]
        };
    }

    private static string LiveLineText(
        PipelineTuiState state,
        PipelineNodeDefinition definition,
        PipelineNodeState runtime,
        DateTimeOffset renderTime,
        int width)
    {
        string status = DisplayStatus(definition, runtime, renderTime);
        string marker = IsPulsing(runtime)
            ? PulseMarker(runtime, renderTime) + " "
            : "";
        string detail = RuntimeDetail(state, definition, runtime, renderTime, width);
        return Clip($"LIVE {marker}{status} {detail}", width);
    }

    private static string LiveLine(
        PipelineTuiState state,
        PipelineNodeDefinition definition,
        PipelineNodeState runtime,
        DateTimeOffset renderTime,
        int width) =>
        Markup.Escape(LiveLineText(state, definition, runtime, renderTime, width));

    private static string RuntimeDetail(
        PipelineTuiState state,
        PipelineNodeDefinition definition,
        PipelineNodeState runtime,
        DateTimeOffset renderTime,
        int width)
    {
        if (!string.IsNullOrWhiteSpace(runtime.LastError) && IsError(runtime.Status))
            return Clip(runtime.LastError, width / 2);
        if (definition.Kind == PipelineNodeKind.AudioInput && state.AudioLevel is { } meter)
            return Clip(Meter(meter), width);
        if (runtime.QueueCapacity > 0)
            return Clip($"queue {runtime.QueueCount}/{runtime.QueueCapacity}", width);
        if (runtime.Status is PipelineNodeStatus.Active or PipelineNodeStatus.Publishing or
            PipelineNodeStatus.Recording or PipelineNodeStatus.Receiving)
        {
            DateTimeOffset started = runtime.LastOperationStarted ?? runtime.LastChanged;
            TimeSpan elapsed = renderTime >= started ? renderTime - started : TimeSpan.Zero;
            return Clip($"{elapsed.TotalSeconds:F2} s {runtime.Detail ?? ""}", width);
        }
        if (runtime.LastDuration is { } duration)
            return Clip($"{duration.TotalMilliseconds:F0} ms {runtime.Detail ?? ""}", width);
        return Clip(runtime.Detail ?? runtime.WorkIdentity ?? "-", width);
    }

    private static string DisplayStatus(
        PipelineNodeDefinition definition,
        PipelineNodeState runtime,
        DateTimeOffset now)
    {
        if (IsError(runtime.Status))
            return runtime.Status.ToString().ToUpperInvariant();
        if (runtime.LastSuccess is { } success && now >= success &&
            now - success <= CompletionFlashDuration)
            return runtime.Status == PipelineNodeStatus.Settled ? "SETTLED" :
            runtime.Status == PipelineNodeStatus.Ready
                ? definition.Kind == PipelineNodeKind.Output ? "DELIVERED" : "COMPLETED" :
                runtime.Status.ToString().ToUpperInvariant();
        return runtime.Status.ToString().ToUpperInvariant();
    }

    private static string PulseMarker(PipelineNodeState runtime, DateTimeOffset now)
    {
        DateTimeOffset origin = runtime.LastOperationStarted ?? runtime.LastChanged;
        double elapsed = Math.Max(0, (now - origin).TotalMilliseconds);
        return ((int)(elapsed / 200) % 4) switch
        {
            0 => "[*]",
            1 => "[+]",
            2 => "[o]",
            _ => "[O]"
        };
    }

    private static bool IsPulsing(PipelineNodeState runtime) => runtime.Status is
        PipelineNodeStatus.Starting or PipelineNodeStatus.Listening or
        PipelineNodeStatus.Receiving or PipelineNodeStatus.Recording or
        PipelineNodeStatus.Buffering or PipelineNodeStatus.Queued or
        PipelineNodeStatus.Active or PipelineNodeStatus.Publishing or
        PipelineNodeStatus.Reconnecting ||
        runtime.Status == PipelineNodeStatus.Waiting &&
        runtime.Detail?.Contains("pending", StringComparison.OrdinalIgnoreCase) == true;

    private static IRenderable TextPanel(string title, string text, int width, int lines)
    {
        int capacity = Math.Max(8, Math.Max(1, lines) * Math.Max(8, width - 6));
        string clipped = Clip(text, capacity);
        return new Panel(new Markup(Markup.Escape(clipped)))
        {
            Header = new PanelHeader($" {title} "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Grey),
            Expand = true
        };
    }

    private static IRenderable ActivitySummary(
        PipelineTuiState state,
        PipelineLayoutMode mode,
        int width,
        bool compressed)
    {
        UiLogEntry? error = state.RecentEvents.LastOrDefault(item => item.Severity == UiEventSeverity.Error);
        UiLogEntry? warning = state.RecentEvents.LastOrDefault(item => item.Severity == UiEventSeverity.Warning);
        string last = error is not null ? "ERROR: " + error.MessageWithRepeat() :
            warning is not null ? "WARNING: " + warning.MessageWithRepeat() :
            state.RecentEvents.LastOrDefault(item => item.Severity == UiEventSeverity.Success) is { } success
                ? success.MessageWithRepeat() : "none";
        string counters =
            $"segments {state.Statistics.SpeechSegmentsCompleted} | " +
            $"translations {state.Statistics.TranslationsAccepted} | " +
            $"delivered {state.Statistics.TranslationsDelivered} | " +
            $"errors {state.Statistics.ProviderFailures + state.Statistics.OutputFailures}";
        if (compressed && error is not null)
            return new Markup($"[red]ERROR {Markup.Escape(Clip(error.MessageWithRepeat(), width))}[/]");
        if (compressed)
            return new Markup($"[white]Last warning:[/] {EventColor(error is not null ? UiEventSeverity.Error : warning is not null ? UiEventSeverity.Warning : UiEventSeverity.Info)}" +
                $"{Markup.Escape(Clip(last, Math.Max(8, width - 16)))}[/]");
        if (mode == PipelineLayoutMode.Tiny && error is not null)
            return new Markup($"[red]ERROR {Markup.Escape(Clip(error.MessageWithRepeat(), width))}[/]");
        if (mode == PipelineLayoutMode.Full)
        {
            var events = state.RecentEvents
                .Where(IsImportant)
                .TakeLast(24)
                .Select(item => (IRenderable)new Markup(
                    $"[grey]{item.Timestamp:HH:mm:ss}[/] {EventColor(item.Severity)}" +
                    $"{Markup.Escape(item.MessageWithRepeat())}[/]"));
            return new Panel(new Rows([
                new Markup($"[white]Last warning:[/] {Markup.Escape(Clip(last, Math.Max(10, width - 20)))}"),
                new Markup($"[grey]{Markup.Escape(counters)}[/]"),
                ..events
            ]))
            {
                Header = new PanelHeader(" ACTIVITY "),
                Border = BoxBorder.Ascii,
                BorderStyle = new Style(Spectre.Console.Color.Grey),
                Expand = true
            };
        }
        return new Markup(
            $"[white]Last warning:[/] {EventColor(error is not null ? UiEventSeverity.Error : warning is not null ? UiEventSeverity.Warning : UiEventSeverity.Info)}" +
            $"{Markup.Escape(Clip(last, Math.Max(10, width - 25)))}[/]  " +
            $"[grey]{Markup.Escape(counters)}[/]");
    }

    private static bool IsImportant(UiLogEntry item) =>
        item.Severity is UiEventSeverity.Warning or UiEventSeverity.Error or UiEventSeverity.Success &&
        !item.Message.Equals("Telemetry", StringComparison.OrdinalIgnoreCase) &&
        !item.Message.Contains("typing", StringComparison.OrdinalIgnoreCase);

    private static int TextHeight(PipelineLayoutMode mode) => mode switch
    {
        PipelineLayoutMode.Full => 3,
        PipelineLayoutMode.Normal => 2,
        PipelineLayoutMode.Compact => 1,
        _ => 1
    };

    private static string StageNumber(PipelineTuiState state, PipelineNodeDefinition node)
    {
        int number = state.Definition.Nodes.ToList().IndexOf(node) + 1;
        if (node.Kind != PipelineNodeKind.Output ||
            state.Definition.Nodes.Count(item => item.Kind == PipelineNodeKind.Output) <= 1)
            return number.ToString(CultureInfo.InvariantCulture);
        int outputBase = state.Definition.Nodes.ToList().FindIndex(item => item.Kind == PipelineNodeKind.Output) + 1;
        int outputIndex = state.Definition.Nodes
            .TakeWhile(item => item.Id != node.Id)
            .Count(item => item.Kind == PipelineNodeKind.Output) + 1;
        return $"{outputBase}.{outputIndex}";
    }

    private static string DataLabel(PipelineDataKind kind, string? identity) =>
        kind switch
        {
            PipelineDataKind.PcmAudio => "PCM audio",
            PipelineDataKind.SpeechSegment => identity ?? "speech segment",
            PipelineDataKind.WavRequest => "WAV request",
            PipelineDataKind.Transcript => identity ?? "transcript",
            PipelineDataKind.Translation => identity ?? "translation",
            PipelineDataKind.OutputUpdate => "output update",
            PipelineDataKind.TypingControl => "typing control",
            _ => kind.ToString()
        };

    private static string TinyTitle(PipelineNodeKind kind) => kind switch
    {
        PipelineNodeKind.AudioInput => "MIC",
        PipelineNodeKind.Vad => "VAD",
        PipelineNodeKind.AudioLlm => "AUDIO LLM",
        PipelineNodeKind.StreamingStt => "VOXTRAL",
        PipelineNodeKind.LogicalUtterance => "UTTERANCE",
        PipelineNodeKind.BatchStt => "STT",
        PipelineNodeKind.TextTranslation => "LLM",
        _ => "OSC"
    };

    private static Color StatusColor(PipelineNodeStatus status) => status switch
    {
        PipelineNodeStatus.Active or PipelineNodeStatus.Listening or
        PipelineNodeStatus.Receiving or PipelineNodeStatus.Recording or
        PipelineNodeStatus.Publishing or PipelineNodeStatus.Ready or
        PipelineNodeStatus.Settled => Spectre.Console.Color.Green,
        PipelineNodeStatus.Waiting or PipelineNodeStatus.Buffering or
        PipelineNodeStatus.Queued or PipelineNodeStatus.Reconnecting or
        PipelineNodeStatus.Warning or PipelineNodeStatus.Starting => Spectre.Console.Color.Yellow,
        PipelineNodeStatus.Error or PipelineNodeStatus.TimedOut or
        PipelineNodeStatus.Quarantined => Spectre.Console.Color.Red,
        _ => Spectre.Console.Color.Grey
    };

    private static string EventColor(UiEventSeverity severity) => severity switch
    {
        UiEventSeverity.Success => "[green]",
        UiEventSeverity.Warning => "[yellow]",
        UiEventSeverity.Error => "[red]",
        UiEventSeverity.Trace => "[grey]",
        _ => "[white]"
    };

    private static bool IsError(PipelineNodeStatus status) => status is
        PipelineNodeStatus.Error or PipelineNodeStatus.TimedOut or PipelineNodeStatus.Quarantined;

    private static bool IsRecent(DateTimeOffset? timestamp, DateTimeOffset now, TimeSpan duration) =>
        timestamp is { } value && now >= value && now - value <= duration;

    private static string Uptime(PipelineTuiState state) =>
        Math.Max(0, (state.LastUpdated - state.StartedAt).TotalSeconds)
            .ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Meter(AudioLevelTelemetry meter)
    {
        if (!meter.IsAvailable)
            return "meter unavailable";
        int filled = Math.Clamp((int)Math.Round((meter.RmsDb + 60) / 3), 0, 20);
        return $"[{new string('#', filled)}{new string('.', 20 - filled)}] {meter.RmsDb:F0} dB" +
            (meter.IsClipping ? " CLIP" : "");
    }

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
}

internal static class UiLogEntryExtensions
{
    public static string MessageWithRepeat(this UiLogEntry entry) =>
        entry.RepeatCount <= 1 ? entry.Message : $"{entry.Message} x{entry.RepeatCount}";
}
