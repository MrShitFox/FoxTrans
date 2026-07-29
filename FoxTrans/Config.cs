using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;

[Description("One active FoxTrans audio translation pipeline.")]
public sealed record FoxTransConfig(
    [property: JsonPropertyName("$schema")] string Schema = "./foxtrans.schema.json",
    int Version = 1,
    AudioConfig? Audio = null,
    PipelineConfig? Pipeline = null,
    IReadOnlyList<OutputProviderConfig>? Outputs = null)
{
    public AudioConfig EffectiveAudio => Audio ?? new();
    public PipelineConfig EffectivePipeline => Pipeline ?? new();
    public IReadOnlyList<OutputProviderConfig> EffectiveOutputs => Outputs ?? [];
}

public sealed record AudioConfig(string Device = "default");
public sealed record PipelineConfig(
    VadProviderConfig? Vad = null,
    SpeechProviderConfig? Speech = null,
    TranslationProviderConfig? Translation = null,
    RealtimeConfig? Realtime = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WebRtcVadConfig), "webrtc")]
public abstract record VadProviderConfig;
public sealed record WebRtcVadConfig(
    [property: Description("Selects phrase segmentation timings and WebRTC speech-detection behavior for batch audio capture. Canonical presets: short-phrases, natural-speech, long-phrases. Deprecated aliases responsive, balanced, and strict remain accepted for compatibility.")]
    string Preset = "natural-speech",
    int? StartAfterMs = null,
    int? StopAfterMs = null,
    int? PreRollMs = null,
    int? MinimumPhraseMs = null) : VadProviderConfig;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAiChatAudioConfig), "openai-chat-audio")]
[JsonDerivedType(typeof(OpenAiTranscriptionConfig), "openai-transcription")]
[JsonDerivedType(typeof(VoxtralFoxConfig), "voxtral-fox")]
public abstract record SpeechProviderConfig;
public sealed record OpenAiChatAudioConfig(string? BaseUrl = null, string? ApiKey = null, string? Model = null, string? Prompt = null) : SpeechProviderConfig;
public sealed record OpenAiTranscriptionConfig(
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Model = null,
    string? Language = null,
    [property: Description("Selects the HTTP request encoding used by the transcription endpoint. Use \"multipart\" for OpenAI-compatible file uploads and \"json\" for base64 input_audio requests such as OpenRouter STT.")]
    string RequestFormat = "multipart") : SpeechProviderConfig;
public sealed record VoxtralFoxConfig(string? BaseUrl = null, string? ApiKey = null, int DelayMs = 240) : SpeechProviderConfig;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAiChatConfig), "openai-chat")]
public abstract record TranslationProviderConfig;
public sealed record OpenAiChatConfig(string? BaseUrl = null, string? ApiKey = null, string? Model = null, string? Prompt = null) : TranslationProviderConfig;

public sealed record RealtimeConfig(
    [property: Description("Selects translation cadence, natural-pause handling, and source-window defaults.")]
    string Preset = "balanced",
    int? MinimumIntervalMs = null,
    int? MaximumIntervalMs = null,
    int? MinimumChangedWords = null,
    [property: Description("Starts a new client-side logical utterance after the cumulative transcript has not meaningfully changed for this duration. It does not reconnect Voxtral.")]
    int? NewUtteranceAfterMs = null,
    int? MaxSourceCharacters = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VrChatOscConfig), "vrchat-osc")]
public abstract record OutputProviderConfig;
public sealed record VrChatOscConfig(string? Address = null, bool? TypingIndicator = null) : OutputProviderConfig;

public enum PipelineKind { DirectAudioTranslation, BatchTranscriptionTranslation, RealtimeTranscriptionTranslation }
public sealed record ConfigIssue(string Path, string Message, string? Suggestion = null);
public sealed record ConfigValidationResult(PipelineKind? PipelineKind, IReadOnlyList<ConfigIssue> Issues)
{ public bool IsValid => Issues.Count == 0; }
public sealed record ResolvedVadSettings(int MinSpeechFrames, int MinSilenceFrames, int PreRollFrames, int MinimumPhraseMs, VadOperatingMode OperatingMode);
public sealed record VadPresetDefinition(
    string Name,
    int StartAfterMs,
    int StopAfterMs,
    int PreRollMs,
    int MinimumPhraseMs,
    VadOperatingMode OperatingMode);

