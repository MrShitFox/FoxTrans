using System.Text.RegularExpressions;

namespace FoxTrans.Desktop.Services;

public sealed partial class DesktopSecretRedactor
{
    private readonly string[] _literalSecrets;

    public DesktopSecretRedactor(
        ResolvedExecutionPlan? plan,
        FoxTransConfig? config = null)
    {
        config ??= plan?.Config;
        PipelineConfig configured = config?.EffectivePipeline ?? new();
        IEnumerable<string?> configuredSecrets = configured.Speech switch
        {
            OpenAiChatAudioConfig audio =>
                [audio.ApiKey, audio.Prompt],
            OpenAiTranscriptionConfig transcription =>
                [transcription.ApiKey],
            VoxtralFoxConfig voxtral =>
                [voxtral.ApiKey],
            _ => []
        };
        if (configured.Translation is OpenAiChatConfig translation)
        {
            configuredSecrets = configuredSecrets.Concat(
                [translation.ApiKey, translation.Prompt]);
        }

        _literalSecrets =
        [
            ..new[]
            {
                plan?.Direct?.ApiKey,
                plan?.Transcription?.ApiKey,
                plan?.Translation?.ApiKey,
                plan?.Voxtral?.ApiKey,
                plan?.Direct?.Prompt,
                plan?.Translation?.Prompt
            }
            .Concat(configuredSecrets)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
        ];
    }

    public AppEvent Redact(AppEvent appEvent) =>
        appEvent.Message is null
            ? appEvent
            : appEvent with { Message = Redact(appEvent.Message) };

    public string Redact(string value)
    {
        string safe = value;
        foreach (string secret in _literalSecrets)
            safe = safe.Replace(secret, "<redacted>", StringComparison.Ordinal);
        safe = AuthorizationHeader().Replace(
            safe,
            "Authorization: <redacted>");
        safe = SecretQuery().Replace(
            safe,
            match => match.Groups[1].Value + "=<redacted>");
        safe = LongBase64().Replace(safe, "<binary audio omitted>");
        safe = HttpEndpoint().Replace(safe, SanitizeEndpoint);
        return safe;
    }

    private static string SanitizeEndpoint(Match match)
    {
        string candidate = match.Value.TrimEnd('.', ',', ';', ')', ']');
        string suffix = match.Value[candidate.Length..];
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
            return "<endpoint>";
        var builder = new UriBuilder(uri)
        {
            UserName = "",
            Password = "",
            Query = "",
            Fragment = ""
        };
        return builder.Uri.ToString() + suffix;
    }

    [GeneratedRegex(
        @"(?i)Authorization\s*:\s*(?:Bearer\s+)?[^\s,;]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeader();

    [GeneratedRegex(
        @"(?i)\b(api[_-]?key|access[_-]?token|token|secret|signature)=([^&\s]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretQuery();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{120,}={0,2}(?![A-Za-z0-9+/])",
        RegexOptions.CultureInvariant)]
    private static partial Regex LongBase64();

    [GeneratedRegex(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpEndpoint();
}
