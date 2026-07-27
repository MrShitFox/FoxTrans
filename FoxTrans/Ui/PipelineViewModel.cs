using System.Collections.ObjectModel;

public readonly record struct PipelineNodeId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct PipelineEdgeId(PipelineNodeId From, PipelineNodeId To)
{
    public override string ToString() => $"{From}>{To}";
}

public enum PipelineNodeKind
{
    AudioInput,
    Vad,
    AudioLlm,
    StreamingStt,
    BatchStt,
    LogicalUtterance,
    TextTranslation,
    Output
}

public enum PipelineDataKind
{
    PcmAudio,
    SpeechSegment,
    WavRequest,
    Transcript,
    Translation,
    OutputUpdate,
    TypingControl
}

public sealed record PipelineSettingView(string Name, string Value);

public sealed record PipelineNodeDefinition(
    PipelineNodeId Id,
    PipelineNodeKind Kind,
    string Title,
    string Subtitle,
    IReadOnlyList<PipelineSettingView> Settings);

public sealed record PipelineEdgeDefinition(
    PipelineEdgeId Id,
    PipelineNodeId From,
    PipelineNodeId To,
    PipelineDataKind DataKind);

public sealed record PipelineViewDefinition(
    PipelineKind PipelineKind,
    string Title,
    IReadOnlyList<PipelineNodeDefinition> Nodes,
    IReadOnlyList<PipelineEdgeDefinition> Edges);