public static class VadPresets
{
    public static IReadOnlyList<VadPresetDefinition> All { get; } =
    [
        new("short-phrases", 160, 600, 400, 800, VadOperatingMode.Aggressive),
        new("natural-speech", 240, 1000, 600, 1200, VadOperatingMode.VeryAggressive),
        new("long-phrases", 400, 1400, 800, 1600, VadOperatingMode.VeryAggressive)
    ];

    private static readonly IReadOnlyList<(string Alias, string Canonical)> LegacyAliases =
    [
        ("responsive", "short-phrases"),
        ("balanced", "natural-speech"),
        ("strict", "long-phrases")
    ];

    public static bool TryGet(string name, out VadPresetDefinition definition)
    {
        string canonical = CanonicalName(name);
        foreach (VadPresetDefinition candidate in All)
        {
            if (string.Equals(candidate.Name, canonical, StringComparison.Ordinal))
            {
                definition = candidate;
                return true;
            }
        }
        definition = null!;
        return false;
    }

    public static bool TryGetDeprecatedReplacement(string name, out string canonical)
    {
        foreach ((string alias, string replacement) in LegacyAliases)
        {
            if (string.Equals(alias, name, StringComparison.Ordinal))
            {
                canonical = replacement;
                return true;
            }
        }
        canonical = null!;
        return false;
    }

    public static string SupportedNames => string.Join(", ", All.Select(item => item.Name));

    private static string CanonicalName(string name) =>
        TryGetDeprecatedReplacement(name, out string canonical) ? canonical : name;
}
public sealed record ResolvedOpenAiAudioSettings(Uri Endpoint, string? ApiKey, string Model, string Prompt);
public enum OpenAiTranscriptionRequestFormat
{
    Multipart,
    Json
}
public static class OpenAiTranscriptionRequestFormats
{
    public static bool TryParse(
        string value,
        out OpenAiTranscriptionRequestFormat requestFormat)
    {
        requestFormat = value switch
        {
            "multipart" => OpenAiTranscriptionRequestFormat.Multipart,
            "json" => OpenAiTranscriptionRequestFormat.Json,
            _ => default
        };
        return value is "multipart" or "json";
    }

    public const string SupportedNames = "multipart, json";
}
public sealed record ResolvedOpenAiTranscriptionSettings(
    Uri Endpoint,
    string? ApiKey,
    string Model,
    string? Language,
    OpenAiTranscriptionRequestFormat RequestFormat = OpenAiTranscriptionRequestFormat.Multipart);
public sealed record ResolvedOpenAiChatSettings(Uri Endpoint, string? ApiKey, string Model, string Prompt);
public sealed record ResolvedRealtimeSettings(
    int MinimumIntervalMs,
    int MaximumIntervalMs,
    int MinimumChangedWords,
    int NewUtteranceAfterMs,
    int MaxSourceCharacters);
public sealed record RealtimePresetDefinition(
    string Name,
    int MinimumIntervalMs,
    int MaximumIntervalMs,
    int MinimumChangedWords,
    int NewUtteranceAfterMs,
    int MaxSourceCharacters);

public static class RealtimePresets
{
    public static IReadOnlyList<RealtimePresetDefinition> All { get; } =
    [
        new("responsive", 250, 700, 2, 2500, 800),
        new("balanced", 350, 1000, 3, 3000, 1000),
        new("economical", 700, 1800, 5, 4000, 1400)
    ];

    public static bool TryGet(
        string name,
        out RealtimePresetDefinition definition)
    {
        foreach (RealtimePresetDefinition candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                definition = candidate;
                return true;
            }
        }
        definition = null!;
        return false;
    }

    public static string SupportedNames => string.Join(", ", All.Select(item => item.Name));
}
public sealed record ResolvedVoxtralFoxSettings(
    Uri HealthEndpoint,
    Uri RealtimeEndpoint,
    string? ApiKey,
    int DelayMs)
{
    public override string ToString() =>
        $"ResolvedVoxtralFoxSettings {{ HealthEndpoint = {HealthEndpoint}, RealtimeEndpoint = {RealtimeEndpoint}, ApiKey = {(ApiKey is null ? "<none>" : "<redacted>")}, DelayMs = {DelayMs} }}";
}
public sealed record ResolvedOscEndpoint(string Host, int Port, bool TypingIndicator);
public sealed record SecretResolution(string? Value, ConfigIssue? Issue, string? Warning);

