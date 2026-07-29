internal static class DesktopTestPlans
{
    public static ResolvedExecutionPlan Create(
        PipelineKind kind = PipelineKind.BatchTranscriptionTranslation,
        int outputCount = 1,
        string apiKey = "test-secret-value")
    {
        var audio = new ResolvedAudioInput(
            0,
            "Studio microphone",
            new AudioFormat(16000, 16, 1));
        IReadOnlyList<ResolvedOscEndpoint> outputs = Enumerable
            .Range(0, outputCount)
            .Select(index => new ResolvedOscEndpoint(
                "127.0.0.1",
                9000 + index,
                true))
            .ToArray();

        return kind switch
        {
            PipelineKind.DirectAudioTranslation => new(
                DirectConfig(),
                kind,
                audio,
                outputs,
                Vad: new(12, 50, 30, 1200, VadOperatingMode.VeryAggressive),
                Direct: new(
                    new("https://user:password@example.test/v1/chat/completions?token=secret"),
                    apiKey,
                    "audio-model",
                    "full direct prompt")),
            PipelineKind.BatchTranscriptionTranslation => new(
                BatchConfig(),
                kind,
                audio,
                outputs,
                Vad: new(12, 50, 30, 1200, VadOperatingMode.VeryAggressive),
                Transcription: new(
                    new("https://example.test/v1/audio/transcriptions"),
                    apiKey,
                    "stt-model",
                    "ru",
                    OpenAiTranscriptionRequestFormat.Json),
                Translation: new(
                    new("https://example.test/v1/chat/completions"),
                    apiKey,
                    "translation-model",
                    "full translation prompt")),
            _ => new(
                RealtimeConfig(),
                kind,
                audio,
                outputs,
                Translation: new(
                    new("https://example.test/v1/chat/completions"),
                    apiKey,
                    "translation-model",
                    "full translation prompt"),
                Voxtral: new(
                    new("http://example.test/health"),
                    new("ws://example.test/v1/realtime/transcription"),
                    apiKey,
                    240),
                Realtime: new(350, 1000, 3, 3000, 1000))
        };
    }

    private static FoxTransConfig DirectConfig() => new(
        Audio: new(),
        Pipeline: new(
            new WebRtcVadConfig(),
            new OpenAiChatAudioConfig(
                "https://example.test/v1",
                "env:KEY",
                "audio-model",
                "configured")),
        Outputs: [new VrChatOscConfig()]);

    private static FoxTransConfig BatchConfig() => new(
        Audio: new(),
        Pipeline: new(
            new WebRtcVadConfig(),
            new OpenAiTranscriptionConfig(
                "https://example.test/v1",
                "env:KEY",
                "stt-model",
                "ru",
                "json"),
            new OpenAiChatConfig(
                "https://example.test/v1",
                "env:KEY",
                "translation-model",
                "configured")),
        Outputs: [new VrChatOscConfig()]);

    private static FoxTransConfig RealtimeConfig() => new(
        Audio: new(),
        Pipeline: new(
            Speech: new VoxtralFoxConfig(
                "http://example.test",
                "env:KEY",
                240),
            Translation: new OpenAiChatConfig(
                "https://example.test/v1",
                "env:KEY",
                "translation-model",
                "configured"),
            Realtime: new RealtimeConfig()),
        Outputs: [new VrChatOscConfig()]);
}
