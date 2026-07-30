using System.Text.Json.Nodes;
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
    public void NaturalSpeechVadPreservesCurrentValuesAndRoundsUp()
    {
        ResolvedVadSettings d=ConfigResolver.ResolveVad(new WebRtcVadConfig()); Assert.Equal(12,d.MinSpeechFrames); Assert.Equal(50,d.MinSilenceFrames); Assert.Equal(30,d.PreRollFrames); Assert.Equal(1200,d.MinimumPhraseMs);
        Assert.Equal(13,ConfigResolver.ResolveVad(new WebRtcVadConfig(StartAfterMs:241)).MinSpeechFrames);
    }

    [Theory]
    [InlineData("short-phrases", 8, 30, 20, 800, VadOperatingMode.Aggressive)]
    [InlineData("natural-speech", 12, 50, 30, 1200, VadOperatingMode.VeryAggressive)]
    [InlineData("long-phrases", 20, 70, 40, 1600, VadOperatingMode.VeryAggressive)]
    public void VadCatalogResolvesCanonicalPhrasePresets(
        string name, int start, int stop, int preRoll, int minimum, VadOperatingMode mode)
    {
        ResolvedVadSettings resolved = ConfigResolver.ResolveVad(new WebRtcVadConfig(name));
        Assert.Equal((start, stop, preRoll, minimum, mode),
            (resolved.MinSpeechFrames, resolved.MinSilenceFrames, resolved.PreRollFrames,
                resolved.MinimumPhraseMs, resolved.OperatingMode));
    }

    [Fact]
    public void VadCatalogIsAuthoritativeAndAliasesRemainCompatible()
    {
        Assert.Equal(["short-phrases", "natural-speech", "long-phrases"], VadPresets.All.Select(x => x.Name));
        Assert.Equal(3, VadPresets.All.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
        foreach ((string alias, string canonical) in new[] { ("responsive", "short-phrases"), ("balanced", "natural-speech"), ("strict", "long-phrases") })
        {
            Assert.True(ConfigValidator.Validate(AppConfig.Default() with { Pipeline = AppConfig.Default().EffectivePipeline with { Vad = new WebRtcVadConfig(alias) } }).IsValid);
            Assert.Equal(ConfigResolver.ResolveVad(new WebRtcVadConfig(canonical)), ConfigResolver.ResolveVad(new WebRtcVadConfig(alias)));
            Assert.True(VadPresets.TryGetDeprecatedReplacement(alias, out string replacement));
            Assert.Equal(canonical, replacement);
        }
        Assert.False(VadPresets.TryGet("economical", out _));
        Assert.Contains(ConfigValidator.Validate(AppConfig.Default() with { Pipeline = AppConfig.Default().EffectivePipeline with { Vad = new WebRtcVadConfig("economical") } }).Issues, issue => issue.Path == "pipeline.vad.preset");
    }

    [Fact]
    public void VadOverridesOnlyReplaceTheirMatchingFields()
    {
        ResolvedVadSettings resolved = ConfigResolver.ResolveVad(new WebRtcVadConfig("natural-speech", StopAfterMs: 1801));
        Assert.Equal((12, 91, 30, 1200), (resolved.MinSpeechFrames, resolved.MinSilenceFrames, resolved.PreRollFrames, resolved.MinimumPhraseMs));
        ResolvedVadSettings legacy = ConfigResolver.ResolveVad(new WebRtcVadConfig("balanced", PreRollMs: 401));
        Assert.Equal((12, 50, 21, 1200), (legacy.MinSpeechFrames, legacy.MinSilenceFrames, legacy.PreRollFrames, legacy.MinimumPhraseMs));
    }

    [Theory]
    [InlineData(null, OpenAiTranscriptionRequestFormat.Multipart)]
    [InlineData("multipart", OpenAiTranscriptionRequestFormat.Multipart)]
    [InlineData("json", OpenAiTranscriptionRequestFormat.Json)]
    public void TranscriptionRequestFormatsResolveToTypedValues(
        string? configured,
        OpenAiTranscriptionRequestFormat expected)
    {
        OpenAiTranscriptionConfig config = configured is null
            ? new("https://example.test/v1", null, "whisper-1")
            : new("https://example.test/v1", null, "whisper-1", RequestFormat: configured);
        Assert.Equal(
            expected,
            ConfigResolver.ResolveTranscription(config, null).RequestFormat);
    }

    [Fact]
    public void UnknownTranscriptionRequestFormatFailsAtSpecificPath()
    {
        var config = new FoxTransConfig(
            Pipeline: new(
                new WebRtcVadConfig(),
                new OpenAiTranscriptionConfig(
                    "https://example.test/v1",
                    null,
                    "whisper-1",
                    RequestFormat: "automatic"),
                new OpenAiChatConfig("https://example.test/v1", null, "m", "p")),
            Outputs: [new VrChatOscConfig()]);
        ConfigIssue issue = Assert.Single(
            ConfigValidator.Validate(config).Issues,
            item => item.Path == "pipeline.speech.requestFormat");
        Assert.Contains("multipart, json", issue.Message);
        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigResolver.ResolveTranscription(
                (OpenAiTranscriptionConfig)config.EffectivePipeline.Speech!,
                null));
        Assert.Contains("pipeline.speech.requestFormat", exception.Message);
    }

    [Fact]
    public void RequestFormatSerializesOnlyForTranscriptionConfig()
    {
        string batch = AppConfig.Serialize(new FoxTransConfig(
            Pipeline: new(
                new WebRtcVadConfig(),
                new OpenAiTranscriptionConfig(
                    "https://example.test/v1",
                    null,
                    "whisper-1",
                    RequestFormat: "json"),
                new OpenAiChatConfig("https://example.test/v1", null, "m", "p")),
            Outputs: [new VrChatOscConfig()]));
        Assert.Contains("\"requestFormat\": \"json\"", batch);
        Assert.Equal(batch, AppConfig.Serialize(AppConfig.Read(TempFile(batch))));
        Assert.DoesNotContain(
            "requestFormat",
            AppConfig.Serialize(AppConfig.Default()));
        var realtime = new FoxTransConfig(
            Pipeline: new(
                Speech: new VoxtralFoxConfig("http://localhost:8080"),
                Translation: new OpenAiChatConfig(
                    "https://example.test/v1",
                    null,
                    "m",
                    "p"),
                Realtime: new RealtimeConfig()),
            Outputs: [new VrChatOscConfig()]);
        Assert.DoesNotContain("requestFormat", AppConfig.Serialize(realtime));
    }

    [Theory]
    [InlineData("responsive", 250, 700, 2, 2500, 800)]
    [InlineData("balanced", 350, 1000, 3, 3000, 1000)]
    [InlineData("economical", 700, 1800, 5, 4000, 1400)]
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
    [InlineData("minimumIntervalMs")]
    [InlineData("maximumIntervalMs")]
    [InlineData("minimumChangedWords")]
    [InlineData("newUtteranceAfterMs")]
    [InlineData("maxSourceCharacters")]
    public void OneRealtimeOverrideLeavesTheOtherPresetValuesIntact(string field)
    {
        RealtimeConfig config = field switch
        {
            "minimumIntervalMs" => new("responsive", MinimumIntervalMs: 333),
            "maximumIntervalMs" => new("responsive", MaximumIntervalMs: 999),
            "minimumChangedWords" => new("responsive", MinimumChangedWords: 7),
            "newUtteranceAfterMs" => new("responsive", NewUtteranceAfterMs: 5000),
            _ => new("responsive", MaxSourceCharacters: 900)
        };
        ResolvedRealtimeSettings resolved = ConfigResolver.ResolveRealtime(config);
        Assert.Equal(
            field == "minimumIntervalMs" ? 333 : 250,
            resolved.MinimumIntervalMs);
        Assert.Equal(
            field == "maximumIntervalMs" ? 999 : 700,
            resolved.MaximumIntervalMs);
        Assert.Equal(
            field == "minimumChangedWords" ? 7 : 2,
            resolved.MinimumChangedWords);
        Assert.Equal(
            field == "newUtteranceAfterMs" ? 5000 : 2500,
            resolved.NewUtteranceAfterMs);
        Assert.Equal(
            field == "maxSourceCharacters" ? 900 : 800,
            resolved.MaxSourceCharacters);
    }

    [Fact]
    public void CombinedPartialOverridesLeaveRemainingResponsiveDefaultsIntact()
    {
        Assert.Equal(
            new ResolvedRealtimeSettings(250, 700, 9, 5000, 800),
            ConfigResolver.ResolveRealtime(new RealtimeConfig(
                "responsive",
                MinimumChangedWords: 9,
                NewUtteranceAfterMs: 5000)));
    }

    [Fact]
    public void RealtimePresetCatalogIsTheSinglePublicSourceOfTruth()
    {
        Assert.Equal(
            ["responsive", "balanced", "economical"],
            RealtimePresets.All.Select(item => item.Name));
        Assert.Equal(
            RealtimePresets.All.Count,
            RealtimePresets.All.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
        foreach (RealtimePresetDefinition preset in RealtimePresets.All)
        {
            Assert.True(RealtimePresets.TryGet(preset.Name, out RealtimePresetDefinition found));
            Assert.Equal(preset, found);
            Assert.True(ValidateRealtime(new RealtimeConfig(preset.Name)).IsValid);
        }
        Assert.Equal(
            ConfigResolver.ResolveRealtime(new RealtimeConfig("balanced")),
            ConfigResolver.ResolveRealtime(new RealtimeConfig()));
        ConfigValidationResult unknown = ValidateRealtime(new RealtimeConfig("fast"));
        ConfigIssue issue = Assert.Single(unknown.Issues, item =>
            item.Path == "pipeline.realtime.preset");
        Assert.Contains("responsive, balanced, economical", issue.Message);
        Assert.Throws<ConfigurationException>(() =>
            ConfigResolver.ResolveRealtime(new RealtimeConfig("fast")));
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
        SecretResolution literal=ConfigResolver.ResolveSecret("literal","x",_=>null); Assert.Equal("literal",literal.Value); Assert.Null(literal.Issue);
    }

    [Fact]
    public void SchemaIsDeterministicAndDocumentsRealtimePauseSemantics()
    {
        string schema = AppConfig.GenerateSchema();
        Assert.Equal(schema, AppConfig.GenerateSchema());
        Assert.Contains(
            "Selects translation cadence, natural-pause handling, and source-window defaults.",
            schema);
        Assert.Contains(
            "It does not reconnect Voxtral.",
            schema);
        Assert.Contains("Selects phrase segmentation timings and WebRTC speech-detection behavior for batch audio capture.", schema);
        Assert.Contains(
            "Selects the HTTP request encoding used by the transcription endpoint.",
            schema);
        JsonNode generated = JsonNode.Parse(schema)!;
        JsonArray speechVariants = generated["properties"]!["pipeline"]!["properties"]!
            ["speech"]!["anyOf"]!.AsArray();
        JsonNode requestFormat = speechVariants[1]!["properties"]!["requestFormat"]!;
        Assert.Equal("multipart", requestFormat["default"]!.GetValue<string>());
        Assert.Equal(
            ["multipart", "json"],
            requestFormat["enum"]!.AsArray().Select(item => item!.GetValue<string>()));
        Assert.Null(speechVariants[0]!["properties"]!["requestFormat"]);
        Assert.Null(speechVariants[2]!["properties"]!["requestFormat"]);
    }
    [Fact]
    public void CommittedSchemaMatchesClrMetadata() => Assert.Equal(AppConfig.GenerateSchema(), File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "foxtrans.schema.json")));
    [Fact]
    public void ExamplesDeserializeAndValidate()
    {
        foreach (string path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "examples"), "*.jsonc"))
        {
            ConfigLoadResult loaded = AppConfig.LoadExplicit(path);
            Assert.True(ConfigValidator.Validate(loaded.Config!).IsValid, path);
            Assert.Empty(loaded.Warnings);
        }
    }

    [Fact]
    public void AliasLoadProducesOneDeprecationWarningWithoutChangingTheConfig()
    {
        string path = TempFile("""{"pipeline":{"vad":{"type":"webrtc","preset":"balanced"},"speech":{"type":"openai-chat-audio","baseUrl":"https://example.test/v1","model":"m","prompt":"p"}},"outputs":[{"type":"vrchat-osc"}]}""");
        ConfigLoadResult loaded = AppConfig.LoadExplicit(path);
        string warning = Assert.Single(loaded.Warnings);
        Assert.Contains("pipeline.vad.preset", warning);
        Assert.Contains("natural-speech", warning);
        Assert.Equal("balanced", ((WebRtcVadConfig)loaded.Config!.EffectivePipeline.Vad!).Preset);
    }

    [Fact]
    public void DirectAndBatchRejectRealtimeAndVoxtralRejectsVad()
    {
        FoxTransConfig direct = AppConfig.Default() with { Pipeline = AppConfig.Default().EffectivePipeline with { Realtime = new RealtimeConfig() } };
        Assert.Contains(ConfigValidator.Validate(direct).Issues, issue => issue.Path == "pipeline.realtime");
        FoxTransConfig batch = new(Pipeline: new(new WebRtcVadConfig(), new OpenAiTranscriptionConfig("http://localhost", null, "m"), new OpenAiChatConfig("https://example.test", null, "m", "p"), new RealtimeConfig()), Outputs: [new VrChatOscConfig()]);
        Assert.Contains(ConfigValidator.Validate(batch).Issues, issue => issue.Path == "pipeline.realtime");
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
        Assert.Equal(
            OpenAiTranscriptionRequestFormat.Multipart,
            ConfigResolver.ResolveTranscription(speech, null).RequestFormat);
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
            new ResolvedRealtimeSettings(250, 700, 2, 2500, 800),
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
