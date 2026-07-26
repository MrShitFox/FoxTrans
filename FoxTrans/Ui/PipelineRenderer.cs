using System.Globalization;
using Spectre.Console;
using Spectre.Console.Rendering;

public readonly record struct TerminalViewport(int Width, int Height);
public sealed record TuiRenderCapabilities(bool Ansi, bool Unicode, bool Interactive);
public enum PipelineLayoutMode { Wide, Compact, Narrow, Tiny }

public interface IPipelineTuiRenderer
{
    IRenderable Render(
        PipelineTuiState state,
        TerminalViewport viewport,
        TuiRenderCapabilities capabilities);
}

public sealed class PipelineTuiRenderer : IPipelineTuiRenderer
{
    public static PipelineLayoutMode SelectLayout(TerminalViewport viewport) =>
        viewport.Width >= 120 && viewport.Height >= 28
            ? PipelineLayoutMode.Wide
            : viewport.Width >= 80 && viewport.Height >= 20
                ? PipelineLayoutMode.Compact
                : viewport.Width >= 45 && viewport.Height >= 12
                    ? PipelineLayoutMode.Narrow
                    : PipelineLayoutMode.Tiny;

    public IRenderable Render(
        PipelineTuiState state,
        TerminalViewport viewport,
        TuiRenderCapabilities capabilities)
    {
        int width = Math.Max(10, viewport.Width);
        int height = Math.Max(5, viewport.Height);
        PipelineLayoutMode mode = SelectLayout(new(width, height));
        return mode switch
        {
            PipelineLayoutMode.Wide => Wide(state, width, height, capabilities),
            PipelineLayoutMode.Compact => Compact(state, width, height, capabilities),
            PipelineLayoutMode.Narrow => Narrow(state, width, height, capabilities),
            _ => Tiny(state, width)
        };
    }

    private static IRenderable Wide(
        PipelineTuiState state,
        int width,
        int height,
        TuiRenderCapabilities capabilities)
    {
        var rows = new List<IRenderable>
        {
            Header(state, includeFox: true),
            Pipeline(state, horizontal: true, capabilities),
            TextPanel("SOURCE", state.Source.Text, width,
                state.PanelMode == TuiPanelMode.SourceExpanded ? 6 : 3),
            TextPanel(state.Translation.IsForCurrentSource ? "RESULT" : "RESULT (previous)",
                state.Translation.Text, width,
                state.PanelMode == TuiPanelMode.SourceExpanded ? 6 : 3)
        };
        if (state.PanelMode == TuiPanelMode.Help)
            rows.Add(HelpPanel());
        else
            rows.Add(Details(state, maxRows: 8));
        if (state.PanelMode == TuiPanelMode.Log || height >= 34)
            rows.Add(Events(state, Math.Clamp(height - 27, 2, 7)));
        rows.Add(Footer());
        return new Rows(rows);
    }

    private static IRenderable Compact(
        PipelineTuiState state,
        int width,
        int height,
        TuiRenderCapabilities capabilities)
    {
        var rows = new List<IRenderable>
        {
            Header(state, includeFox: false),
            Pipeline(state, horizontal: true, capabilities),
            TextPanel("SOURCE", state.Source.Text, width,
                state.PanelMode == TuiPanelMode.SourceExpanded ? 4 : 2),
            TextPanel(state.Translation.IsForCurrentSource ? "RESULT" : "RESULT (previous)",
                state.Translation.Text, width,
                state.PanelMode == TuiPanelMode.SourceExpanded ? 4 : 2),
            state.PanelMode == TuiPanelMode.Help ? HelpPanel() : Details(state, 4)
        };
        if (state.PanelMode == TuiPanelMode.Log)
            rows.Add(Events(state, 3));
        rows.Add(Footer());
        return new Rows(rows);
    }