public static class ConfigResolver
{
    public static ResolvedVadSettings ResolveVad(WebRtcVadConfig config)
    {
        if (!VadPresets.TryGet(config.Preset, out VadPresetDefinition preset))
            throw new ConfigurationException(
                "pipeline.vad.preset: Expected one of: " + VadPresets.SupportedNames + ".");
        int start = config.StartAfterMs ?? preset.StartAfterMs;
        int stop = config.StopAfterMs ?? preset.StopAfterMs;
        int pre = config.PreRollMs ?? preset.PreRollMs;
        int min = config.MinimumPhraseMs ?? preset.MinimumPhraseMs;
        return new ResolvedVadSettings(CeilFrames(start), CeilFrames(stop), CeilFrames(pre), min, preset.OperatingMode);
    }
    public static int CeilFrames(int milliseconds) => (milliseconds + 19) / 20;
    public static SecretResolution ResolveSecret(string? value, string path, Func<string, string?> environment)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(null, null, null);
        if (!value.StartsWith("env:", StringComparison.Ordinal)) return new(value, null, $"{path}: Literal API keys work, but env:NAME is recommended.");
        string name = value[4..]; string? resolved = string.IsNullOrWhiteSpace(name) ? null : environment(name);
        return string.IsNullOrWhiteSpace(resolved) ? new(null, new(path, $"Environment variable '{name}' is missing or empty."), null) : new(resolved, null, null);
    }
    public static Uri ChatEndpoint(string baseUrl) => Endpoint(baseUrl, "/chat/completions");
    public static Uri TranscriptionEndpoint(string baseUrl) => Endpoint(baseUrl, "/audio/transcriptions");
    public static ResolvedOpenAiTranscriptionSettings ResolveTranscription(OpenAiTranscriptionConfig config, string? apiKey)
    {
        if (!OpenAiTranscriptionRequestFormats.TryParse(
                config.RequestFormat,
                out OpenAiTranscriptionRequestFormat requestFormat))
            throw new ConfigurationException(
                "pipeline.speech.requestFormat: Expected one of: " +
                OpenAiTranscriptionRequestFormats.SupportedNames + ".");
        return new(
            TranscriptionEndpoint(config.BaseUrl!),
            apiKey,
            config.Model!,
            string.IsNullOrWhiteSpace(config.Language) ? null : config.Language.Trim(),
            requestFormat);
    }
    public static ResolvedOpenAiChatSettings ResolveChat(OpenAiChatConfig config, string? apiKey) =>
        new(ChatEndpoint(config.BaseUrl!), apiKey, config.Model!, config.Prompt!);
    public static ResolvedRealtimeSettings ResolveRealtime(RealtimeConfig config)
    {
        if (!RealtimePresets.TryGet(config.Preset, out RealtimePresetDefinition preset))
        {
            throw new ConfigurationException(
                "pipeline.realtime.preset: Expected one of: " +
                RealtimePresets.SupportedNames + ".");
        }
        return new(
            config.MinimumIntervalMs ?? preset.MinimumIntervalMs,
            config.MaximumIntervalMs ?? preset.MaximumIntervalMs,
            config.MinimumChangedWords ?? preset.MinimumChangedWords,
            config.NewUtteranceAfterMs ?? preset.NewUtteranceAfterMs,
            config.MaxSourceCharacters ?? preset.MaxSourceCharacters);
    }
    public static ResolvedVoxtralFoxSettings ResolveVoxtral(
        VoxtralFoxConfig config,
        string? apiKey,
        string path = "pipeline.speech.baseUrl")
    {
        if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out Uri? baseUri) ||
            string.IsNullOrWhiteSpace(baseUri.Host) ||
            !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ConfigurationException($"{path}: Expected an absolute HTTP or HTTPS server base URL.");
        }

        if (baseUri.Scheme is not ("http" or "https"))
            throw new ConfigurationException($"{path}: Only http and https server base URLs are supported.");
        if (!string.IsNullOrEmpty(baseUri.Query))
            throw new ConfigurationException($"{path}: The server base URL must not contain a query string.");
        if (!string.IsNullOrEmpty(baseUri.Fragment))
            throw new ConfigurationException($"{path}: The server base URL must not contain a fragment.");
        if (baseUri.AbsolutePath is not ("" or "/"))
            throw new ConfigurationException($"{path}: Configure only the server base URL, without a protocol path.");

        var health = new UriBuilder(baseUri) { Path = "/health", Query = "", Fragment = "" }.Uri;
        var realtime = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
            Path = "/v1/realtime/transcription",
            Query = "",
            Fragment = ""
        }.Uri;
        return new(health, realtime, apiKey, config.DelayMs);
    }
    private static Uri Endpoint(string baseUrl, string suffix)
    {
        string trimmed = baseUrl.TrimEnd('/');
        return new Uri(trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + suffix, UriKind.Absolute);
    }
    public static ResolvedOscEndpoint ResolveOsc(VrChatOscConfig config)
    {
        string[] parts = (config.Address ?? "127.0.0.1:9000").Split(':', 2);
        return new(parts[0], int.Parse(parts[1]), config.TypingIndicator ?? true);
    }
}

