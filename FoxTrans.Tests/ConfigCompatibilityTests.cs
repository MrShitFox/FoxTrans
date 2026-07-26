using Xunit;

public sealed class ConfigCompatibilityTests
{
    [Fact]
    public void CommentsTrailingCommasAndCamelCaseLoad()
    {
        string path = TempFile("""
        { // comment
          "version": 1,
          "audio": { "device": "default", },
          "pipeline": { "vad": { "type": "webrtc", }, "speech": { "type": "openai-chat-audio", "baseUrl": "https://example.test/v1", "model": "m", "prompt": "p", }, },
          "outputs": [{ "type": "vrchat-osc", }],
        }
        """);
        FoxTransConfig c = AppConfig.Read(path);
        Assert.IsType<OpenAiChatAudioConfig>(c.EffectivePipeline.Speech);
        Assert.True(ConfigValidator.Validate(c).IsValid);
    }

    [Fact]
    public void UnknownPropertyIsRejected() => Assert.Throws<ConfigurationException>(() => AppConfig.Read(TempFile("""{"pipeline":{"unexpected":true}}""")));

    [Fact]
    public void DirectAndFuturePipelinesValidate()
    {
        Assert.Equal(PipelineKind.DirectAudioTranslation, ConfigValidator.Validate(AppConfig.Default()).PipelineKind);
        var batch = new FoxTransConfig(Audio:new(), Pipeline:new(new WebRtcVadConfig(),new OpenAiTranscriptionConfig("http://localhost:8000/v1",null,"whisper-1"),new OpenAiChatConfig("https://example.test/v1",null,"m","p")), Outputs:[new VrChatOscConfig()]);
        var real = new FoxTransConfig(Audio:new(), Pipeline:new(null,new VoxtralFoxConfig("http://localhost:8080",null,240),new OpenAiChatConfig("https://example.test/v1",null,"m","p"),new RealtimeConfig()), Outputs:[new VrChatOscConfig()]);
        Assert.True(ConfigValidator.Validate(batch).IsValid); Assert.True(ConfigValidator.Validate(real).IsValid);
    }

    [Fact]
    public void InvalidCombinationsAreReported()
    {
        FoxTransConfig directTranslation = AppConfig.Default() with { Pipeline = AppConfig.Default().EffectivePipeline with { Translation = new OpenAiChatConfig("https://x.test",null,"m","p") } };
        Assert.Contains(ConfigValidator.Validate(directTranslation).Issues, x => x.Path == "pipeline.translation");
        var voxtralVad = new FoxTransConfig(Pipeline:new(new WebRtcVadConfig(),new VoxtralFoxConfig("http://localhost",null),new OpenAiChatConfig("https://x.test",null,"m","p"),new RealtimeConfig()),Outputs:[new VrChatOscConfig()]);
        Assert.Contains(ConfigValidator.Validate(voxtralVad).Issues,x=>x.Path=="pipeline.vad");
    }

    [Fact]
    public void BalancedVadPreservesCurrentValuesAndRoundsUp()
    {
        ResolvedVadSettings d=ConfigResolver.ResolveVad(new WebRtcVadConfig()); Assert.Equal(12,d.MinSpeechFrames); Assert.Equal(50,d.MinSilenceFrames); Assert.Equal(30,d.PreRollFrames); Assert.Equal(1200,d.MinimumPhraseMs);
        Assert.Equal(13,ConfigResolver.ResolveVad(new WebRtcVadConfig(StartAfterMs:241)).MinSpeechFrames);
    }

    [Theory]
    [InlineData("responsive", 250, 650, 2, 1000, 600)]
    [InlineData("balanced", 350, 900, 3, 1400, 800)]
    [InlineData("economical", 650, 1500, 5, 1800, 1000)]
    public void RealtimePresetsResolveToDocumentedBetaDefaults(
        string preset,
        int minimum,
        int maximum,
        int words,
        int utterance,
        int source)
    {
        Assert.Equal(
            new ResolvedRealtimeSettings(minimum, maximum, words, utterance, source),
            ConfigResolver.ResolveRealtime(new RealtimeConfig(preset)));
    }

    [Fact]
    public void EveryRealtimeOverrideWins()
    {
        Assert.Equal(
            new ResolvedRealtimeSettings(51, 52, 7, 251, 65),
            ConfigResolver.ResolveRealtime(new RealtimeConfig(
                "responsive", 51, 52, 7, 251, 65)));
    }

