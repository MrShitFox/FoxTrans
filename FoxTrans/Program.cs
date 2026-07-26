using System.Text;

Console.OutputEncoding = Encoding.UTF8;
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

ConsoleUi? ui = null;
try
{
    ConfigLoadResult loaded = AppConfig.LoadOrCreate();
    ui = new ConsoleUi(loaded.Config);
    if (loaded.State == ConfigLoadState.Created)
    {
        ui.Report(AppEvent.ConfigCreated(loaded.Path));
        return;
    }
    if (loaded.State == ConfigLoadState.Migrated)
    {
        ui.Report(AppEvent.ConfigMigrated(loaded.Path));
        return;
    }
    foreach (string warning in loaded.Warnings)
        ui.Report(AppEvent.ConfigWarning(warning));

    FoxTransConfig config = loaded.Config!;
    ConfigValidationResult validation = ConfigValidator.Validate(config);
    if (!validation.IsValid)
    {
        throw new ConfigurationException(string.Join(
            Environment.NewLine,
            validation.Issues.Select(issue => $"{issue.Path}: {issue.Message}")));
    }

    var format = new AudioFormat(16000, 16, 1);
    switch (validation.PipelineKind)
    {
        case PipelineKind.DirectAudioTranslation:
        {
            var speech = (OpenAiChatAudioConfig)config.EffectivePipeline.Speech!;
            string? speechKey = ResolveSecret(
                speech.ApiKey,
                "pipeline.speech.apiKey",
                ui);
            var settings = new ResolvedOpenAiAudioSettings(
                ConfigResolver.ChatEndpoint(speech.BaseUrl!),
                speechKey,
                speech.Model!,
                speech.Prompt!);
            ResolvedVadSettings vadSettings =
                ConfigResolver.ResolveVad((WebRtcVadConfig)config.EffectivePipeline.Vad!);
            ResolvedOscEndpoint[] outputSettings = config.EffectiveOutputs
                .Cast<VrChatOscConfig>()
                .Select(ConfigResolver.ResolveOsc)
                .ToArray();
            var outputs = outputSettings.Select(item => new VrChatOscOutput(item)).ToArray();
            try
            {
                using var microphone = new NAudioMicrophoneSource(format);
                using var segmenter = new WebRtcVadSegmenter(vadSettings);
                using var httpClient = new HttpClient();
                await FoxTransApp.RunDirectAudioPipelineAsync(
                    microphone,
                    segmenter,
                    new OpenAiAudioTranslator(httpClient, settings),
                    outputs,
                    ui,
                    cancellationToken: shutdown.Token);
            }
            finally
            {
                foreach (VrChatOscOutput output in outputs)
                    await output.DisposeAsync();
            }
            break;
        }
        case PipelineKind.BatchTranscriptionTranslation:
        {
            var speech = (OpenAiTranscriptionConfig)config.EffectivePipeline.Speech!;
            var translation = (OpenAiChatConfig)config.EffectivePipeline.Translation!;
            string? speechKey = ResolveSecret(
                speech.ApiKey,
                "pipeline.speech.apiKey",
                ui);
            string? translationKey = ResolveSecret(
                translation.ApiKey,
                "pipeline.translation.apiKey",
                ui);
            ResolvedOpenAiTranscriptionSettings transcriptionSettings =
                ConfigResolver.ResolveTranscription(speech, speechKey);
            ResolvedOpenAiChatSettings translationSettings =
                ConfigResolver.ResolveChat(translation, translationKey);
            ResolvedVadSettings vadSettings =
                ConfigResolver.ResolveVad((WebRtcVadConfig)config.EffectivePipeline.Vad!);
            ResolvedOscEndpoint[] outputSettings = config.EffectiveOutputs
                .Cast<VrChatOscConfig>()
                .Select(ConfigResolver.ResolveOsc)
                .ToArray();
            var outputs = outputSettings.Select(item => new VrChatOscOutput(item)).ToArray();
            try
            {
                using var microphone = new NAudioMicrophoneSource(format);
                using var segmenter = new WebRtcVadSegmenter(vadSettings);
                using var httpClient = new HttpClient();
                await FoxTransApp.RunBatchTranscriptionPipelineAsync(
                    microphone,
                    segmenter,
                    new OpenAiTranscriber(httpClient, transcriptionSettings),
                    new OpenAiTextTranslator(httpClient, translationSettings),
                    outputs,
                    ui,
                    cancellationToken: shutdown.Token);
            }
            finally
            {
                foreach (VrChatOscOutput output in outputs)
                    await output.DisposeAsync();
            }
            break;
        }
        case PipelineKind.RealtimeTranscriptionTranslation:
        {
            var speech = (VoxtralFoxConfig)config.EffectivePipeline.Speech!;
            string? speechKey = ResolveSecret(
                speech.ApiKey,
                "pipeline.speech.apiKey",
                ui);
            ResolvedVoxtralFoxSettings settings =
                ConfigResolver.ResolveVoxtral(speech, speechKey);
            using var httpClient = new HttpClient();
            VoxtralHealthInfo health = await new VoxtralFoxHealthClient(httpClient)
                .CheckAsync(settings, shutdown.Token);
            ui.Report(AppEvent.VoxtralHealthChecked(health));
            ui.Report(AppEvent.VoxtralConnecting(settings.RealtimeEndpoint));
            using var microphone = new NAudioMicrophoneSource(format);
            await using var transcriber = new VoxtralFoxTranscriber(settings);
            await FoxTransApp.RunRealtimeTranscriptionPreviewAsync(
                microphone,
                transcriber,
                ui,
                shutdown.Token);
            break;
        }
        default:
            throw new ConfigurationException("No executable pipeline was selected.");
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Environment.ExitCode = 0;
}
catch (Exception exception)
{
    if (ui is null)
        Console.Error.WriteLine($"FoxTrans failed: {exception.Message}");
    else
        ui.Report(exception is ConfigurationException
            ? AppEvent.ConfigError(exception.Message)
            : AppEvent.FatalError(exception.Message));
    Environment.ExitCode = 1;
}
finally
{
    ui?.Dispose();
}

static string? ResolveSecret(string? value, string path, IAppReporter reporter)
{
    SecretResolution resolution = ConfigResolver.ResolveSecret(
        value,
        path,
        Environment.GetEnvironmentVariable);
    if (resolution.Issue is not null)
        throw new ConfigurationException($"{resolution.Issue.Path}: {resolution.Issue.Message}");
    if (resolution.Warning is not null)
        reporter.Report(AppEvent.ConfigWarning(resolution.Warning));
    return resolution.Value;
}