public static class ConfigValidator
{
    public static readonly IReadOnlySet<int> VoxtralDelays =
        new HashSet<int>([80, 160, 240, 320, 400, 480, 560, 640, 720, 800, 880, 960, 1040, 1120, 1200, 2400]);
    public static ConfigValidationResult Validate(FoxTransConfig config)
    {
        var issues = new List<ConfigIssue>(); PipelineConfig p = config.EffectivePipeline;
        if (config.Version != 1) issues.Add(new("version", "Only configuration version 1 is supported."));
        if (p.Speech is null) issues.Add(new("pipeline.speech", "A speech provider is required."));
        if (config.EffectiveOutputs.Count == 0) issues.Add(new("outputs", "At least one output is required."));
        foreach (OutputProviderConfig output in config.EffectiveOutputs)
        {
            if (ValidateOutput(output) is { } issue)
                issues.Add(issue);
        }
        if (p.Vad is WebRtcVadConfig vad) ValidateVad(vad, issues);
        if (p.Realtime is not null) ValidateRealtime(p.Realtime, issues);
        PipelineKind? kind = p.Speech switch
        {
            OpenAiChatAudioConfig a => ValidateDirect(p, a, issues),
            OpenAiTranscriptionConfig a => ValidateBatch(p, a, issues),
            VoxtralFoxConfig a => ValidateRealtimePipeline(p, a, issues),
            _ => null
        };
        return new(kind, issues);
    }
    private static PipelineKind ValidateDirect(PipelineConfig p, OpenAiChatAudioConfig a, List<ConfigIssue> i) { Required(a.BaseUrl,"pipeline.speech.baseUrl",i); Required(a.Model,"pipeline.speech.model",i); Required(a.Prompt,"pipeline.speech.prompt",i); if (p.Vad is null)i.Add(new("pipeline.vad","A VAD provider is required for direct audio translation.")); if(p.Realtime is not null)i.Add(new("pipeline.realtime","Realtime settings apply only to the \"voxtral-fox\" pipeline. Remove the realtime section.")); if(p.Translation is not null)i.Add(new("pipeline.translation","The speech provider \"openai-chat-audio\" already returns translated text. Remove the translation section.")); return PipelineKind.DirectAudioTranslation; }
    private static PipelineKind ValidateBatch(PipelineConfig p, OpenAiTranscriptionConfig a, List<ConfigIssue> i) { Required(a.BaseUrl,"pipeline.speech.baseUrl",i); Required(a.Model,"pipeline.speech.model",i); if(!OpenAiTranscriptionRequestFormats.TryParse(a.RequestFormat,out _))i.Add(new("pipeline.speech.requestFormat","Expected one of: " + OpenAiTranscriptionRequestFormats.SupportedNames + ".")); if(p.Vad is null)i.Add(new("pipeline.vad","A VAD provider is required for transcription.")); if(p.Realtime is not null)i.Add(new("pipeline.realtime","Realtime settings apply only to the \"voxtral-fox\" pipeline. Remove the realtime section.")); ValidateTranslation(p.Translation,i); return PipelineKind.BatchTranscriptionTranslation; }
    private static PipelineKind ValidateRealtimePipeline(
        PipelineConfig pipeline,
        VoxtralFoxConfig config,
        List<ConfigIssue> issues)
    {
        const string path = "pipeline.speech.baseUrl";
        Required(config.BaseUrl, path, issues);
        if (!string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            try
            {
                ConfigResolver.ResolveVoxtral(config, null);
            }
            catch (ConfigurationException exception)
            {
                string prefix = path + ": ";
                issues.Add(new(
                    path,
                    exception.Message.StartsWith(prefix, StringComparison.Ordinal)
                        ? exception.Message[prefix.Length..]
                        : exception.Message));
            }
        }

        if (!VoxtralDelays.Contains(config.DelayMs))
            issues.Add(new(
                "pipeline.speech.delayMs",
                "Expected one of: 80, 160, 240, 320, 400, 480, 560, 640, 720, 800, 880, 960, 1040, 1120, 1200, 2400."));
        if (pipeline.Vad is not null)
            issues.Add(new(
                "pipeline.vad",
                "The speech provider \"voxtral-fox\" receives continuous audio. Remove VAD from this pipeline."));
        if (pipeline.Realtime is null)
            issues.Add(new(
                "pipeline.realtime",
                "Realtime settings are required when speech.type is \"voxtral-fox\"."));
        ValidateTranslation(pipeline.Translation, issues);
        return PipelineKind.RealtimeTranscriptionTranslation;
    }
    private static void ValidateTranslation(TranslationProviderConfig? t,List<ConfigIssue> i) { if(t is not OpenAiChatConfig a){i.Add(new("pipeline.translation","The speech provider returns source text, so a translation provider is required.")); return;} Required(a.BaseUrl,"pipeline.translation.baseUrl",i); Required(a.Model,"pipeline.translation.model",i); Required(a.Prompt,"pipeline.translation.prompt",i); }
    private static void ValidateVad(WebRtcVadConfig v,List<ConfigIssue> i) { if(!VadPresets.TryGet(v.Preset, out _)) i.Add(new("pipeline.vad.preset","Expected one of: " + VadPresets.SupportedNames + ".")); foreach((string n,int? x) in new[]{("startAfterMs",v.StartAfterMs),("stopAfterMs",v.StopAfterMs),("preRollMs",v.PreRollMs),("minimumPhraseMs",v.MinimumPhraseMs)}) if(x is <=0 or >60000)i.Add(new($"pipeline.vad.{n}","The value must be between 1 and 60000 milliseconds.")); }
    private static void ValidateRealtime(RealtimeConfig r, List<ConfigIssue> i)
    {
        const string root = "pipeline.realtime";
        if (!RealtimePresets.TryGet(r.Preset, out _))
        {
            i.Add(new(
                $"{root}.preset",
                "Expected one of: " + RealtimePresets.SupportedNames + "."));
            return;
        }

        ResolvedRealtimeSettings resolved = ConfigResolver.ResolveRealtime(r);
        Range(r.MinimumIntervalMs, 50, 10000, $"{root}.minimumIntervalMs", i);
        Range(r.MaximumIntervalMs, 50, 30000, $"{root}.maximumIntervalMs", i);
        Range(r.MinimumChangedWords, 1, 100, $"{root}.minimumChangedWords", i);
        Range(r.NewUtteranceAfterMs, 250, 30000, $"{root}.newUtteranceAfterMs", i);
        Range(r.MaxSourceCharacters, 64, 20000, $"{root}.maxSourceCharacters", i);
        if (resolved.MinimumIntervalMs > resolved.MaximumIntervalMs)
        {
            i.Add(new(
                $"{root}.maximumIntervalMs",
                "The value must be greater than or equal to pipeline.realtime.minimumIntervalMs."));
        }
    }
    private static void Range(int? value, int minimum, int maximum, string path, List<ConfigIssue> issues)
    {
        if (value is not null && (value < minimum || value > maximum))
            issues.Add(new(path, $"The value must be between {minimum} and {maximum}."));
    }
    public static ConfigIssue? ValidateOutput(
        OutputProviderConfig output,
        string path = "outputs.address")
    {
        if (output is not VrChatOscConfig osc)
            return null;
        string address = osc.Address ?? "127.0.0.1:9000";
        string[] parts = address.Split(':', 2);
        return parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out _) ||
            !int.TryParse(parts[1], out int port) ||
            port is < 1 or > 65535
                ? new(
                    path,
                    "Expected an IP address and port, for example 127.0.0.1:9000.")
                : null;
    }
    private static void Required(string? value,string path,List<ConfigIssue> i) { if(string.IsNullOrWhiteSpace(value))i.Add(new(path,"A non-empty value is required.")); else if(path.EndsWith("baseUrl",StringComparison.Ordinal)&&!Uri.TryCreate(value,UriKind.Absolute,out _))i.Add(new(path,"Expected an absolute URL.")); }
}