    [Theory]
    [InlineData("minimumIntervalMs", 49)]
    [InlineData("minimumIntervalMs", 10001)]
    [InlineData("maximumIntervalMs", 30001)]
    [InlineData("minimumChangedWords", 0)]
    [InlineData("minimumChangedWords", 101)]
    [InlineData("newUtteranceAfterMs", 249)]
    [InlineData("newUtteranceAfterMs", 30001)]
    [InlineData("maxSourceCharacters", 63)]
    [InlineData("maxSourceCharacters", 20001)]
    public void RealtimeBoundsProduceConfigurationPathErrors(
        string field,
        int value)
    {
        RealtimeConfig realtime = field switch
        {
            "minimumIntervalMs" => new(MinimumIntervalMs: value),
            "maximumIntervalMs" => new(MaximumIntervalMs: value),
            "minimumChangedWords" => new(MinimumChangedWords: value),
            "newUtteranceAfterMs" => new(NewUtteranceAfterMs: value),
            _ => new(MaxSourceCharacters: value)
        };
        ConfigValidationResult result = ValidateRealtime(realtime);
        Assert.Contains(result.Issues, issue =>
            issue.Path == "pipeline.realtime." + field);
    }

    [Fact]
    public void RealtimeMinimumCannotExceedMaximum()
    {
        ConfigValidationResult result = ValidateRealtime(
            new RealtimeConfig(MinimumIntervalMs: 1000, MaximumIntervalMs: 999));
        Assert.Contains(result.Issues, issue =>
            issue.Path == "pipeline.realtime.maximumIntervalMs");
    }

    [Fact]
    public void SecretsAreResolvedWithoutLeaks()
    {
        SecretResolution ok=ConfigResolver.ResolveSecret("env:KEY","pipeline.speech.apiKey", n=>"secret-value"); Assert.Equal("secret-value",ok.Value);
        SecretResolution missing=ConfigResolver.ResolveSecret("env:MISSING","pipeline.speech.apiKey", _=>null); Assert.NotNull(missing.Issue); Assert.DoesNotContain("secret-value",missing.Issue!.Message);
        Assert.NotNull(ConfigResolver.ResolveSecret("literal","x",_=>null).Warning);
    }

    [Fact]
    public void SchemaIsDeterministic() => Assert.Equal(AppConfig.GenerateSchema(),AppConfig.GenerateSchema());
    [Fact]
    public void CommittedSchemaMatchesClrMetadata() => Assert.Equal(AppConfig.GenerateSchema(), File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "foxtrans.schema.json")));
    [Fact]
    public void ExamplesDeserializeAndValidate()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"), "*.jsonc"))
            Assert.True(ConfigValidator.Validate(AppConfig.Read(path)).IsValid, path);
    }
    [Fact]
    public void WhisperExampleIsExecutableBatchConfiguration()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples", "config.whisper.jsonc");
        FoxTransConfig config = AppConfig.Read(path);
        Assert.Equal(PipelineKind.BatchTranscriptionTranslation, ConfigValidator.Validate(config).PipelineKind);
        var speech = (OpenAiTranscriptionConfig)config.EffectivePipeline.Speech!;
        var translation = (OpenAiChatConfig)config.EffectivePipeline.Translation!;
        Assert.Equal("http://127.0.0.1:8000/v1/audio/transcriptions", ConfigResolver.ResolveTranscription(speech, null).Endpoint.ToString());
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", ConfigResolver.ResolveChat(translation, "key").Endpoint.ToString());
    }

    [Fact]
    public void VoxtralExampleResolvesToExecutableRealtimeSettings()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "examples",
            "config.voxtral.jsonc");
        FoxTransConfig config = AppConfig.Read(path);
        Assert.Equal(
            PipelineKind.RealtimeTranscriptionTranslation,
            ConfigValidator.Validate(config).PipelineKind);
        Assert.Equal(
            new ResolvedRealtimeSettings(350, 900, 3, 1400, 800),
            ConfigResolver.ResolveRealtime(config.EffectivePipeline.Realtime!));
    }

    private static ConfigValidationResult ValidateRealtime(RealtimeConfig realtime) =>
        ConfigValidator.Validate(new FoxTransConfig(
            Pipeline: new(
                null,
                new VoxtralFoxConfig("http://localhost:8080"),
                new OpenAiChatConfig("https://example.test/v1", null, "m", "p"),
                realtime),
            Outputs: [new VrChatOscConfig()]));

    private static string TempFile(string contents) { string path=Path.Combine(Path.GetTempPath(),$"foxtrans-{Guid.NewGuid():N}.jsonc"); File.WriteAllText(path,contents); return path; }
}