    private static IRenderable Narrow(
        PipelineTuiState state,
        int width,
        int height,
        TuiRenderCapabilities capabilities)
    {
        var rows = new List<IRenderable> { Header(state, includeFox: false) };
        foreach (PipelineNodeDefinition node in state.Definition.Nodes)
        {
            PipelineNodeState runtime = state.Nodes[node.Id];
            string selected = state.SelectedNode == node.Id ? ">" : " ";
            rows.Add(new Markup(
                $"{selected} {StatusOpen(runtime.Status)}[[{Markup.Escape(Clip(node.Title, 25))}]][/] " +
                $"[{StatusColor(runtime.Status)}]{Markup.Escape(runtime.Status.ToString().ToUpperInvariant())}[/] " +
                $"[grey]{Markup.Escape(CompactDetail(runtime))}[/]"));
        }
        rows.Add(new Markup($"[white]Source:[/] {Markup.Escape(Clip(state.Source.Text, Math.Max(8, width - 10)))}"));
        rows.Add(new Markup($"[white]Result{(state.Translation.IsForCurrentSource ? "" : " (previous)")}:[/] " +
            Markup.Escape(Clip(state.Translation.Text, Math.Max(8, width - 10)))));
        UiLogEntry? error = state.RecentEvents.LastOrDefault(item => item.Severity == UiEventSeverity.Error);
        UiLogEntry? latest = error ?? state.RecentEvents.LastOrDefault();
        if (latest is not null)
            rows.Add(new Markup($"{EventColor(latest.Severity)}{Markup.Escape(
                Clip(latest.MessageWithRepeat(), Math.Max(8, width - 2)))}[/]"));
        rows.Add(new Markup("[grey]Tab select  Enter details  Q quit  Resize for full dashboard.[/]"));
        return new Rows(rows.Take(Math.Max(5, height)));
    }

    private static IRenderable Tiny(PipelineTuiState state, int width)
    {
        string stages = string.Join(" > ", state.Definition.Nodes.Select(node =>
            ShortName(node.Kind)));
        PipelineNodeDefinition? active = state.Definition.Nodes.FirstOrDefault(node =>
            state.Nodes[node.Id].Status is PipelineNodeStatus.Active or
                PipelineNodeStatus.Recording or PipelineNodeStatus.Publishing or
                PipelineNodeStatus.Reconnecting);
        var rows = new List<IRenderable>
        {
            new Markup($"[cyan]FoxTrans[/] {(state.IsStopping ? "STOPPING" : "RUNNING")}"),
            new Text(Clip(stages, width)),
            new Text(active is null ? "Waiting" : $"{active.Title} {state.Nodes[active.Id].Status}".ToLowerInvariant()),
            new Text("Source: " + Clip(state.Source.Text, Math.Max(5, width - 8))),
            new Text("Result: " + Clip(state.Translation.Text, Math.Max(5, width - 8))),
            new Text("Resize for full dashboard. Q quits.")
        };
        UiLogEntry? error = state.RecentEvents.LastOrDefault(item => item.Severity == UiEventSeverity.Error);
        if (error is not null)
            rows.Insert(3, new Markup($"[red]{Markup.Escape(Clip(error.Message, width))}[/]"));
        return new Rows(rows);
    }

