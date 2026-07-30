using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FoxTransConfig))]
[JsonSerializable(typeof(OpenAiAudioTranslationRequest))]
[JsonSerializable(typeof(OpenAiTranscriptionRequest))]
[JsonSerializable(typeof(OpenAiTextTranslationRequest))]
[JsonSerializable(typeof(VoxtralConfigureRequest))]
internal sealed partial class FoxTransJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(LegacyConfig))]
internal sealed partial class LegacyConfigJsonContext : JsonSerializerContext;

internal sealed record LegacyConfig(
    LegacyApi? Api,
    LegacyVad? Vad,
    LegacyOsc? Osc);

internal sealed record LegacyApi(
    string? Key,
    string? Endpoint,
    string? Model,
    string? Prompt);

internal sealed record LegacyVad(
    int MinSpeechFrames,
    int MinSilenceFrames,
    int PreRollFrames,
    int MinPhraseLengthMs);

internal sealed record LegacyOsc(
    string? IpAddress,
    int Port,
    bool EnableTypingIndicator);

internal sealed record OpenAiAudioTranslationRequest(
    string Model,
    IReadOnlyList<OpenAiAudioMessage> Messages);

internal sealed record OpenAiAudioMessage(
    string Role,
    IReadOnlyList<OpenAiAudioContent> Content);

internal sealed record OpenAiAudioContent(
    string Type,
    string? Text = null,
    [property: JsonPropertyName("input_audio")]
    OpenAiAudioInput? InputAudio = null);

internal sealed record OpenAiAudioInput(
    string Data,
    string Format);

internal sealed record OpenAiTranscriptionRequest(
    string Model,
    [property: JsonPropertyName("input_audio")]
    OpenAiAudioInput InputAudio,
    string? Language);

internal sealed record OpenAiTextTranslationRequest(
    string Model,
    IReadOnlyList<OpenAiTextMessage> Messages);

internal sealed record OpenAiTextMessage(
    string Role,
    string Content);

internal sealed record VoxtralConfigureRequest(
    string Type,
    [property: JsonPropertyName("transcription_delay_ms")]
    int TranscriptionDelayMs,
    VoxtralConfigureAudio Audio,
    string Language,
    VoxtralConfigureEvents Events);

internal sealed record VoxtralConfigureAudio(
    string Format,
    [property: JsonPropertyName("sample_rate")]
    int SampleRate,
    int Channels);

internal sealed record VoxtralConfigureEvents(
    bool Token,
    bool Partial);
