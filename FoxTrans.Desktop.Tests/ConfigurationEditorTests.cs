using FoxTrans.Desktop.Services;
using FoxTrans.Desktop.ViewModels;
using Xunit;

public sealed class ConfigurationEditorTests
{
    [Theory]
    [InlineData(DesktopPipelineMode.AudioLlm, true, false, false)]
    [InlineData(DesktopPipelineMode.WhisperLlm, true, false, true)]
    [InlineData(DesktopPipelineMode.VoxtralLlm, false, true, true)]
    public async Task ModeSelectionExposesOnlyRelevantConfiguration(
        DesktopPipelineMode mode,
        bool usesVad,
        bool usesRealtime,
        bool usesTranslator)
    {
        await using EditorContext context = EditorContext.Create();

        context.Editor.Mode = mode;

        Assert.Equal(usesVad, context.Editor.UsesVad);
        Assert.Equal(usesRealtime, context.Editor.IsVoxtralLlm);
        Assert.Equal(usesTranslator, context.Editor.UsesTranslator);
        Assert.Equal(
            mode == DesktopPipelineMode.AudioLlm,
            context.Editor.IsAudioLlm);
        Assert.Equal(
            mode == DesktopPipelineMode.WhisperLlm,
            context.Editor.IsWhisperLlm);
    }

    [Theory]
    [InlineData(
        DesktopPipelineMode.AudioLlm,
        PipelineKind.DirectAudioTranslation,
        true,
        false)]
    [InlineData(
        DesktopPipelineMode.WhisperLlm,
        PipelineKind.BatchTranscriptionTranslation,
        true,
        false)]
    [InlineData(
        DesktopPipelineMode.VoxtralLlm,
        PipelineKind.RealtimeTranscriptionTranslation,
        false,
        true)]
    public async Task AllPipelineModesSaveReloadAndResolve(
        DesktopPipelineMode mode,
        PipelineKind expectedKind,
        bool usesVad,
        bool usesRealtime)
    {
        await using EditorContext context = EditorContext.Create();
        SettingsViewModel editor = context.Editor;
        Configure(editor, mode);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.False(editor.FeedbackIsError);
        Assert.Equal("Configuration saved", editor.Feedback);
        Assert.False(editor.IsDirty);
        FoxTransConfig saved = AppConfig.Read(context.ConfigPath);
        ExecutionPlanResolution resolution = ExecutionPlanResolver.Resolve(
            saved,
            context.Devices.GetInputs(),
            _ => "resolved-test-secret");
        Assert.NotNull(resolution.Plan);
        Assert.Equal(expectedKind, resolution.Plan!.PipelineKind);
        Assert.Equal(usesVad, saved.EffectivePipeline.Vad is not null);
        Assert.Equal(
            usesRealtime,
            saved.EffectivePipeline.Realtime is not null);
        Assert.Equal(mode != DesktopPipelineMode.AudioLlm,
            saved.EffectivePipeline.Translation is not null);
        Assert.DoesNotContain(
            "resolved-test-secret",
            File.ReadAllText(context.ConfigPath));
    }