public static class PipelineTopologyBuilder
{
    public static PipelineViewDefinition Build(ResolvedExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var nodes = new List<PipelineNodeDefinition>();
        var edges = new List<PipelineEdgeDefinition>();

        PipelineNodeDefinition Add(
            string id,
            PipelineNodeKind kind,
            string title,
            string subtitle,
            params PipelineSettingView[] settings)
        {
            var node = new PipelineNodeDefinition(
                new(id), kind, title, subtitle,
                Array.AsReadOnly(settings));
            nodes.Add(node);
            return node;
        }

        void Connect(
            PipelineNodeDefinition from,
            PipelineNodeDefinition to,
            PipelineDataKind kind)
        {
            var id = new PipelineEdgeId(from.Id, to.Id);
            edges.Add(new(id, from.Id, to.Id, kind));
        }

        AudioFormat format = plan.Audio.Format;
        PipelineNodeDefinition audio = Add(
            "audio-input",
            PipelineNodeKind.AudioInput,
            "Microphone",
            plan.Audio.DisplayName,
            new("Device number", plan.Audio.DeviceNumber.ToString()),
            new("Device name", plan.Audio.DisplayName),
            new("Sample rate", $"{format.SampleRate} Hz"),
            new("Bits per sample", format.BitsPerSample.ToString()),
            new("Channels", format.Channels.ToString()));

        PipelineNodeDefinition tail;
        switch (plan.PipelineKind)
        {
            case PipelineKind.DirectAudioTranslation:
            {
                PipelineNodeDefinition vad = AddVad(plan);
                ResolvedOpenAiAudioSettings direct = plan.Direct!;
                PipelineNodeDefinition provider = Add(
                    "audio-llm",
                    PipelineNodeKind.AudioLlm,
                    "Audio LLM",
                    direct.Model,
                    Endpoint("Endpoint", direct.Endpoint),
                    new("Model", direct.Model),
                    new("Credential configured", YesNo(direct.ApiKey)),
                    new("Prompt", Configured(direct.Prompt)),
                    new("Request audio", "WAV PCM16LE"));
                Connect(audio, vad, PipelineDataKind.PcmAudio);
                Connect(vad, provider, PipelineDataKind.SpeechSegment);
                tail = provider;
                break;
            }
            case PipelineKind.BatchTranscriptionTranslation:
            {
                PipelineNodeDefinition vad = AddVad(plan);
                ResolvedOpenAiTranscriptionSettings transcription = plan.Transcription!;
                PipelineNodeDefinition stt = Add(
                    "batch-stt",
                    PipelineNodeKind.BatchStt,
                    "Speech to text",
                    transcription.Model,
                    Endpoint("Endpoint", transcription.Endpoint),
                    new("Model", transcription.Model),
                    new("Language", transcription.Language ?? "automatic"),
                    new("Request format", transcription.RequestFormat == OpenAiTranscriptionRequestFormat.Json
                        ? "JSON base64 WAV"
                        : "multipart WAV"),
                    new("Credential configured", YesNo(transcription.ApiKey)));
                PipelineNodeDefinition translation = AddTranslation(plan);
                Connect(audio, vad, PipelineDataKind.PcmAudio);
                Connect(vad, stt, PipelineDataKind.SpeechSegment);
                Connect(stt, translation, PipelineDataKind.Transcript);
                tail = translation;
                break;
            }
            case PipelineKind.RealtimeTranscriptionTranslation:
            {
                ResolvedVoxtralFoxSettings voxtral = plan.Voxtral!;
                PipelineNodeDefinition streaming = Add(
                    "streaming-stt",
                    PipelineNodeKind.StreamingStt,
                    "Voxtral streaming",
                    $"{voxtral.RealtimeEndpoint.Host}:{voxtral.RealtimeEndpoint.Port}",
                    Endpoint("Health endpoint", voxtral.HealthEndpoint),
                    Endpoint("Realtime endpoint", voxtral.RealtimeEndpoint),
                    new("Delay", $"{voxtral.DelayMs} ms"),
                    new("Credential configured", YesNo(voxtral.ApiKey)));
                ResolvedRealtimeSettings realtime = plan.Realtime!;
                string preset = plan.Config.EffectivePipeline.Realtime?.Preset ?? "custom";
                PipelineNodeDefinition utterance = Add(
                    "logical-utterance",
                    PipelineNodeKind.LogicalUtterance,
                    "Logical utterance",
                    preset,
                    new("Preset", preset),
                    new("Minimum interval", $"{realtime.MinimumIntervalMs} ms"),
                    new("Maximum interval", $"{realtime.MaximumIntervalMs} ms"),
                    new("Changed-word trigger", realtime.MinimumChangedWords.ToString()),
                    new("New utterance pause", $"{realtime.NewUtteranceAfterMs} ms"),
                    new("Source window", $"{realtime.MaxSourceCharacters} text elements"));
                PipelineNodeDefinition translation = AddTranslation(plan);
                Connect(audio, streaming, PipelineDataKind.PcmAudio);
                Connect(streaming, utterance, PipelineDataKind.Transcript);
                Connect(utterance, translation, PipelineDataKind.Transcript);
                tail = translation;
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(plan.PipelineKind));
        }

        for (int index = 0; index < plan.Outputs.Count; index++)
        {
            ResolvedOscEndpoint output = plan.Outputs[index];
            PipelineNodeDefinition sink = Add(
                $"output:{index + 1}",
                PipelineNodeKind.Output,
                plan.Outputs.Count == 1 ? "VRChat OSC" : $"VRChat OSC {index + 1}",
                $"{output.Host}:{output.Port}",
                new("Type", "VRChat OSC"),
                new("Address", $"{output.Host}:{output.Port}"),
                new("Typing enabled", YesNo(output.TypingIndicator)));
            Connect(tail, sink, PipelineDataKind.OutputUpdate);
        }

        string title = plan.PipelineKind switch
        {
            PipelineKind.DirectAudioTranslation => "Direct audio translation",
            PipelineKind.BatchTranscriptionTranslation => "Classic transcription + translation",
            _ => "Voxtral realtime translation"
        };
        return new(
            plan.PipelineKind,
            title,
            new ReadOnlyCollection<PipelineNodeDefinition>(nodes),
            new ReadOnlyCollection<PipelineEdgeDefinition>(edges));

        PipelineNodeDefinition AddVad(ResolvedExecutionPlan resolved)
        {
            ResolvedVadSettings vad = resolved.Vad!;
            WebRtcVadConfig configured =
                (WebRtcVadConfig)resolved.Config.EffectivePipeline.Vad!;
            _ = VadPresets.TryGet(configured.Preset, out VadPresetDefinition preset);
            return Add(
                "vad",
                PipelineNodeKind.Vad,
                "WebRTC VAD",
                preset.Name,
                new("Preset", preset.Name),
                new("Start after", $"{vad.MinSpeechFrames * 20} ms"),
                new("Stop after", $"{vad.MinSilenceFrames * 20} ms"),
                new("Pre-roll", $"{vad.PreRollFrames * 20} ms"),
                new("Minimum phrase", $"{vad.MinimumPhraseMs} ms"),
                new("Operating mode", vad.OperatingMode.ToString()));
        }

        PipelineNodeDefinition AddTranslation(ResolvedExecutionPlan resolved)
        {
            ResolvedOpenAiChatSettings translation = resolved.Translation!;
            return Add(
                "text-translation",
                PipelineNodeKind.TextTranslation,
                "Text translator",
                translation.Model,
                Endpoint("Endpoint", translation.Endpoint),
                new("Model", translation.Model),
                new("Credential configured", YesNo(translation.ApiKey)),
                new("Prompt", Configured(translation.Prompt)));
        }
    }

    private static PipelineSettingView Endpoint(string name, Uri endpoint)
    {
        string host = endpoint.Host;
        if (!endpoint.IsDefaultPort && endpoint.Port > 0)
            host += $":{endpoint.Port}";
        return new(name, host);
    }