public sealed record ConfigLoadResult(FoxTransConfig? Config, string Path, ConfigLoadState State, IReadOnlyList<string> Warnings);
public enum ConfigLoadState { Loaded, Created, Migrated }
public sealed class ConfigurationException(string message) : Exception(message);

public static class AppConfig
{
    private const string EmbeddedSchemaName = "FoxTrans.foxtrans.schema.json";

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        TypeInfoResolver = FoxTransJsonContext.Default,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    public static ConfigLoadResult LoadOrCreate(string directory = ".")
    {
        string canonical=Path.Combine(directory,"config.jsonc"), legacy=Path.Combine(directory,"config.json"), schema=Path.Combine(directory,"foxtrans.schema.json");
        if(File.Exists(canonical)){ EnsureSchema(schema); FoxTransConfig config = Read(canonical); return new(config,canonical,ConfigLoadState.Loaded,LoadWarnings(config, File.Exists(legacy)?["config.json is ignored because config.jsonc exists."]:[])); }
        if(File.Exists(legacy)) return Migrate(legacy,canonical,schema);
        Directory.CreateDirectory(directory); File.WriteAllText(canonical, Serialize(Default())); EnsureSchema(schema); return new(Default(),canonical,ConfigLoadState.Created,[]);
    }
    public static ConfigLoadResult LoadExplicit(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
            throw new ConfigurationException($"Configuration path is a directory: {fullPath}");
        if (!File.Exists(fullPath))
            throw new ConfigurationException($"Configuration file does not exist: {fullPath}");
        FoxTransConfig config = Read(fullPath);
        return new(config, fullPath, ConfigLoadState.Loaded, LoadWarnings(config, []));
    }
    public static string? ResolveSchemaPath(FoxTransConfig config, string configPath)
    {
        if (Uri.TryCreate(config.Schema, UriKind.Absolute, out Uri? schemaUri))
            return schemaUri.IsFile ? schemaUri.LocalPath : null;
        return Path.GetFullPath(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(configPath))!, config.Schema));
    }
    public static FoxTransConfig Default() => new(Audio:new(),Pipeline:new(new WebRtcVadConfig(),new OpenAiChatAudioConfig("https://openrouter.ai/api/v1","env:OPENROUTER_API_KEY","google/gemini-2.5-flash","Translate this audio to English. Reply only with the translated text.")),Outputs:[new VrChatOscConfig()]);
    public static string Serialize(FoxTransConfig c)=>JsonSerializer.Serialize(c,FoxTransJsonContext.Default.FoxTransConfig)+Environment.NewLine;
    public static FoxTransConfig Read(string path) { try { return JsonSerializer.Deserialize(File.ReadAllText(path),FoxTransJsonContext.Default.FoxTransConfig)??throw new ConfigurationException($"Configuration error in {path}: file is empty."); } catch(JsonException e){throw new ConfigurationException($"Configuration error in {path} at line {e.LineNumber}, byte {e.BytePositionInLine}: {SafeJsonMessage(e.Message)}");} }
    [RequiresDynamicCode(
        "JSON schema export is a development-time compatibility check.")]
    [RequiresUnreferencedCode(
        "JSON schema export is excluded from the published application.")]
    public static string GenerateSchema()
    {
        JsonNode schema = JsonSchemaExporter.GetJsonSchemaAsNode(
            JsonOptions,
            typeof(FoxTransConfig),
            new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        JsonObject pipeline = schema["properties"]!["pipeline"]!.AsObject();
        JsonObject vad = pipeline["properties"]!["vad"]!["anyOf"]![0]!
            ["properties"]!.AsObject();
        vad["preset"]!["description"] =
            "Selects phrase segmentation timings and WebRTC speech-detection behavior for batch audio capture. Canonical presets: short-phrases, natural-speech, long-phrases. Deprecated aliases responsive, balanced, and strict remain accepted for compatibility.";
        JsonObject realtime = pipeline["properties"]!["realtime"]!
            .AsObject();
        JsonObject properties = realtime["properties"]!.AsObject();
        properties["preset"]!["description"] =
            "Selects translation cadence, natural-pause handling, and source-window defaults.";
        properties["newUtteranceAfterMs"]!["description"] =
            "Starts a new client-side logical utterance after the cumulative transcript has not meaningfully changed for this duration. It does not reconnect Voxtral.";
        JsonObject transcription = pipeline["properties"]!["speech"]!["anyOf"]![1]!
            ["properties"]!.AsObject();
        transcription["requestFormat"]!["enum"] =
            new JsonArray("multipart", "json");
        transcription["requestFormat"]!["description"] =
            "Selects the HTTP request encoding used by the transcription endpoint. Use \"multipart\" for OpenAI-compatible file uploads and \"json\" for base64 input_audio requests such as OpenRouter STT.";
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["title"] = "FoxTrans configuration";
        schema["description"] = "One active FoxTrans pipeline.";
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
            Environment.NewLine;
    }
    private static void EnsureSchema(string path)
    {
        if (File.Exists(path))
            return;

        using Stream resource = typeof(AppConfig).Assembly
            .GetManifestResourceStream(EmbeddedSchemaName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedSchemaName}' was not found.");
        using FileStream destination = File.Create(path);
        resource.CopyTo(destination);
    }
    private static IReadOnlyList<string> LoadWarnings(FoxTransConfig config, IReadOnlyList<string> warnings)
    {
        var result = new List<string>(warnings);
        if (config.EffectivePipeline.Vad is WebRtcVadConfig vad &&
            VadPresets.TryGetDeprecatedReplacement(vad.Preset, out string replacement))
            result.Add($"pipeline.vad.preset: VAD preset \"{vad.Preset}\" is deprecated. Use \"{replacement}\".");
        return result;
    }
    private static ConfigLoadResult Migrate(string legacy,string canonical,string schema){ string text=File.ReadAllText(legacy); LegacyConfig old; try{old=JsonSerializer.Deserialize(text,LegacyConfigJsonContext.Default.LegacyConfig)??throw new ConfigurationException("Legacy configuration is empty.");}catch(JsonException e){throw new ConfigurationException($"Configuration error in {legacy}: {SafeJsonMessage(e.Message)}");} if(old.Api is null||old.Vad is null||old.Osc is null)throw new ConfigurationException($"Configuration error in {legacy}: Api, Vad, and Osc are required for migration."); string backup=Path.Combine(Path.GetDirectoryName(legacy)!,"config.legacy.json"); if(File.Exists(backup))throw new ConfigurationException($"Cannot migrate {legacy}: {backup} already exists."); string baseUrl=old.Api.Endpoint??""; if(baseUrl.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase))baseUrl=baseUrl[..^"/chat/completions".Length]; var config=new FoxTransConfig(Audio:new(),Pipeline:new(new WebRtcVadConfig("natural-speech",old.Vad.MinSpeechFrames*20,old.Vad.MinSilenceFrames*20,old.Vad.PreRollFrames*20,old.Vad.MinPhraseLengthMs),new OpenAiChatAudioConfig(baseUrl,old.Api.Key,old.Api.Model,old.Api.Prompt)),Outputs:[new VrChatOscConfig($"{old.Osc.IpAddress}:{old.Osc.Port}",old.Osc.EnableTypingIndicator)]); string tmp=canonical+".tmp"; File.WriteAllText(tmp,Serialize(config)); File.Copy(legacy,backup); File.Move(tmp,canonical); EnsureSchema(schema); return new(config,canonical,ConfigLoadState.Migrated,[]); }
    private static string SafeJsonMessage(string message)=>message.Replace("\r"," ").Replace("\n"," ");
}
