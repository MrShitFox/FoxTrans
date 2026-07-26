using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
    string Preset = "balanced",
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
public sealed record OpenAiTranscriptionConfig(string? BaseUrl = null, string? ApiKey = null, string? Model = null, string? Language = null) : SpeechProviderConfig;
public sealed record VoxtralFoxConfig(string? BaseUrl = null, string? ApiKey = null, int DelayMs = 240) : SpeechProviderConfig;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(OpenAiChatConfig), "openai-chat")]
public abstract record TranslationProviderConfig;
public sealed record OpenAiChatConfig(string? BaseUrl = null, string? ApiKey = null, string? Model = null, string? Prompt = null) : TranslationProviderConfig;

public sealed record RealtimeConfig(string Preset = "balanced", int? MinimumIntervalMs = null, int? MaximumIntervalMs = null, int? MinimumChangedWords = null, int? NewUtteranceAfterMs = null, int? MaxSourceCharacters = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(VrChatOscConfig), "vrchat-osc")]
public abstract record OutputProviderConfig;
public sealed record VrChatOscConfig(string? Address = null, bool? TypingIndicator = null) : OutputProviderConfig;

public enum PipelineKind { DirectAudioTranslation, BatchTranscriptionTranslation, RealtimeTranscriptionTranslation }
public sealed record ConfigIssue(string Path, string Message, string? Suggestion = null);
public sealed record ConfigValidationResult(PipelineKind? PipelineKind, IReadOnlyList<ConfigIssue> Issues)
{ public bool IsValid => Issues.Count == 0; }
public sealed record ResolvedVadSettings(int MinSpeechFrames, int MinSilenceFrames, int PreRollFrames, int MinimumPhraseMs, WebRtcVadSharp.OperatingMode OperatingMode);
public sealed record ResolvedOpenAiAudioSettings(Uri Endpoint, string? ApiKey, string Model, string Prompt);
public sealed record ResolvedOscEndpoint(string Host, int Port, bool TypingIndicator);
public sealed record SecretResolution(string? Value, ConfigIssue? Issue, string? Warning);

public static class ConfigResolver
{
    public static ResolvedVadSettings ResolveVad(WebRtcVadConfig config)
    {
        (int start, int stop, int pre, int min, WebRtcVadSharp.OperatingMode mode) = config.Preset switch
        {
            "responsive" => (160, 600, 400, 800, WebRtcVadSharp.OperatingMode.Aggressive),
            "strict" => (400, 1400, 800, 1600, WebRtcVadSharp.OperatingMode.VeryAggressive),
            _ => (240, 1000, 600, 1200, WebRtcVadSharp.OperatingMode.VeryAggressive)
        };
        start = config.StartAfterMs ?? start; stop = config.StopAfterMs ?? stop; pre = config.PreRollMs ?? pre; min = config.MinimumPhraseMs ?? min;
        return new ResolvedVadSettings(CeilFrames(start), CeilFrames(stop), CeilFrames(pre), min, mode);
    }
    public static int CeilFrames(int milliseconds) => (milliseconds + 19) / 20;
    public static SecretResolution ResolveSecret(string? value, string path, Func<string, string?> environment)
    {
        if (string.IsNullOrWhiteSpace(value)) return new(null, null, null);
        if (!value.StartsWith("env:", StringComparison.Ordinal)) return new(value, null, $"{path}: Literal API keys work, but env:NAME is recommended.");
        string name = value[4..]; string? resolved = string.IsNullOrWhiteSpace(name) ? null : environment(name);
        return string.IsNullOrWhiteSpace(resolved) ? new(null, new(path, $"Environment variable '{name}' is missing or empty."), null) : new(resolved, null, null);
    }
    public static Uri ChatEndpoint(string baseUrl) => new(baseUrl.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? baseUrl : baseUrl.TrimEnd('/') + "/chat/completions", UriKind.Absolute);
    public static ResolvedOscEndpoint ResolveOsc(VrChatOscConfig config)
    {
        string[] parts = (config.Address ?? "127.0.0.1:9000").Split(':', 2);
        return new(parts[0], int.Parse(parts[1]), config.TypingIndicator ?? true);
    }
}