    [Fact]
    public async Task WhisperEditorPreservesEveryVisibleFieldAndMultipleOutputs()
    {
        await using EditorContext context = EditorContext.Create();
        SettingsViewModel editor = context.Editor;
        Configure(editor, DesktopPipelineMode.WhisperLlm);
        editor.Transcription.Language = "ru";
        editor.Transcription.RequestFormat = "json";
        editor.VadPreset = "long-phrases";
        editor.VadStartAfterMs = "410";
        editor.VadStopAfterMs = "1410";
        editor.VadPreRollMs = "810";
        editor.VadMinimumPhraseMs = "1610";
        editor.AddOutputCommand.Execute(null);
        editor.Outputs[0].Address = "127.0.0.1:9001";
        editor.Outputs[0].TypingIndicator = false;
        editor.Outputs[1].Address = "127.0.0.1:9002";
        editor.Outputs[1].MoveUpCommand.Execute(null);

        await editor.SaveCommand.ExecuteAsync(null);

        FoxTransConfig saved = AppConfig.Read(context.ConfigPath);
        var speech =
            Assert.IsType<OpenAiTranscriptionConfig>(
                saved.EffectivePipeline.Speech);
        var vad =
            Assert.IsType<WebRtcVadConfig>(
                saved.EffectivePipeline.Vad);
        Assert.Equal("ru", speech.Language);
        Assert.Equal("json", speech.RequestFormat);
        Assert.Equal("whisper-test-model", speech.Model);
        Assert.Equal("long-phrases", vad.Preset);
        Assert.Equal(410, vad.StartAfterMs);
        Assert.Equal(1410, vad.StopAfterMs);
        Assert.Equal(810, vad.PreRollMs);
        Assert.Equal(1610, vad.MinimumPhraseMs);
        Assert.Equal(2, saved.EffectiveOutputs.Count);
        var first = Assert.IsType<VrChatOscConfig>(
            saved.EffectiveOutputs[0]);
        var second = Assert.IsType<VrChatOscConfig>(
            saved.EffectiveOutputs[1]);
        Assert.Equal("127.0.0.1:9002", first.Address);
        Assert.Equal("127.0.0.1:9001", second.Address);
        Assert.False(second.TypingIndicator);
    }

    [Fact]
    public async Task VoxtralEditorPersistsRealtimeOverridesAndNeverWritesVad()
    {
        await using EditorContext context = EditorContext.Create();
        SettingsViewModel editor = context.Editor;
        Configure(editor, DesktopPipelineMode.VoxtralLlm);
        editor.RealtimePreset = "responsive";
        editor.RealtimeMinimumIntervalMs = "300";
        editor.RealtimeMaximumIntervalMs = "900";
        editor.RealtimeMinimumChangedWords = "2";
        editor.RealtimeNewUtteranceAfterMs = "2800";
        editor.RealtimeMaxSourceCharacters = "850";

        await editor.SaveCommand.ExecuteAsync(null);

        FoxTransConfig saved = AppConfig.Read(context.ConfigPath);
        Assert.Null(saved.EffectivePipeline.Vad);
        var realtime = Assert.IsType<RealtimeConfig>(
            saved.EffectivePipeline.Realtime);
        Assert.Equal("responsive", realtime.Preset);
        Assert.Equal(300, realtime.MinimumIntervalMs);
        Assert.Equal(900, realtime.MaximumIntervalMs);
        Assert.Equal(2, realtime.MinimumChangedWords);
        Assert.Equal(2800, realtime.NewUtteranceAfterMs);
        Assert.Equal(850, realtime.MaxSourceCharacters);
    }

    [Fact]
    public async Task ValidationIsFieldLocalAndCannotOverwriteExistingFile()
    {
        await using EditorContext context = EditorContext.Create();
        string before = File.ReadAllText(context.ConfigPath);
        SettingsViewModel editor = context.Editor;
        editor.AudioLlm.Endpoint = "relative/provider";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(editor.FeedbackIsError);
        Assert.True(editor.AudioLlm.HasEndpointError);
        Assert.Contains(
            "absolute HTTP or HTTPS",
            editor.AudioLlm.EndpointError);
        Assert.Equal(before, File.ReadAllText(context.ConfigPath));
        Assert.False(File.Exists(context.ConfigPath + ".tmp"));
    }

