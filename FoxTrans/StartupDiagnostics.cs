public sealed record ResolvedExecutionPlan(
    FoxTransConfig Config,
    PipelineKind PipelineKind,
    ResolvedAudioInput Audio,
    IReadOnlyList<ResolvedOscEndpoint> Outputs,
    ResolvedVadSettings? Vad = null,
    ResolvedOpenAiAudioSettings? Direct = null,
    ResolvedOpenAiTranscriptionSettings? Transcription = null,
    ResolvedOpenAiChatSettings? Translation = null,
    ResolvedVoxtralFoxSettings? Voxtral = null,
    ResolvedRealtimeSettings? Realtime = null);

public sealed record ExecutionPlanResolution(
    ResolvedExecutionPlan? Plan,
    IReadOnlyList<ConfigIssue> Issues,
    IReadOnlyList<string> Warnings)
{
    public bool IsValid => Plan is not null && Issues.Count == 0;
}

public static class ExecutionPlanResolver
{
    private static readonly AudioFormat RequiredFormat = new(16000, 16, 1);

    public static ExecutionPlanResolution Resolve(
        FoxTransConfig config,
        IReadOnlyList<AudioInputDevice> devices,
        Func<string, string?> environment)
    {
        var issues = new List<ConfigIssue>();
        var warnings = new List<string>();
        ConfigValidationResult validation = ConfigValidator.Validate(config);
        issues.AddRange(validation.Issues);

        ResolvedAudioInput? audio = null;
        try
        {
            audio = AudioDeviceSelection.Resolve(
                config.EffectiveAudio.Device,
                devices,
                RequiredFormat);
        }
        catch (AudioDeviceSelectionException exception)
        {
            issues.Add(new("audio.device", exception.Message));
        }

        var outputs = new List<ResolvedOscEndpoint>();
        foreach (OutputProviderConfig output in config.EffectiveOutputs)
        {
            if (output is not VrChatOscConfig osc)
                continue;
            try
            {
                outputs.Add(ConfigResolver.ResolveOsc(osc));
            }
            catch (Exception exception)
            {
                issues.Add(new("outputs.address", exception.Message));
            }
        }

        string? Resolve(string? value, string path)
        {
            SecretResolution result = ConfigResolver.ResolveSecret(value, path, environment);
            if (result.Issue is not null)
                issues.Add(result.Issue);
            if (result.Warning is not null)
                warnings.Add(result.Warning);
            return result.Value;
        }

        if (validation.PipelineKind is null || issues.Count != 0 || audio is null)
        {
            switch (config.EffectivePipeline.Speech)
            {
                case OpenAiChatAudioConfig direct:
                    _ = Resolve(direct.ApiKey, "pipeline.speech.apiKey");
                    break;
                case OpenAiTranscriptionConfig transcription:
                    _ = Resolve(transcription.ApiKey, "pipeline.speech.apiKey");
                    if (config.EffectivePipeline.Translation is OpenAiChatConfig batchTranslation)
                        _ = Resolve(batchTranslation.ApiKey, "pipeline.translation.apiKey");
                    break;
                case VoxtralFoxConfig voxtral:
                    _ = Resolve(voxtral.ApiKey, "pipeline.speech.apiKey");
                    if (config.EffectivePipeline.Translation is OpenAiChatConfig realtimeTranslation)
                        _ = Resolve(realtimeTranslation.ApiKey, "pipeline.translation.apiKey");
                    break;
            }
            return new(null, issues, warnings);
        }

        try
        {
            ResolvedExecutionPlan plan;
            PipelineConfig pipeline = config.EffectivePipeline;
            switch (validation.PipelineKind.Value)
            {
                case PipelineKind.DirectAudioTranslation:
                {
                    var speech = (OpenAiChatAudioConfig)pipeline.Speech!;
                    plan = new(
                        config,
                        validation.PipelineKind.Value,
                        audio,
                        outputs,
                        Vad: ConfigResolver.ResolveVad((WebRtcVadConfig)pipeline.Vad!),
                        Direct: new(
                            ConfigResolver.ChatEndpoint(speech.BaseUrl!),
                            Resolve(speech.ApiKey, "pipeline.speech.apiKey"),
                            speech.Model!,
                            speech.Prompt!));
                    break;
                }
                case PipelineKind.BatchTranscriptionTranslation:
                {
                    var speech = (OpenAiTranscriptionConfig)pipeline.Speech!;
                    var translation = (OpenAiChatConfig)pipeline.Translation!;
                    plan = new(
                        config,
                        validation.PipelineKind.Value,
                        audio,
                        outputs,
                        Vad: ConfigResolver.ResolveVad((WebRtcVadConfig)pipeline.Vad!),
                        Transcription: ConfigResolver.ResolveTranscription(
                            speech,
                            Resolve(speech.ApiKey, "pipeline.speech.apiKey")),
                        Translation: ConfigResolver.ResolveChat(
                            translation,
                            Resolve(translation.ApiKey, "pipeline.translation.apiKey")));
                    break;
                }
                case PipelineKind.RealtimeTranscriptionTranslation:
                {
                    var speech = (VoxtralFoxConfig)pipeline.Speech!;
                    var translation = (OpenAiChatConfig)pipeline.Translation!;
                    plan = new(
                        config,
                        validation.PipelineKind.Value,
                        audio,
                        outputs,
                        Translation: ConfigResolver.ResolveChat(
                            translation,
                            Resolve(translation.ApiKey, "pipeline.translation.apiKey")),
                        Voxtral: ConfigResolver.ResolveVoxtral(
                            speech,
                            Resolve(speech.ApiKey, "pipeline.speech.apiKey")),
                        Realtime: ConfigResolver.ResolveRealtime(pipeline.Realtime!));
                    break;
                }
                default:
                    throw new ConfigurationException("No executable pipeline was selected.");
            }
            return issues.Count == 0
                ? new(plan, issues, warnings)
                : new(null, issues, warnings);
        }
        catch (Exception exception) when (
            exception is ConfigurationException or UriFormatException or FormatException)
        {
            issues.Add(new("pipeline", exception.Message));
            return new(null, issues, warnings);
        }
    }
}

