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
    private static string TempFile(string contents) { string path=Path.Combine(Path.GetTempPath(),$"foxtrans-{Guid.NewGuid():N}.jsonc"); File.WriteAllText(path,contents); return path; }
}