public static class ConfigValidator
{
    private static readonly HashSet<int> VoxtralDelays = [120, 240, 480];
    public static ConfigValidationResult Validate(FoxTransConfig config)
    {
        var issues = new List<ConfigIssue>(); PipelineConfig p = config.EffectivePipeline;
        if (config.Version != 1) issues.Add(new("version", "Only configuration version 1 is supported."));
        if (p.Speech is null) issues.Add(new("pipeline.speech", "A speech provider is required."));
        if (config.EffectiveOutputs.Count == 0) issues.Add(new("outputs", "At least one output is required."));
        foreach (OutputProviderConfig output in config.EffectiveOutputs) ValidateOutput(output, issues);
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
    private static PipelineKind ValidateDirect(PipelineConfig p, OpenAiChatAudioConfig a, List<ConfigIssue> i) { Required(a.BaseUrl,"pipeline.speech.baseUrl",i); Required(a.Model,"pipeline.speech.model",i); Required(a.Prompt,"pipeline.speech.prompt",i); if (p.Vad is null)i.Add(new("pipeline.vad","A VAD provider is required for direct audio translation.")); if(p.Translation is not null)i.Add(new("pipeline.translation","The speech provider \"openai-chat-audio\" already returns translated text. Remove the translation section.")); return PipelineKind.DirectAudioTranslation; }
    private static PipelineKind ValidateBatch(PipelineConfig p, OpenAiTranscriptionConfig a, List<ConfigIssue> i) { Required(a.BaseUrl,"pipeline.speech.baseUrl",i); Required(a.Model,"pipeline.speech.model",i); if(p.Vad is null)i.Add(new("pipeline.vad","A VAD provider is required for transcription.")); ValidateTranslation(p.Translation,i); return PipelineKind.BatchTranscriptionTranslation; }
    private static PipelineKind ValidateRealtimePipeline(PipelineConfig p, VoxtralFoxConfig a, List<ConfigIssue> i) { Required(a.BaseUrl,"pipeline.speech.baseUrl",i); if(!VoxtralDelays.Contains(a.DelayMs))i.Add(new("pipeline.speech.delayMs","The value must be one of the supported Voxtral delay values.")); if(p.Vad is not null)i.Add(new("pipeline.vad","The speech provider \"voxtral-fox\" receives continuous audio. Remove VAD from this pipeline.")); if(p.Realtime is null)i.Add(new("pipeline.realtime","Realtime settings are required when speech.type is \"voxtral-fox\".")); ValidateTranslation(p.Translation,i); return PipelineKind.RealtimeTranscriptionTranslation; }
    private static void ValidateTranslation(TranslationProviderConfig? t,List<ConfigIssue> i) { if(t is not OpenAiChatConfig a){i.Add(new("pipeline.translation","The speech provider returns source text, so a translation provider is required.")); return;} Required(a.BaseUrl,"pipeline.translation.baseUrl",i); Required(a.Model,"pipeline.translation.model",i); Required(a.Prompt,"pipeline.translation.prompt",i); }
    private static void ValidateVad(WebRtcVadConfig v,List<ConfigIssue> i) { if(v.Preset is not ("responsive" or "balanced" or "strict")) i.Add(new("pipeline.vad.preset","Expected responsive, balanced, or strict.")); foreach((string n,int? x) in new[]{("startAfterMs",v.StartAfterMs),("stopAfterMs",v.StopAfterMs),("preRollMs",v.PreRollMs),("minimumPhraseMs",v.MinimumPhraseMs)}) if(x is <=0 or >60000)i.Add(new($"pipeline.vad.{n}","The value must be between 1 and 60000 milliseconds.")); }
    private static void ValidateRealtime(RealtimeConfig r,List<ConfigIssue> i) { if(r.Preset is not ("responsive" or "balanced" or "economical"))i.Add(new("pipeline.realtime.preset","Expected responsive, balanced, or economical.")); foreach((string n,int? x) in new[]{("minimumIntervalMs",r.MinimumIntervalMs),("maximumIntervalMs",r.MaximumIntervalMs),("newUtteranceAfterMs",r.NewUtteranceAfterMs),("maxSourceCharacters",r.MaxSourceCharacters)})if(x is <=0)i.Add(new($"pipeline.realtime.{n}","The value must be positive.")); }
    private static void ValidateOutput(OutputProviderConfig o,List<ConfigIssue> i) { if(o is VrChatOscConfig v){string a=v.Address??"127.0.0.1:9000"; string[] p=a.Split(':',2); if(p.Length!=2||!IPAddress.TryParse(p[0],out _)||!int.TryParse(p[1],out int port)||port is <1 or >65535)i.Add(new("outputs.address","Expected an IP address and port, for example 127.0.0.1:9000."));} }
    private static void Required(string? value,string path,List<ConfigIssue> i) { if(string.IsNullOrWhiteSpace(value))i.Add(new(path,"A non-empty value is required.")); else if(path.EndsWith("baseUrl",StringComparison.Ordinal)&&!Uri.TryCreate(value,UriKind.Absolute,out _))i.Add(new(path,"Expected an absolute URL.")); }
}

public sealed record ConfigLoadResult(FoxTransConfig? Config, string Path, ConfigLoadState State, IReadOnlyList<string> Warnings);
public enum ConfigLoadState { Loaded, Created, Migrated }
public sealed class ConfigurationException(string message) : Exception(message);

public static class AppConfig
{
    public static JsonSerializerOptions JsonOptions { get; } = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver(), PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public static ConfigLoadResult LoadOrCreate(string directory = ".")
    {
        string canonical=Path.Combine(directory,"config.jsonc"), legacy=Path.Combine(directory,"config.json"), schema=Path.Combine(directory,"foxtrans.schema.json");
        if(File.Exists(canonical)){ EnsureSchema(schema); return new(Read(canonical),canonical,ConfigLoadState.Loaded,File.Exists(legacy)?["config.json is ignored because config.jsonc exists."]:[]); }
        if(File.Exists(legacy)) return Migrate(legacy,canonical,schema);
        Directory.CreateDirectory(directory); File.WriteAllText(canonical, Serialize(Default())); EnsureSchema(schema); return new(Default(),canonical,ConfigLoadState.Created,[]);
    }
    public static FoxTransConfig Default() => new(Audio:new(),Pipeline:new(new WebRtcVadConfig(),new OpenAiChatAudioConfig("https://openrouter.ai/api/v1","env:OPENROUTER_API_KEY","google/gemini-2.5-flash","Translate this audio to English. Reply only with the translated text.")),Outputs:[new VrChatOscConfig()]);
    public static string Serialize(FoxTransConfig c)=>JsonSerializer.Serialize(c,JsonOptions)+Environment.NewLine;
    public static FoxTransConfig Read(string path) { try { return JsonSerializer.Deserialize<FoxTransConfig>(File.ReadAllText(path),JsonOptions)??throw new ConfigurationException($"Configuration error in {path}: file is empty."); } catch(JsonException e){throw new ConfigurationException($"Configuration error in {path} at line {e.LineNumber}, byte {e.BytePositionInLine}: {SafeJsonMessage(e.Message)}");} }
    public static string GenerateSchema(){ JsonNode n=JsonSchemaExporter.GetJsonSchemaAsNode(JsonOptions,typeof(FoxTransConfig),new JsonSchemaExporterOptions{TreatNullObliviousAsNonNullable=true}); n["$schema"]="https://json-schema.org/draft/2020-12/schema"; n["title"]="FoxTrans configuration"; n["description"]="One active FoxTrans pipeline."; return n.ToJsonString(new JsonSerializerOptions{WriteIndented=true})+Environment.NewLine; }
    private static void EnsureSchema(string path){ if(!File.Exists(path))File.WriteAllText(path,GenerateSchema()); }
    private static ConfigLoadResult Migrate(string legacy,string canonical,string schema){ string text=File.ReadAllText(legacy); LegacyConfig old; try{old=JsonSerializer.Deserialize<LegacyConfig>(text,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new ConfigurationException("Legacy configuration is empty.");}catch(JsonException e){throw new ConfigurationException($"Configuration error in {legacy}: {SafeJsonMessage(e.Message)}");} if(old.Api is null||old.Vad is null||old.Osc is null)throw new ConfigurationException($"Configuration error in {legacy}: Api, Vad, and Osc are required for migration."); string backup=Path.Combine(Path.GetDirectoryName(legacy)!,"config.legacy.json"); if(File.Exists(backup))throw new ConfigurationException($"Cannot migrate {legacy}: {backup} already exists."); string baseUrl=old.Api.Endpoint??""; if(baseUrl.EndsWith("/chat/completions",StringComparison.OrdinalIgnoreCase))baseUrl=baseUrl[..^"/chat/completions".Length]; var config=new FoxTransConfig(Audio:new(),Pipeline:new(new WebRtcVadConfig("balanced",old.Vad.MinSpeechFrames*20,old.Vad.MinSilenceFrames*20,old.Vad.PreRollFrames*20,old.Vad.MinPhraseLengthMs),new OpenAiChatAudioConfig(baseUrl,old.Api.Key,old.Api.Model,old.Api.Prompt)),Outputs:[new VrChatOscConfig($"{old.Osc.IpAddress}:{old.Osc.Port}",old.Osc.EnableTypingIndicator)]); string tmp=canonical+".tmp"; File.WriteAllText(tmp,Serialize(config)); File.Copy(legacy,backup); File.Move(tmp,canonical); EnsureSchema(schema); return new(config,canonical,ConfigLoadState.Migrated,[]); }
    private static string SafeJsonMessage(string message)=>message.Replace("\r"," ").Replace("\n"," ");
    private sealed record LegacyConfig(LegacyApi? Api,LegacyVad? Vad,LegacyOsc? Osc); private sealed record LegacyApi(string? Key,string? Endpoint,string? Model,string? Prompt); private sealed record LegacyVad(int MinSpeechFrames,int MinSilenceFrames,int PreRollFrames,int MinPhraseLengthMs); private sealed record LegacyOsc(string? IpAddress,int Port,bool EnableTypingIndicator);
}