public static class DiagnosticFormatting
{
    public static string FormatPlan(ResolvedExecutionPlan plan)
    {
        string pipeline = plan.PipelineKind switch
        {
            PipelineKind.DirectAudioTranslation => "direct audio translation",
            PipelineKind.BatchTranscriptionTranslation => "classic transcription and translation",
            _ => "realtime transcription and translation"
        };
        var lines = new List<string>
        {
            $"Pipeline: {pipeline}",
            $"Audio: device {plan.Audio.DeviceNumber}  {plan.Audio.DisplayName}"
        };
        if (plan.Direct is not null)
            lines.Add($"Speech/translation: OpenAI-compatible chat audio, model {plan.Direct.Model}, endpoint {SafeEndpoint(plan.Direct.Endpoint)}");
        if (plan.Transcription is not null)
        {
            lines.Add("Speech: OpenAI-compatible transcription");
            lines.Add($"Transcription endpoint: {SafeEndpoint(plan.Transcription.Endpoint)}");
            lines.Add($"Transcription model: {plan.Transcription.Model}");
            lines.Add($"Transcription language: {plan.Transcription.Language ?? "automatic detection"}");
            lines.Add(
                "Transcription request format: " +
                (plan.Transcription.RequestFormat == OpenAiTranscriptionRequestFormat.Json
                    ? "JSON base64"
                    : "multipart"));
        }
        if (plan.Voxtral is not null)
            lines.Add($"Speech: VoxtralFox at {plan.Voxtral.RealtimeEndpoint.Host}:{plan.Voxtral.RealtimeEndpoint.Port}, delay {plan.Voxtral.DelayMs} ms");
        if (plan.Translation is not null)
            lines.Add($"Translation: OpenAI-compatible chat, model {plan.Translation.Model}, endpoint {SafeEndpoint(plan.Translation.Endpoint)}");
        if (plan.Vad is not null)
        {
            WebRtcVadConfig vad = (WebRtcVadConfig)plan.Config.EffectivePipeline.Vad!;
            _ = VadPresets.TryGet(vad.Preset, out VadPresetDefinition preset);
            lines.Add($"VAD phrase preset: {preset.Name}");
            lines.Add($"Speech start: {plan.Vad.MinSpeechFrames * 20} ms");
            lines.Add($"Speech end pause: {plan.Vad.MinSilenceFrames * 20} ms");
            lines.Add($"Pre-roll: {plan.Vad.PreRollFrames * 20} ms");
            lines.Add($"Minimum phrase: {plan.Vad.MinimumPhraseMs} ms");
        }
        lines.Add("Outputs: " + string.Join(", ", plan.Outputs.Select(output =>
            $"VRChat OSC at {output.Host}:{output.Port}")));
        if (plan.Realtime is not null)
        {
            ResolvedRealtimeSettings realtime = plan.Realtime;
            lines.Add($"Realtime preset: {plan.Config.EffectivePipeline.Realtime!.Preset}");
            lines.Add(
                $"Translation interval: {realtime.MinimumIntervalMs}-{realtime.MaximumIntervalMs} ms");
            lines.Add($"Changed-word trigger: {realtime.MinimumChangedWords}");
            lines.Add($"New utterance pause: {realtime.NewUtteranceAfterMs} ms");
            lines.Add($"Source window: {realtime.MaxSourceCharacters} text elements");
        }
        lines.Add("Secrets: resolved");
        return string.Join(Environment.NewLine, lines);
    }