    [Fact]
    public void StoreCreatesOneBackupAndAtomicallyReloadsCanonicalJsonc()
    {
        using EditorContext context = EditorContext.Create();
        string original = File.ReadAllText(context.ConfigPath);
        FoxTransConfig replacement = BatchConfig() with
        {
            Outputs =
            [
                new VrChatOscConfig("127.0.0.1:9011", false),
                new VrChatOscConfig("127.0.0.1:9012", true)
            ]
        };

        DesktopConfigurationSaveResult result =
            context.Services.Configuration.Save(replacement);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Bootstrap?.Plan);
        Assert.Equal(
            PipelineKind.BatchTranscriptionTranslation,
            result.Bootstrap!.Plan!.PipelineKind);
        Assert.Equal(original, File.ReadAllText(context.ConfigPath + ".bak"));
        string canonical = File.ReadAllText(context.ConfigPath);
        Assert.StartsWith(
            "// Generated by FoxTrans Desktop.",
            canonical);
        Assert.Equal(
            AppConfig.Serialize(replacement),
            AppConfig.Serialize(AppConfig.Read(context.ConfigPath)));
        Assert.False(File.Exists(context.ConfigPath + ".tmp"));
        Assert.Single(
            Directory.GetFiles(
                context.Directory,
                "config.jsonc.bak",
                SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void InvalidStoreSaveLeavesOriginalAndBackupUntouched()
    {
        using EditorContext context = EditorContext.Create();
        string original = File.ReadAllText(context.ConfigPath);
        FoxTransConfig invalid = DirectConfig() with
        {
            Pipeline = DirectConfig().EffectivePipeline with
            {
                Speech = new OpenAiChatAudioConfig(
                    "not-absolute",
                    "env:KEY",
                    "audio-model",
                    "prompt")
            }
        };

        DesktopConfigurationSaveResult result =
            context.Services.Configuration.Save(invalid);

        Assert.False(result.IsSuccess);
        Assert.Equal(original, File.ReadAllText(context.ConfigPath));
        Assert.False(File.Exists(context.ConfigPath + ".tmp"));
        Assert.False(File.Exists(context.ConfigPath + ".bak"));
    }

    [Fact]
    public void CredentialModesPreserveUnrevealedInlineValue()
    {
        const string inline = "inline-value-that-must-be-preserved";
        var credential = new CredentialEditorViewModel(inline);

        Assert.True(credential.UsesInlineValue);
        Assert.True(credential.IsMasked);
        Assert.False(credential.IsRevealed);
        Assert.Equal(inline, credential.ToConfiguredValue());

        credential.IsRevealed = true;
        Assert.False(credential.IsMasked);
        credential.Mode = CredentialStorageMode.EnvironmentVariable;
        credential.EnvironmentVariable = "OPENROUTER_API_KEY";
        Assert.Equal(
            "env:OPENROUTER_API_KEY",
            credential.ToConfiguredValue());
    }

    [Fact]
    public async Task AppearancePreferencesStayOutsidePipelineConfig()
    {
        await using EditorContext context = EditorContext.Create();
        string before = File.ReadAllText(context.ConfigPath);
        SettingsViewModel editor = context.Editor;

        editor.Appearance = DesktopAppearance.Light;
        editor.ReducedMotion = true;
        DesktopPreferences updated = editor.ApplyTo(new());

        Assert.Equal(DesktopAppearance.Light, updated.Appearance);
        Assert.True(updated.ReducedMotion);
        Assert.Equal(before, File.ReadAllText(context.ConfigPath));
    }

    private static void Configure(
        SettingsViewModel editor,
        DesktopPipelineMode mode)
    {
        editor.Mode = mode;
        editor.AudioLlm.Endpoint = "https://audio.example.test/v1";
        editor.AudioLlm.Model = "audio-test-model";
        editor.AudioLlm.Prompt = "Translate the supplied audio.";
        editor.AudioLlm.Credential.Mode =
            CredentialStorageMode.EnvironmentVariable;
        editor.AudioLlm.Credential.EnvironmentVariable = "AUDIO_KEY";

        editor.Transcription.Endpoint = "https://stt.example.test/v1";
        editor.Transcription.Model = "whisper-test-model";
        editor.Transcription.RequestFormat = "multipart";
        editor.Transcription.Credential.Mode =
            CredentialStorageMode.EnvironmentVariable;
        editor.Transcription.Credential.EnvironmentVariable = "STT_KEY";

        editor.Voxtral.Endpoint = "http://voxtral.example.test";
        editor.Voxtral.DelayMs = 240;
        editor.Voxtral.Credential.Mode =
            CredentialStorageMode.EnvironmentVariable;
        editor.Voxtral.Credential.EnvironmentVariable = "VOXTRAL_KEY";

        editor.Translator.Endpoint =
            "https://translator.example.test/v1";
        editor.Translator.Model = "translator-test-model";
        editor.Translator.Prompt = "Translate to English.";
        editor.Translator.Credential.Mode =
            CredentialStorageMode.EnvironmentVariable;
        editor.Translator.Credential.EnvironmentVariable =
            "TRANSLATOR_KEY";
        editor.Outputs[0].Address = "127.0.0.1:9000";
    }

    private static FoxTransConfig DirectConfig() => new(
        Audio: new("default"),
        Pipeline: new(
            new WebRtcVadConfig(),
            new OpenAiChatAudioConfig(
                "https://audio.example.test/v1",
                "env:KEY",
                "audio-model",
                "Translate audio.")),
        Outputs: [new VrChatOscConfig("127.0.0.1:9000", true)]);

    private static FoxTransConfig BatchConfig() => new(
        Audio: new("default"),
        Pipeline: new(
            new WebRtcVadConfig(),
            new OpenAiTranscriptionConfig(
                "https://stt.example.test/v1",
                "env:STT_KEY",
                "whisper-model",
                "ru",
                "json"),
            new OpenAiChatConfig(
                "https://translator.example.test/v1",
                "env:TRANSLATOR_KEY",
                "translator-model",
                "Translate.")),
        Outputs: [new VrChatOscConfig("127.0.0.1:9000", true)]);

    private sealed class EditorContext :
        IDisposable,
        IAsyncDisposable
    {
        private int _disposed;

        private EditorContext(
            string directory,
            Devices devices,
            DesktopApplicationServices services,
            SettingsViewModel editor)
        {
            Directory = directory;
            Devices = devices;
            Services = services;
            Editor = editor;
        }

        public string Directory { get; }
        public string ConfigPath => Path.Combine(Directory, "config.jsonc");
        public Devices Devices { get; }
        public DesktopApplicationServices Services { get; }
        public SettingsViewModel Editor { get; }

        public static EditorContext Create()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "foxtrans-settings-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "config.jsonc"),
                AppConfig.Serialize(DirectConfig()));
            var devices = new Devices();
            var preferences = new DesktopPreferencesStore(
                Path.Combine(directory, "preferences.json"));
            DesktopApplicationServices services =
                DesktopApplicationServices.Create(
                    directory,
                    preferences,
                    new FoxTransRuntime(new NoopSessionFactory()),
                    devices,
                    _ => "resolved-test-secret");
            var editor = new SettingsViewModel(
                services,
                () => { },
                (result, _) =>
                {
                    if (result.Bootstrap is { } bootstrap)
                        services.AcceptBootstrap(bootstrap);
                    return Task.CompletedTask;
                });
            return new(directory, devices, services, editor);
        }

        public void Dispose() =>
            DisposeAsync().AsTask().GetAwaiter().GetResult();

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            await Editor.DisposeAsync();
            await Services.DisposeAsync();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    public sealed class Devices : IAudioInputDeviceCatalogue
    {
        public IReadOnlyList<AudioInputDevice> GetInputs() =>
            [new(0, "Test microphone")];
    }

    private sealed class NoopSessionFactory :
        IRuntimePipelineSessionFactory
    {
        public ValueTask<IRuntimePipelineSession> CreateAsync(
            ResolvedExecutionPlan plan,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IRuntimePipelineSession>(
                new NoopSession());
    }

    private sealed class NoopSession : IRuntimePipelineSession
    {
        public Task RunAsync(
            IAppReporter reporter,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
