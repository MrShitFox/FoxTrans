using System.Text;

Console.OutputEncoding = Encoding.UTF8;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
ConsoleUi? ui = null;
try
{
    ConfigLoadResult loaded = AppConfig.LoadOrCreate();
    ui = new ConsoleUi(loaded.Config);
    if (loaded.State == ConfigLoadState.Created) { ui.Report(AppEvent.ConfigCreated(loaded.Path)); return; }
    if (loaded.State == ConfigLoadState.Migrated) { ui.Report(AppEvent.ConfigMigrated(loaded.Path)); return; }
    foreach (string warning in loaded.Warnings) ui.Report(AppEvent.ConfigWarning(warning));

    FoxTransConfig config = loaded.Config!;
    ConfigValidationResult validation = ConfigValidator.Validate(config);
    var secrets = new Dictionary<string, string?>();
    var secretIssues = new List<ConfigIssue>();
    foreach ((string? value, string path) in SecretValues(config))
    {
        SecretResolution resolution = ConfigResolver.ResolveSecret(value, path, Environment.GetEnvironmentVariable);
        secrets[path] = resolution.Value;
        if (resolution.Issue is not null) secretIssues.Add(resolution.Issue);
        if (resolution.Warning is not null) ui.Report(AppEvent.ConfigWarning(resolution.Warning));
    }
    if (!validation.IsValid || secretIssues.Count > 0)
        throw new ConfigurationException(string.Join(Environment.NewLine, validation.Issues.Concat(secretIssues).Select(i => $"{i.Path}: {i.Message}")));

    if (validation.PipelineKind == PipelineKind.RealtimeTranscriptionTranslation)
    {
        ui.Report(AppEvent.UnsupportedValidPipeline("The valid RealtimeTranscriptionTranslation pipeline is not executable in this beta yet."));
        return;
    }

    ResolvedOpenAiAudioSettings? directSettings = null;
    ResolvedOpenAiTranscriptionSettings? transcriptionSettings = null;
    ResolvedOpenAiChatSettings? translationSettings = null;
    switch (validation.PipelineKind)
    {
        case PipelineKind.DirectAudioTranslation:
            var direct = (OpenAiChatAudioConfig)config.EffectivePipeline.Speech!;
            directSettings = new ResolvedOpenAiAudioSettings(ConfigResolver.ChatEndpoint(direct.BaseUrl!), secrets["pipeline.speech.apiKey"], direct.Model!, direct.Prompt!);
            break;
        case PipelineKind.BatchTranscriptionTranslation:
            transcriptionSettings = ConfigResolver.ResolveTranscription((OpenAiTranscriptionConfig)config.EffectivePipeline.Speech!, secrets["pipeline.speech.apiKey"]);
            translationSettings = ConfigResolver.ResolveChat((OpenAiChatConfig)config.EffectivePipeline.Translation!, secrets["pipeline.translation.apiKey"]);
            break;
    }
    var vad = ConfigResolver.ResolveVad((WebRtcVadConfig)config.EffectivePipeline.Vad!);
    ResolvedOscEndpoint[] outputSettings = config.EffectiveOutputs.Cast<VrChatOscConfig>().Select(ConfigResolver.ResolveOsc).ToArray();
    var outputs = outputSettings.Select(x => new VrChatOscOutput(x)).ToArray();
    try
    {
        var format = new AudioFormat(16000, 16, 1);
        using var microphone = new NAudioMicrophoneSource(format);
        using var segmenter = new WebRtcVadSegmenter(vad);
        using var httpClient = new HttpClient();
        switch (validation.PipelineKind)
        {
            case PipelineKind.DirectAudioTranslation:
                await FoxTransApp.RunDirectAudioPipelineAsync(microphone, segmenter, new OpenAiAudioTranslator(httpClient, directSettings!), outputs, ui, cancellationToken: shutdown.Token);
                break;
            case PipelineKind.BatchTranscriptionTranslation:
                await FoxTransApp.RunBatchTranscriptionPipelineAsync(microphone, segmenter, new OpenAiTranscriber(httpClient, transcriptionSettings!), new OpenAiTextTranslator(httpClient, translationSettings!), outputs, ui, cancellationToken: shutdown.Token);
                break;
            default:
                throw new ConfigurationException("No executable pipeline was selected.");
        }
    }
    finally
    {
        foreach (VrChatOscOutput output in outputs) await output.DisposeAsync();
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { Environment.ExitCode = 0; }
catch (Exception ex)
{
    if (ui is null) Console.Error.WriteLine($"FoxTrans failed: {ex.Message}");
    else ui.Report(ex is ConfigurationException ? AppEvent.ConfigError(ex.Message) : AppEvent.FatalError(ex.Message));
    Environment.ExitCode = 1;
}
finally { ui?.Dispose(); }

static IEnumerable<(string? Value, string Path)> SecretValues(FoxTransConfig c)
{
    if (c.EffectivePipeline.Speech is OpenAiChatAudioConfig audio) yield return (audio.ApiKey, "pipeline.speech.apiKey");
    if (c.EffectivePipeline.Speech is OpenAiTranscriptionConfig transcription) yield return (transcription.ApiKey, "pipeline.speech.apiKey");
    if (c.EffectivePipeline.Speech is VoxtralFoxConfig voxtral) yield return (voxtral.ApiKey, "pipeline.speech.apiKey");
    if (c.EffectivePipeline.Translation is OpenAiChatConfig translation) yield return (translation.ApiKey, "pipeline.translation.apiKey");
}