    private static string YesNo(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "no" : "yes";

    private static string YesNo(bool value) => value ? "yes" : "no";
    private static string Configured(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "not configured" : "configured";
}

public enum PipelineNodeStatus
{
    Configured,
    Starting,
    Ready,
    Listening,
    Receiving,
    Recording,
    Buffering,
    Queued,
    Active,
    Waiting,
    Publishing,
    Settled,
    Reconnecting,
    Warning,
    Error,
    TimedOut,
    Quarantined,
    Stopped
}

public sealed record PipelineNodeState(
    PipelineNodeStatus Status,
    DateTimeOffset LastChanged,
    string? WorkIdentity = null,
    TimeSpan? LastDuration = null,
    long SuccessCount = 0,
    long FailureCount = 0,
    int QueueCount = 0,
    int QueueCapacity = 0,
    string? Detail = null,
    DateTimeOffset? LastOperationStarted = null,
    DateTimeOffset? LastOperationCompleted = null,
    DateTimeOffset? LastSuccess = null,
    string? LastError = null);

public sealed record PipelineEdgeState(
    DateTimeOffset? LastActivity,
    long ActivityCount = 0,
    string? Identity = null);

public sealed record SourceDisplayState(string Text, long Epoch = 0, long Utterance = 0, long Revision = 0);
public sealed record TranslationDisplayState(string Text, bool IsForCurrentSource = true);

public sealed record SessionStatistics(
    long AudioFramesObserved = 0,
    long SpeechSegmentsCompleted = 0,
    long ShortPhrasesIgnored = 0,
    long TranscriptionRequests = 0,
    long TranslationRequests = 0,
    long TranslationsAccepted = 0,
    long TranslationsDelivered = 0,
    long OutputCoalesces = 0,
    long ProviderFailures = 0,
    long OutputFailures = 0,
    long Reconnects = 0,
    long AudioGaps = 0);

public enum UiEventSeverity { Trace, Info, Success, Warning, Error }
public sealed record UiLogEntry(
    DateTimeOffset Timestamp,
    UiEventSeverity Severity,
    string Stage,
    string Message,
    TimeSpan? Duration = null,
    int RepeatCount = 1);

public sealed record PipelineTuiState(
    PipelineViewDefinition Definition,
    IReadOnlyDictionary<PipelineNodeId, PipelineNodeState> Nodes,
    IReadOnlyDictionary<PipelineEdgeId, PipelineEdgeState> Edges,
    SourceDisplayState Source,
    TranslationDisplayState Translation,
    SessionStatistics Statistics,
    IReadOnlyList<UiLogEntry> RecentEvents,
    bool IsStopping,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUpdated,
    AudioLevelTelemetry? AudioLevel = null)
{
    public static PipelineTuiState Create(
        PipelineViewDefinition definition,
        DateTimeOffset now)
    {
        var nodes = definition.Nodes.ToDictionary(
            item => item.Id,
            _ => new PipelineNodeState(PipelineNodeStatus.Configured, now));
        var edges = definition.Edges.ToDictionary(
            item => item.Id,
            _ => new PipelineEdgeState(null));
        return new(
            definition,
            new ReadOnlyDictionary<PipelineNodeId, PipelineNodeState>(nodes),
            new ReadOnlyDictionary<PipelineEdgeId, PipelineEdgeState>(edges),
            new("None"),
            new("None"),
            new(),
            Array.Empty<UiLogEntry>(),
            false,
            now,
            now);
    }
}

public enum StageOperationPhase { Started, Completed, Failed }
public enum OutputDeliveryPhase
{
    Accepted,
    Pending,
    Delivered,
    Failed,
    Coalesced,
    TimedOut,
    Quarantined,
    Discarded
}

public abstract record PipelineTelemetry;
public sealed record RuntimeLifecycleTelemetry(
    RuntimeState State,
    long Generation,
    string? FailureCategory = null) : PipelineTelemetry;
public sealed record AudioLevelTelemetry(
    double RmsDb,
    double PeakDb,
    bool IsClipping,
    bool IsAvailable,
    long FramesObserved) : PipelineTelemetry;
public sealed record QueueTelemetry(
    int Count,
    int Capacity,
    long Produced,
    long Consumed,
    bool Backpressured,
    TimeSpan? OldestAge = null) : PipelineTelemetry;
public sealed record StageOperationTelemetry(
    PipelineNodeId NodeId,
    long OperationId,
    StageOperationPhase Phase,
    TimeSpan? Duration = null,
    TimeSpan? AudioDuration = null,
    int ResultLength = 0,
    string? Detail = null) : PipelineTelemetry;
public sealed record OutputDeliveryTelemetry(
    PipelineNodeId NodeId,
    string OutputName,
    long OperationId,
    TranslationUpdateKind UpdateKind,
    OutputDeliveryPhase Phase,
    TimeSpan? Duration = null) : PipelineTelemetry;
public sealed record EdgeActivityTelemetry(
    PipelineNodeId From,
    PipelineNodeId To,
    PipelineDataKind DataKind,
    string? Identity = null) : PipelineTelemetry;
public sealed record RealtimeIdentityTelemetry(
    long Epoch,
    long Utterance,
    long Revision,
    long? RequestedRevision = null,
    int? ConnectionGeneration = null,
    string? SessionId = null,
    int? ProtocolVersion = null) : PipelineTelemetry;

public static class PipelineTuiReducer
{
    private const int EventCapacity = 100;
    private const int TextSafetyBound = 32_768;
    private const int MessageBound = 600;

    public static PipelineTuiState Reduce(
        PipelineTuiState current,
        AppEvent appEvent,
        DateTimeOffset now)
    {
        var nodes = current.Nodes.ToDictionary(pair => pair.Key, pair => pair.Value);
        var edges = current.Edges.ToDictionary(pair => pair.Key, pair => pair.Value);
        SourceDisplayState source = current.Source;
        TranslationDisplayState translation = current.Translation;
        SessionStatistics stats = current.Statistics;
        AudioLevelTelemetry? audio = current.AudioLevel;
        bool stopping = current.IsStopping;
        string stage = "system";
        UiEventSeverity severity = UiEventSeverity.Info;

        void Set(
            string id,
            PipelineNodeStatus status,
            string? detail = null,
            TimeSpan? duration = null,
            bool success = false,
            bool failure = false,
            string? work = null)
        {
            var key = new PipelineNodeId(id);
            if (!nodes.TryGetValue(key, out PipelineNodeState? old))
                return;
            nodes[key] = old with
            {
                Status = status,
                LastChanged = now,
                Detail = success ? Bound(detail, MessageBound) : Bound(detail, MessageBound) ?? old.Detail,
                LastDuration = duration ?? old.LastDuration,
                SuccessCount = Add(old.SuccessCount, success ? 1 : 0),
                FailureCount = Add(old.FailureCount, failure ? 1 : 0),
                WorkIdentity = work ?? old.WorkIdentity,
                LastOperationStarted = status is PipelineNodeStatus.Active or
                    PipelineNodeStatus.Publishing or PipelineNodeStatus.Recording
                    ? now : old.LastOperationStarted,
                LastOperationCompleted = success ? now : old.LastOperationCompleted,
                LastSuccess = success ? now : old.LastSuccess,
                LastError = failure ? Bound(detail, MessageBound) : success ? null : old.LastError
            };
            stage = id;
        }

        void Flow(string from, string to, string? identity = null)
        {
            var id = new PipelineEdgeId(new(from), new(to));
            if (!edges.TryGetValue(id, out PipelineEdgeState? edge))
                return;
            edges[id] = edge with
            {
                LastActivity = now,
                ActivityCount = Add(edge.ActivityCount, 1),
                Identity = Bound(identity, 80)
            };
        }

        switch (appEvent.Kind)
        {
            case AppEventKind.Listening:
                Set("audio-input", PipelineNodeStatus.Listening, "listening");
                if (nodes.ContainsKey(new("vad")))
                    Set("vad", PipelineNodeStatus.Waiting, "listening for speech");
                break;
            case AppEventKind.SpeechStarted:
                Set("vad", PipelineNodeStatus.Recording, "speech detected");
                Flow("audio-input", "vad", "PCM audio");
                break;
            case AppEventKind.SegmentCompleted:
                Set("vad", PipelineNodeStatus.Ready, "segment completed", appEvent.Duration, success: true);
                stats = stats with { SpeechSegmentsCompleted = Add(stats.SpeechSegmentsCompleted, 1) };
                Flow("vad", current.Definition.PipelineKind == PipelineKind.DirectAudioTranslation
                    ? "audio-llm" : "batch-stt", "speech segment");
                severity = UiEventSeverity.Success;
                break;
            case AppEventKind.ShortPhraseIgnored:
                Set("vad", PipelineNodeStatus.Warning, "short phrase ignored", appEvent.Duration);
                stats = stats with { ShortPhrasesIgnored = Add(stats.ShortPhrasesIgnored, 1) };
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.ProcessingStarted:
                Set("audio-llm", PipelineNodeStatus.Active, "WAV request");
                stats = stats with { TranslationRequests = Add(stats.TranslationRequests, 1) };
                break;
            case AppEventKind.ProcessingCompleted:
                Set("audio-llm", PipelineNodeStatus.Ready);
                Set("batch-stt", PipelineNodeStatus.Ready);
                Set("text-translation", PipelineNodeStatus.Ready);
                break;
            case AppEventKind.TranscriptionStarted:
                Set("batch-stt", PipelineNodeStatus.Active, "provider request");
                stats = stats with { TranscriptionRequests = Add(stats.TranscriptionRequests, 1) };
                break;
            case AppEventKind.TranscriptionCompleted:
                source = source with { Text = Bound(appEvent.Message, TextSafetyBound) ?? "" };
                Set("batch-stt", PipelineNodeStatus.Ready, "transcription complete", appEvent.Duration, success: true);
                Flow("batch-stt", "text-translation", "transcript");
                severity = UiEventSeverity.Success;
                break;
            case AppEventKind.TextTranslationStarted:
                Set("text-translation", PipelineNodeStatus.Active, "provider request");
                stats = stats with { TranslationRequests = Add(stats.TranslationRequests, 1) };
                break;
            case AppEventKind.TranslationCompleted:
                translation = new(Bound(appEvent.Message, TextSafetyBound) ?? "", true);
                string provider = current.Definition.PipelineKind == PipelineKind.DirectAudioTranslation
                    ? "audio-llm" : "text-translation";
                Set(provider, PipelineNodeStatus.Ready, "translation complete", appEvent.Duration, success: true);
                foreach (PipelineNodeDefinition output in current.Definition.Nodes.Where(n => n.Kind == PipelineNodeKind.Output))
                    Flow(provider, output.Id.Value, "translation");
                stats = stats with { TranslationsAccepted = Add(stats.TranslationsAccepted, 1) };
                severity = UiEventSeverity.Success;
                break;
            case AppEventKind.ApiError:
            case AppEventKind.RealtimeTranslationFailed:
                Set(ActiveProvider(nodes), PipelineNodeStatus.Error, appEvent.Message, appEvent.Duration, failure: true);
                stats = stats with { ProviderFailures = Add(stats.ProviderFailures, 1) };
                severity = UiEventSeverity.Error;
                break;
            case AppEventKind.OutputError:
                Set(FirstOutput(nodes), PipelineNodeStatus.Error, appEvent.Message, appEvent.Duration, failure: true);
                stats = stats with { OutputFailures = Add(stats.OutputFailures, 1) };
                severity = UiEventSeverity.Error;
                break;
            case AppEventKind.QueueOverflow:
                Set("vad", PipelineNodeStatus.Warning, appEvent.Message);
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.VoxtralConnecting:
            case AppEventKind.VoxtralReconnectAttempt:
                Set("streaming-stt", PipelineNodeStatus.Reconnecting, appEvent.Message);
                break;
            case AppEventKind.VoxtralSessionStarted:
            case AppEventKind.VoxtralReconnected:
                Set("streaming-stt", PipelineNodeStatus.Receiving, appEvent.Message, success: true);
                if (appEvent.Kind == AppEventKind.VoxtralReconnected)
                    stats = stats with { Reconnects = Add(stats.Reconnects, 1) };
                break;
            case AppEventKind.VoxtralConnectionLost:
                Set("streaming-stt", PipelineNodeStatus.Error, appEvent.Message, failure: true);
                severity = UiEventSeverity.Error;
                break;
            case AppEventKind.VoxtralReconnectScheduled:
                Set("streaming-stt", PipelineNodeStatus.Reconnecting, appEvent.Message, appEvent.Duration);
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.RealtimeAudioGapStarted:
            case AppEventKind.RealtimeAudioGapCompleted:
                stats = stats with { AudioGaps = Add(stats.AudioGaps, 1) };
                Set("audio-input", appEvent.Kind == AppEventKind.RealtimeAudioGapStarted
                    ? PipelineNodeStatus.Warning : PipelineNodeStatus.Listening, appEvent.Message, appEvent.Duration);
                severity = appEvent.Kind == AppEventKind.RealtimeAudioGapStarted
                    ? UiEventSeverity.Warning : UiEventSeverity.Success;
                break;
            case AppEventKind.LogicalUtteranceStarted:
                if (translation.Text != "None")
                    translation = translation with { IsForCurrentSource = false };
                Set("logical-utterance", PipelineNodeStatus.Active, "new utterance");
                ApplyRealtimeIdentity(appEvent, ref source);
                Flow("streaming-stt", "logical-utterance", "transcript");
                break;
            case AppEventKind.LogicalUtteranceUpdated:
                Set("logical-utterance", PipelineNodeStatus.Active, "source revision");
                ApplyRealtimeIdentity(appEvent, ref source);
                Flow("streaming-stt", "logical-utterance", "transcript");
                break;
            case AppEventKind.LogicalUtteranceSettled:
                Set("logical-utterance", PipelineNodeStatus.Settled, appEvent.Message);
                break;
            case AppEventKind.TranslationRequestStarted:
                Set("text-translation", PipelineNodeStatus.Active, appEvent.Message);
                Flow("logical-utterance", "text-translation", "translation request");
                stats = stats with { TranslationRequests = Add(stats.TranslationRequests, 1) };
                break;
            case AppEventKind.TranslationRequestCoalesced:
                Set("text-translation", PipelineNodeStatus.Waiting, "newer revision pending");
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.RealtimeTranslationAccepted:
                translation = new(Bound(appEvent.Message, TextSafetyBound) ?? "", true);
                Set("text-translation", PipelineNodeStatus.Ready, "translation accepted", success: true);
                stats = stats with { TranslationsAccepted = Add(stats.TranslationsAccepted, 1) };
                severity = UiEventSeverity.Success;
                break;
            case AppEventKind.RealtimeTranslationCompleted:
                Set("text-translation",
                    appEvent.TranslationTelemetry?.Disposition == TranslationCompletionDisposition.Failed
                        ? PipelineNodeStatus.Error : PipelineNodeStatus.Ready,
                    appEvent.Message,
                    appEvent.Duration,
                    success: appEvent.TranslationTelemetry?.Disposition is
                        TranslationCompletionDisposition.PublishedFinal or
                        TranslationCompletionDisposition.PublishedIntermediate);
                break;
            case AppEventKind.RealtimeOutputTranslationCoalesced:
                stats = stats with { OutputCoalesces = Add(stats.OutputCoalesces,
                    Math.Max(1, appEvent.OutputTelemetry?.CoalescedCount ?? 1)) };
                ApplyRealtimeOutput(appEvent, nodes, now, PipelineNodeStatus.Waiting);
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.RealtimeOutputTimedOut:
                ApplyRealtimeOutput(appEvent, nodes, now, PipelineNodeStatus.TimedOut);
                stats = stats with { OutputFailures = Add(stats.OutputFailures, 1) };
                severity = UiEventSeverity.Error;
                break;
            case AppEventKind.RealtimeOutputQuarantined:
            case AppEventKind.RealtimeOutputControlOverflow:
                ApplyRealtimeOutput(appEvent, nodes, now, PipelineNodeStatus.Quarantined);
                stats = stats with { OutputFailures = Add(stats.OutputFailures, 1) };
                severity = UiEventSeverity.Error;
                break;
            case AppEventKind.RealtimeOutputDiscarded:
                ApplyRealtimeOutput(appEvent, nodes, now, PipelineNodeStatus.Warning);
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.TranscriptEpochResynchronized:
                source = new("None");
                translation = new("None");
                Set("logical-utterance", PipelineNodeStatus.Warning, appEvent.Message);
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.VoxtralWarning:
                Set("streaming-stt", PipelineNodeStatus.Warning, appEvent.Message);
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.ConfigWarning:
                severity = UiEventSeverity.Warning;
                break;
            case AppEventKind.Stopped:
                stopping = true;
                foreach (PipelineNodeId key in nodes.Keys.ToArray())
                    nodes[key] = nodes[key] with { Status = PipelineNodeStatus.Stopped, LastChanged = now };
                severity = UiEventSeverity.Success;
                break;
            case AppEventKind.FatalError:
                stopping = true;
                severity = UiEventSeverity.Error;
                Set(ActiveProvider(nodes), PipelineNodeStatus.Error, appEvent.Message, failure: true);
                break;
        }

        if (appEvent.Telemetry is not null)
            ApplyTelemetry(appEvent.Telemetry, nodes, edges, ref stats, ref audio, now);

        IReadOnlyList<UiLogEntry> events = AddEvent(
            current.RecentEvents,
            new(now, severity, stage, Bound(appEvent.Message, MessageBound) ??
                DefaultMessage(appEvent.Kind), appEvent.Duration));
        return current with
        {
            Nodes = new ReadOnlyDictionary<PipelineNodeId, PipelineNodeState>(nodes),
            Edges = new ReadOnlyDictionary<PipelineEdgeId, PipelineEdgeState>(edges),
            Source = source,
            Translation = translation,
            Statistics = stats,
            RecentEvents = events,
            IsStopping = stopping,
            LastUpdated = now,
            AudioLevel = audio
        };
    }

    private static void ApplyRealtimeIdentity(AppEvent appEvent, ref SourceDisplayState source)
    {
        if (appEvent.Telemetry is RealtimeIdentityTelemetry identity)
        {
            string text = appEvent.Message ?? source.Text;
            int colon = text.IndexOf(':');
            if (appEvent.Kind == AppEventKind.LogicalUtteranceStarted && colon >= 0)
                text = text[(colon + 1)..].TrimStart();
            source = new(Bound(text, TextSafetyBound) ?? "", identity.Epoch,
                identity.Utterance, identity.Revision);
        }
        else
        {
            source = source with { Text = Bound(appEvent.Message, TextSafetyBound) ?? "" };
        }
    }

    private static void ApplyTelemetry(
        PipelineTelemetry telemetry,
        Dictionary<PipelineNodeId, PipelineNodeState> nodes,
        Dictionary<PipelineEdgeId, PipelineEdgeState> edges,
        ref SessionStatistics stats,
        ref AudioLevelTelemetry? audio,
        DateTimeOffset now)
    {
        switch (telemetry)
        {
            case AudioLevelTelemetry meter:
                audio = meter;
                stats = stats with { AudioFramesObserved = Math.Max(stats.AudioFramesObserved, meter.FramesObserved) };
                break;
            case QueueTelemetry queue:
                foreach (PipelineNodeId id in new[] { new PipelineNodeId("audio-llm"), new("batch-stt") })
                {
                    if (!nodes.TryGetValue(id, out PipelineNodeState? state))
                        continue;
                    nodes[id] = state with
                    {
                        QueueCount = Math.Clamp(queue.Count, 0, queue.Capacity),
                        QueueCapacity = queue.Capacity,
                        Detail = queue.Backpressured ? "queue backpressure" : state.Detail,
                        Status = queue.Backpressured ? PipelineNodeStatus.Warning : state.Status,
                        LastChanged = now
                    };
                }
                break;
            case StageOperationTelemetry operation when nodes.TryGetValue(operation.NodeId, out PipelineNodeState? state):
                if (long.TryParse(state.WorkIdentity, out long latestOperation) &&
                    operation.OperationId < latestOperation)
                    break;
                nodes[operation.NodeId] = state with
                {
                    Status = operation.Phase switch
                    {
                        StageOperationPhase.Started => PipelineNodeStatus.Active,
                        StageOperationPhase.Completed => PipelineNodeStatus.Ready,
                        _ => PipelineNodeStatus.Error
                    },
                    LastChanged = now,
                    WorkIdentity = operation.OperationId.ToString(),
                    LastDuration = operation.Duration ?? state.LastDuration,
                    Detail = Bound(operation.Detail, MessageBound) ?? state.Detail,
                    SuccessCount = Add(state.SuccessCount, operation.Phase == StageOperationPhase.Completed ? 1 : 0),
                    FailureCount = Add(state.FailureCount, operation.Phase == StageOperationPhase.Failed ? 1 : 0),
                    LastOperationStarted = operation.Phase == StageOperationPhase.Started
                        ? now : state.LastOperationStarted,
                    LastOperationCompleted = operation.Phase == StageOperationPhase.Completed
                        ? now : state.LastOperationCompleted,
                    LastSuccess = operation.Phase == StageOperationPhase.Completed
                        ? now : state.LastSuccess,
                    LastError = operation.Phase == StageOperationPhase.Failed
                        ? Bound(operation.Detail, MessageBound) ?? state.Detail
                        : operation.Phase == StageOperationPhase.Completed ? null : state.LastError
                };
                break;
            case OutputDeliveryTelemetry delivery when nodes.TryGetValue(delivery.NodeId, out PipelineNodeState? output):
                if (long.TryParse(output.WorkIdentity, out long latestDelivery) &&
                    delivery.OperationId < latestDelivery)
                    break;
                nodes[delivery.NodeId] = output with
                {
                    Status = delivery.Phase switch
                    {
                        OutputDeliveryPhase.Accepted or OutputDeliveryPhase.Pending => PipelineNodeStatus.Publishing,
                        OutputDeliveryPhase.Delivered => PipelineNodeStatus.Ready,
                        OutputDeliveryPhase.Failed => PipelineNodeStatus.Error,
                        OutputDeliveryPhase.Coalesced or OutputDeliveryPhase.Discarded => PipelineNodeStatus.Warning,
                        OutputDeliveryPhase.TimedOut => PipelineNodeStatus.TimedOut,
                        _ => PipelineNodeStatus.Quarantined
                    },
                    LastChanged = now,
                    WorkIdentity = delivery.OperationId.ToString(),
                    LastDuration = delivery.Duration ?? output.LastDuration,
                    SuccessCount = Add(output.SuccessCount, delivery.Phase == OutputDeliveryPhase.Delivered ? 1 : 0),
                    FailureCount = Add(output.FailureCount,
                        delivery.Phase is OutputDeliveryPhase.Failed or OutputDeliveryPhase.TimedOut or OutputDeliveryPhase.Quarantined ? 1 : 0),
                    LastOperationStarted = delivery.Phase is OutputDeliveryPhase.Accepted or OutputDeliveryPhase.Pending
                        ? now : output.LastOperationStarted,
                    LastOperationCompleted = delivery.Phase == OutputDeliveryPhase.Delivered
                        ? now : output.LastOperationCompleted,
                    LastSuccess = delivery.Phase == OutputDeliveryPhase.Delivered
                        ? now : output.LastSuccess,
                    LastError = delivery.Phase is OutputDeliveryPhase.Failed or OutputDeliveryPhase.TimedOut or OutputDeliveryPhase.Quarantined
                        ? output.Detail
                        : delivery.Phase == OutputDeliveryPhase.Delivered ? null : output.LastError
                };
                if (delivery.Phase == OutputDeliveryPhase.Delivered)
                    stats = stats with { TranslationsDelivered = Add(stats.TranslationsDelivered, 1) };
                break;
            case EdgeActivityTelemetry flow:
                var edgeId = new PipelineEdgeId(flow.From, flow.To);
                if (edges.TryGetValue(edgeId, out PipelineEdgeState? edge))
                    edges[edgeId] = edge with
                    {
                        LastActivity = now,
                        ActivityCount = Add(edge.ActivityCount, 1),
                        Identity = Bound(flow.Identity, 80)
                    };
                break;
            case RealtimeIdentityTelemetry identity:
                var logicalKey = new PipelineNodeId("logical-utterance");
                if (identity.Utterance > 0 &&
                    nodes.TryGetValue(logicalKey, out PipelineNodeState? logical))
                    nodes[logicalKey] = logical with
                    {
                        Detail = $"epoch {identity.Epoch}; utterance {identity.Utterance}; revision {identity.Revision}" +
                            (identity.RequestedRevision is long requested
                                ? $"; requested revision {requested}" : ""),
                        WorkIdentity = $"e{identity.Epoch}/u{identity.Utterance}/r{identity.Revision}",
                        LastChanged = now
                    };
                var streamingKey = new PipelineNodeId("streaming-stt");
                if (identity.ConnectionGeneration is int generation &&
                    nodes.TryGetValue(streamingKey, out PipelineNodeState? streaming))
                    nodes[streamingKey] = streaming with
                    {
                        Detail = $"connection generation {generation}; session {identity.SessionId ?? "-"}; protocol {identity.ProtocolVersion?.ToString() ?? "-"}",
                        WorkIdentity = $"connection {generation}",
                        LastChanged = now
                    };
                break;
        }
    }

    private static void ApplyRealtimeOutput(
        AppEvent appEvent,
        Dictionary<PipelineNodeId, PipelineNodeState> nodes,
        DateTimeOffset now,
        PipelineNodeStatus status)
    {
        PipelineNodeId id = appEvent.OutputTelemetry is { OutputIndex: >= 0 } telemetry
            ? new($"output:{telemetry.OutputIndex + 1}")
            : nodes.Keys.FirstOrDefault(key => key.Value.StartsWith("output:", StringComparison.Ordinal));
        if (nodes.TryGetValue(id, out PipelineNodeState? state))
            nodes[id] = state with
            {
                Status = status,
                Detail = Bound(appEvent.Message, MessageBound),
                LastChanged = now,
                LastSuccess = status == PipelineNodeStatus.Ready ? now : state.LastSuccess,
                LastError = status is PipelineNodeStatus.TimedOut or PipelineNodeStatus.Quarantined
                    ? Bound(appEvent.Message, MessageBound) : state.LastError,
                FailureCount = Add(state.FailureCount,
                    status is PipelineNodeStatus.TimedOut or PipelineNodeStatus.Quarantined ? 1 : 0)
            };
    }

    private static IReadOnlyList<UiLogEntry> AddEvent(
        IReadOnlyList<UiLogEntry> existing,
        UiLogEntry next)
    {
        var list = existing.ToList();
        if (list.Count > 0)
        {
            UiLogEntry last = list[^1];
            if (last.Severity == next.Severity &&
                last.Stage == next.Stage &&
                last.Message == next.Message &&
                next.Timestamp - last.Timestamp <= TimeSpan.FromSeconds(3))
            {
                list[^1] = last with { Timestamp = next.Timestamp, RepeatCount = last.RepeatCount + 1 };
                return Array.AsReadOnly(list.ToArray());
            }
        }
        list.Add(next);
        if (list.Count > EventCapacity)
            list.RemoveRange(0, list.Count - EventCapacity);
        return Array.AsReadOnly(list.ToArray());
    }

    private static string ActiveProvider(IReadOnlyDictionary<PipelineNodeId, PipelineNodeState> nodes) =>
        nodes.FirstOrDefault(pair =>
            pair.Value.Status == PipelineNodeStatus.Active &&
            pair.Key.Value is "audio-llm" or "batch-stt" or "text-translation").Key.Value ??
        (nodes.ContainsKey(new("text-translation")) ? "text-translation" :
         nodes.ContainsKey(new("audio-llm")) ? "audio-llm" : "streaming-stt");

    private static string FirstOutput(IReadOnlyDictionary<PipelineNodeId, PipelineNodeState> nodes) =>
        nodes.Keys.FirstOrDefault(key => key.Value.StartsWith("output:", StringComparison.Ordinal)).Value ??
        "output:1";

    private static string DefaultMessage(AppEventKind kind) => kind switch
    {
        AppEventKind.Listening => "listening",
        AppEventKind.SpeechStarted => "speech detected",
        AppEventKind.SegmentCompleted => "segment completed",
        AppEventKind.ProcessingStarted => "provider request started",
        AppEventKind.ProcessingCompleted => "processing completed",
        AppEventKind.TranscriptionStarted => "transcription started",
        AppEventKind.TextTranslationStarted => "translation started",
        AppEventKind.TranslationCompleted => "translation completed",
        AppEventKind.Stopped => "application stopped",
        _ => kind.ToString()
    };

    private static string? Bound(string? text, int length)
    {
        if (text is null || text.Length <= length)
            return text;
        return text[..length] + $"... [{text.Length - length} hidden]";
    }

    private static long Add(long value, long amount) =>
        amount <= 0 ? value : value > long.MaxValue - amount ? long.MaxValue : value + amount;
}
