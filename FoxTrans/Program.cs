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
    var secretIssues = new List<ConfigIssue>();
    foreach ((string? value, string path) in SecretValues(config)) { SecretResolution s = ConfigResolver.ResolveSecret(value, path, Environment.GetEnvironmentVariable); if(s.Issue is not null) secretIssues.Add(s.Issue); if(s.Warning is not null)ui.Report(AppEvent.ConfigWarning(s.Warning)); }
    if (!validation.IsValid || secretIssues.Count > 0) throw new ConfigurationException(string.Join(Environment.NewLine, validation.Issues.Concat(secretIssues).Select(i => $"{i.Path}: {i.Message}")));
    if (validation.PipelineKind != PipelineKind.DirectAudioTranslation) { ui.Report(AppEvent.UnsupportedValidPipeline($"The valid {validation.PipelineKind} pipeline is not executable in this beta yet.")); return; }
    var speech = (OpenAiChatAudioConfig)config.EffectivePipeline.Speech!;
    string? key = ConfigResolver.ResolveSecret(speech.ApiKey, "pipeline.speech.apiKey", Environment.GetEnvironmentVariable).Value;
    var vad = ConfigResolver.ResolveVad((WebRtcVadConfig)config.EffectivePipeline.Vad!);
    var translatorSettings = new ResolvedOpenAiAudioSettings(ConfigResolver.ChatEndpoint(speech.BaseUrl!), key, speech.Model!, speech.Prompt!);
    ResolvedOscEndpoint[] outputs = config.EffectiveOutputs.Cast<VrChatOscConfig>().Select(ConfigResolver.ResolveOsc).ToArray();
    var format = new AudioFormat(16000, 16, 1);
    using var microphone = new NAudioMicrophoneSource(format);
    using var segmenter = new WebRtcVadSegmenter(vad);
    using var httpClient = new HttpClient();
    var translator = new OpenAiAudioTranslator(httpClient, translatorSettings);
    await using var osc = new VrChatOscOutput(outputs[0]);
    await FoxTransApp.RunDirectAudioPipelineAsync(microphone, segmenter, translator, [osc], ui, cancellationToken: shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { Environment.ExitCode = 0; }
catch (Exception ex) { if(ui is null) Console.Error.WriteLine($"FoxTrans failed: {ex.Message}"); else ui.Report(ex is ConfigurationException ? AppEvent.ConfigError(ex.Message) : AppEvent.FatalError(ex.Message)); Environment.ExitCode = 1; }
finally { ui?.Dispose(); }

static IEnumerable<(string? Value, string Path)> SecretValues(FoxTransConfig c)
{
    if(c.EffectivePipeline.Speech is OpenAiChatAudioConfig audio) yield return (audio.ApiKey,"pipeline.speech.apiKey");
    if(c.EffectivePipeline.Speech is OpenAiTranscriptionConfig transcription) yield return (transcription.ApiKey,"pipeline.speech.apiKey");
    if(c.EffectivePipeline.Speech is VoxtralFoxConfig voxtral) yield return (voxtral.ApiKey,"pipeline.speech.apiKey");
    if(c.EffectivePipeline.Translation is OpenAiChatConfig translation) yield return (translation.ApiKey,"pipeline.translation.apiKey");
}
