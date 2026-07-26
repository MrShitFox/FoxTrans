using System.Net;
using System.Text;
using Xunit;

public sealed class Session6DiagnosticsTests
{
    private static readonly AudioInputDevice[] Devices = [new(3, "Test Microphone")];

    [Fact]
    public void FormatsAllPipelinePlansWithoutExposingSecrets()
    {
        foreach (FoxTransConfig config in new[] { Direct(), Batch(), Realtime() })
        {
            ExecutionPlanResolution resolution = ExecutionPlanResolver.Resolve(
                config,
                Devices,
                name => name == "SECRET" ? "super-secret-value" : null);
            Assert.True(resolution.IsValid);
            string text = DiagnosticFormatting.FormatPlan(resolution.Plan!);
            Assert.Contains("Pipeline:", text);
            Assert.Contains("device 3", text);
            Assert.Contains("Secrets: resolved", text);
            Assert.DoesNotContain("super-secret-value", text);
            if (config.EffectivePipeline.Vad is not null)
            {
                Assert.Contains("VAD phrase preset: natural-speech", text);
                Assert.Contains("Speech start: 240 ms", text);
                Assert.Contains("Speech end pause: 1000 ms", text);
                Assert.Contains("Pre-roll: 600 ms", text);
                Assert.Contains("Minimum phrase: 1200 ms", text);
            }
            if (config.EffectivePipeline.Realtime is not null)
            {
                Assert.Contains("Realtime preset: balanced", text);
                Assert.Contains("Translation interval: 350-1000 ms", text);
                Assert.Contains("Changed-word trigger: 3", text);
                Assert.Contains("New utterance pause: 3000 ms", text);
                Assert.Contains("Source window: 1000 text elements", text);
            }
        }
    }

    [Fact]
    public void ResolutionReportsMissingSecretAndInvalidAudioTogether()
    {
        ExecutionPlanResolution resolution = ExecutionPlanResolver.Resolve(
            Realtime() with { Audio = new("missing device") },
            Devices,
            _ => null);
        Assert.False(resolution.IsValid);
        Assert.Contains(resolution.Issues, issue => issue.Path == "audio.device");
        Assert.Contains(resolution.Issues, issue => issue.Path == "pipeline.speech.apiKey");
        Assert.Contains(resolution.Issues, issue => issue.Path == "pipeline.translation.apiKey");
    }

    [Fact]
    public async Task DirectAndBatchChecksDoNotSendHttpRequests()
    {
        foreach (FoxTransConfig config in new[] { Direct(), Batch() })
        {
            var handler = new CountingHandler();
            using var http = new HttpClient(handler);
            ResolvedExecutionPlan plan = ExecutionPlanResolver.Resolve(
                config,
                Devices,
                _ => "resolved").Plan!;
            ReadinessCheckResult result = await ReadinessChecks.RunAsync(
                plan,
                http,
                TestContext.Current.CancellationToken);
            Assert.True(result.IsReady);
            Assert.Equal(0, handler.Count);
            Assert.Contains(result.Successes, line => line.Contains("not probed"));
        }
    }

    [Fact]
    public async Task RealtimeCheckCallsOnlyHealthAndReportsBusyDistinctly()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                HealthJson(busy: true),
                Encoding.UTF8,
                "application/json")
        });
        using var http = new HttpClient(handler);
        ResolvedExecutionPlan plan = ExecutionPlanResolver.Resolve(
            Realtime(),
            Devices,
            _ => "resolved").Plan!;
        ReadinessCheckResult result = await ReadinessChecks.RunAsync(
            plan,
            http,
            TestContext.Current.CancellationToken);
        Assert.False(result.IsReady);
        Assert.Equal(1, handler.Count);
        Assert.Equal("/health", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Null(handler.LastRequest.Headers.Authorization);
        Assert.Contains(result.Failures, line => line.Contains("busy"));
    }

    private static FoxTransConfig Direct() => new(
        Audio: new("default"),
        Pipeline: new(
            new WebRtcVadConfig(),
            new OpenAiChatAudioConfig(
                "https://provider.test/v1",
                "env:SECRET",
                "audio-model",
                "translate")),
        Outputs: [new VrChatOscConfig()]);

    private static FoxTransConfig Batch() => new(
        Audio: new("default"),
        Pipeline: new(
            new WebRtcVadConfig(),
            new OpenAiTranscriptionConfig(
                "https://speech.test/v1",
                "env:SECRET",
                "speech-model"),
            new OpenAiChatConfig(
                "https://translation.test/v1",
                "env:SECRET",
                "chat-model",
                "translate")),
        Outputs: [new VrChatOscConfig()]);

    private static FoxTransConfig Realtime() => new(
        Audio: new("default"),
        Pipeline: new(
            Speech: new VoxtralFoxConfig(
                "http://voxtral.test:8080",
                "env:SECRET",
                240),
            Translation: new OpenAiChatConfig(
                "https://translation.test/v1",
                "env:SECRET",
                "chat-model",
                "translate"),
            Realtime: new RealtimeConfig()),
        Outputs: [new VrChatOscConfig()]);

    private static string HealthJson(bool busy) =>
        $$"""
        {
          "status":"ok","ready":true,"busy":{{busy.ToString().ToLowerInvariant()}},
          "busy_mode":{{(busy ? "\"realtime\"" : "null")}},
          "server_version":"1","voxtral_version":"1","model":"voxtral-mini",
          "capabilities":{
            "sample_rate":16000,"channels":1,"audio_format":"pcm_s16le",
            "max_active_streams":1,
            "transcription_delay":{"default_ms":480,"supported_ms":[80,160,240]}
          }
        }
        """;

    private sealed class CountingHandler(
        Func<HttpRequestMessage, HttpResponseMessage>? response = null)
        : HttpMessageHandler
    {
        public int Count { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            LastRequest = request;
            return Task.FromResult(response?.Invoke(request) ??
                throw new InvalidOperationException("No request was expected."));
        }
    }
}