    public static string OpenAiConnectivityNotice(Uri endpoint) =>
        $"Endpoint syntax: valid ({SafeEndpoint(endpoint)}){Environment.NewLine}" +
        "Credentials: resolved" + Environment.NewLine +
        "Connectivity: not probed because the provider has no standardized non-inference health route";

    private static string SafeEndpoint(Uri endpoint) =>
        new UriBuilder(endpoint) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.ToString();
}

public sealed record ReadinessCheckResult(
    IReadOnlyList<string> Successes,
    IReadOnlyList<string> Failures)
{
    public bool IsReady => Failures.Count == 0;
}

public static class ReadinessChecks
{
    public static async Task<ReadinessCheckResult> RunAsync(
        ResolvedExecutionPlan plan,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var successes = new List<string>
        {
            "Configuration: valid",
            $"Audio: device {plan.Audio.DeviceNumber} {plan.Audio.DisplayName}; required format 16000 Hz PCM16LE mono",
            "Output addresses: valid"
        };
        var failures = new List<string>();

        if (plan.Direct is not null)
            successes.Add("Direct provider:\n" + DiagnosticFormatting.OpenAiConnectivityNotice(plan.Direct.Endpoint));
        if (plan.Transcription is not null)
            successes.Add("Transcription provider:\n" + DiagnosticFormatting.OpenAiConnectivityNotice(plan.Transcription.Endpoint));
        if (plan.Translation is not null)
            successes.Add("Translation provider:\n" + DiagnosticFormatting.OpenAiConnectivityNotice(plan.Translation.Endpoint));
        if (plan.Voxtral is not null)
        {
            try
            {
                VoxtralHealthInfo health = await new VoxtralFoxHealthClient(httpClient)
                    .CheckAsync(plan.Voxtral, cancellationToken);
                successes.Add(
                    $"Voxtral /health: ready; model {health.Model}; delay {plan.Voxtral.DelayMs} ms; " +
                    $"{health.SampleRate} Hz {health.AudioFormat} mono; max active streams {health.MaxActiveStreams}");
            }
            catch (VoxtralFoxException exception)
            {
                failures.Add(exception.Code == "server_busy"
                    ? $"Voxtral /health: busy ({exception.Message})"
                    : $"Voxtral /health: unavailable ({exception.Message})");
            }
        }
        return new(successes, failures);
    }
}