    private static IRenderable Header(PipelineTuiState state, bool includeFox)
    {
        string uptime = (state.LastUpdated - state.StartedAt).ToString(@"hh\:mm\:ss");
        string status = state.IsStopping ? "[yellow]STOPPING[/]" : "[green]RUNNING[/]";
        string errors = (state.Statistics.ProviderFailures + state.Statistics.OutputFailures).ToString();
        string identity = includeFox
            ? " /\\_/\\  [cyan]FOXTRANS[/]  live speech translation\n( o.o )  "
            : "[cyan]FOXTRANS[/]  ";
        return new Panel(new Markup(
            identity +
            $"[blue]{Markup.Escape(state.Definition.Title)}[/]  {status}  " +
            $"[grey]{uptime}  errors {errors}[/]"))
        {
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Blue)
        };
    }

    private static IRenderable Pipeline(
        PipelineTuiState state,
        bool horizontal,
        TuiRenderCapabilities capabilities)
    {
        string Node(PipelineNodeDefinition node)
        {
            PipelineNodeState runtime = state.Nodes[node.Id];
            bool selected = state.SelectedNode == node.Id;
            string title = Markup.Escape(node.Title.ToUpperInvariant());
            string selection = selected ? "*" : "";
            string detail = node.Kind == PipelineNodeKind.AudioInput &&
                state.AudioLevel is { } meter
                ? Meter(meter)
                : CompactDetail(runtime);
            return
                $"[{StatusColor(runtime.Status)}][[{selection}{title}]] {runtime.Status.ToString().ToUpperInvariant()}[/] " +
                $"[grey]{Markup.Escape(detail)}[/]";
        }

        string Arrow(PipelineNodeId from, PipelineNodeId to)
        {
            var id = new PipelineEdgeId(from, to);
            bool pulse = state.Edges.TryGetValue(id, out PipelineEdgeState? edge) &&
                edge.LastActivity is { } last &&
                state.LastUpdated - last <= TimeSpan.FromMilliseconds(1500);
            string arrow = capabilities.Unicode ? " -> " : " > ";
            return pulse ? $"[green]{arrow}[/]" : $"[grey]{arrow}[/]";
        }

        PipelineNodeDefinition[] main = state.Definition.Nodes
            .Where(node => node.Kind != PipelineNodeKind.Output).ToArray();
        PipelineNodeDefinition[] outputs = state.Definition.Nodes
            .Where(node => node.Kind == PipelineNodeKind.Output).ToArray();
        var graph = new System.Text.StringBuilder();
        for (int index = 0; index < main.Length; index++)
        {
            if (index > 0)
                graph.Append(horizontal
                    ? Arrow(main[index - 1].Id, main[index].Id)
                    : "\n[grey]|[/]\n[grey]v[/]\n");
            graph.Append(Node(main[index]));
        }
        if (outputs.Length == 1)
        {
            graph.Append(horizontal
                ? Arrow(main[^1].Id, outputs[0].Id)
                : "\n[grey]|[/]\n[grey]v[/]\n");
            graph.Append(Node(outputs[0]));
        }
        else
        {
            foreach (PipelineNodeDefinition output in outputs)
            {
                graph.Append("\n");
                graph.Append(Arrow(main[^1].Id, output.Id));
                graph.Append("[grey]+->[/] ");
                graph.Append(Node(output));
            }
        }
        return new Panel(new Markup(graph.ToString()))
        {
            Header = new PanelHeader(" PIPELINE "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Blue)
        };
    }

    private static IRenderable TextPanel(string title, string text, int width, int lines)
    {
        string clipped = Clip(text, Math.Max(20, (width - 6) * lines));
        return new Panel(new Markup($"[white]{Markup.Escape(clipped)}[/]"))
        {
            Header = new PanelHeader($" {title} "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Grey)
        };
    }

    private static IRenderable Details(PipelineTuiState state, int maxRows)
    {
        PipelineNodeDefinition? selected = state.Definition.Nodes
            .FirstOrDefault(node => node.Id == state.SelectedNode);
        if (selected is null)
            return new Text("");
        PipelineNodeState runtime = state.Nodes[selected.Id];
        var table = new Table
        {
            Border = TableBorder.Ascii,
            Expand = true
        };
        table.AddColumn(new TableColumn("[cyan]SETTING[/]"));
        table.AddColumn(new TableColumn("[cyan]VALUE[/]"));
        table.AddRow("Status", Markup.Escape(runtime.Status.ToString()));
        if (runtime.LastDuration is { } duration)
            table.AddRow("Last duration", $"{duration.TotalMilliseconds:F0} ms");
        if (runtime.QueueCapacity > 0)
            table.AddRow("Queue", $"{runtime.QueueCount}/{runtime.QueueCapacity}");
        if (!string.IsNullOrEmpty(runtime.Detail))
            table.AddRow("Runtime", Markup.Escape(runtime.Detail));
        if (selected.Kind == PipelineNodeKind.AudioInput && state.AudioLevel is { } meter)
        {
            table.AddRow("Microphone meter", Markup.Escape(Meter(meter)));
            table.AddRow("Recent peak", meter.IsAvailable ? $"{meter.PeakDb:F0} dB" : "unavailable");
            table.AddRow("Clipping", meter.IsClipping ? "[red]YES[/]" : "no");
        }
        foreach (PipelineSettingView setting in selected.Settings.Take(Math.Max(0, maxRows - 4)))
            table.AddRow(Markup.Escape(setting.Name), Markup.Escape(setting.Value));
        return new Panel(table)
        {
            Header = new PanelHeader($" {Markup.Escape(selected.Title)} DETAILS "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Blue)
        };
    }

    private static IRenderable Events(PipelineTuiState state, int count)
    {
        var rows = state.RecentEvents.TakeLast(count).Select(item =>
            (IRenderable)new Markup(
                $"[grey]{item.Timestamp:HH:mm:ss}[/] " +
                $"{EventColor(item.Severity)}{Markup.Escape(item.Severity.ToString().ToUpperInvariant()),-7}[/] " +
                $"[cyan]{Markup.Escape(item.Stage)}[/] " +
                Markup.Escape(item.MessageWithRepeat()))).ToArray();
        return new Panel(rows.Length == 0 ? new Text("No events yet.") : new Rows(rows))
        {
            Header = new PanelHeader(" EVENTS "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Grey)
        };
    }

    private static IRenderable HelpPanel() =>
        new Panel(new Text(
            "Tab/Right/Down next  Shift+Tab/Left/Up previous\n" +
            "Enter details  L events  S source/result  ?/F1 help\n" +
            "Escape close overlay  Q graceful shutdown  Ctrl+C graceful shutdown"))
        {
            Header = new PanelHeader(" HELP "),
            Border = BoxBorder.Ascii,
            BorderStyle = new Style(Spectre.Console.Color.Blue)
        };

    private static IRenderable Footer() =>
        new Markup("[grey]Tab/Arrows select  Enter details  L log  S text  ? help  Q quit[/]");

    private static string CompactDetail(PipelineNodeState state)
    {
        if (state.QueueCapacity > 0)
            return $"queue {state.QueueCount}/{state.QueueCapacity}" +
                (state.LastDuration is { } queuedDuration ? $"  {queuedDuration.TotalSeconds:F2} s" : "");
        if (state.LastDuration is { } duration)
            return $"{duration.TotalMilliseconds:F0} ms last";
        return Clip(state.Detail ?? state.WorkIdentity ?? "-", 38);
    }

    private static string Meter(AudioLevelTelemetry meter)
    {
        if (!meter.IsAvailable)
            return "[meter unavailable]";
        int filled = Math.Clamp((int)Math.Round((meter.RmsDb + 60) / 3), 0, 20);
        return $"[{new string('#', filled)}{new string('.', 20 - filled)}] " +
            $"{meter.RmsDb:F0} dB" + (meter.IsClipping ? " CLIP" : "");
    }

    private static string ShortName(PipelineNodeKind kind) => kind switch
    {
        PipelineNodeKind.AudioInput => "MIC",
        PipelineNodeKind.Vad => "VAD",
        PipelineNodeKind.AudioLlm => "AUDIO LLM",
        PipelineNodeKind.StreamingStt => "VOXTRAL",
        PipelineNodeKind.BatchStt => "STT",
        PipelineNodeKind.LogicalUtterance => "UTTERANCE",
        PipelineNodeKind.TextTranslation => "LLM",
        _ => "OSC"
    };

    private static string StatusColor(PipelineNodeStatus status) => status switch
    {
        PipelineNodeStatus.Active or PipelineNodeStatus.Listening or
        PipelineNodeStatus.Receiving or PipelineNodeStatus.Recording or
        PipelineNodeStatus.Publishing or PipelineNodeStatus.Ready or
        PipelineNodeStatus.Settled => "green",
        PipelineNodeStatus.Waiting or PipelineNodeStatus.Buffering or
        PipelineNodeStatus.Queued or PipelineNodeStatus.Reconnecting or
        PipelineNodeStatus.Warning or PipelineNodeStatus.Starting => "yellow",
        PipelineNodeStatus.Error or PipelineNodeStatus.TimedOut or
        PipelineNodeStatus.Quarantined => "red",
        _ => "grey"
    };

    private static string StatusOpen(PipelineNodeStatus status) => $"[{StatusColor(status)}]";
    private static string EventColor(UiEventSeverity severity) => severity switch
    {
        UiEventSeverity.Success => "[green]",
        UiEventSeverity.Warning => "[yellow]",
        UiEventSeverity.Error => "[red]",
        UiEventSeverity.Trace => "[grey]",
        _ => "[white]"
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
}

internal static class UiLogEntryExtensions
{
    public static string MessageWithRepeat(this UiLogEntry entry) =>
        entry.RepeatCount <= 1 ? entry.Message : $"{entry.Message} x{entry.RepeatCount}";
}
