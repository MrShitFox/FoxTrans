using System.Net;
using System.Text;
using Xunit;

public sealed class VoxtralConfigAndHealthTests
{
    public static IEnumerable<object[]> EndpointCases()
    {
        yield return ["http://host:8080", "http://host:8080/health", "ws://host:8080/v1/realtime/transcription"];
        yield return ["http://host:8080/", "http://host:8080/health", "ws://host:8080/v1/realtime/transcription"];
        yield return ["https://host", "https://host/health", "wss://host/v1/realtime/transcription"];
    }

    [Theory]
    [MemberData(nameof(EndpointCases))]
    public void ResolvesServerBaseEndpoints(string baseUrl, string health, string realtime)
    {
        ResolvedVoxtralFoxSettings resolved =
            ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig(baseUrl, null, 240), null);
        Assert.Equal(health, resolved.HealthEndpoint.ToString());
        Assert.Equal(realtime, resolved.RealtimeEndpoint.ToString());
    }

    [Theory]
    [InlineData("ftp://host")]
    [InlineData("http://host?x=1")]
    [InlineData("http://host#fragment")]
    [InlineData("http://host/custom")]
    [InlineData("not a URL")]
    public void RejectsInvalidServerBase(string value)
    {
        ConfigurationException exception = Assert.Throws<ConfigurationException>(
            () => ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig(value), null));
        Assert.Contains("pipeline.speech.baseUrl", exception.Message);
    }

    [Fact]
    public void AcceptsExactlyDocumentedStaticDelays()
    {
        int[] expected = [80, 160, 240, 320, 400, 480, 560, 640, 720, 800, 880, 960, 1040, 1120, 1200, 2400];
        Assert.Equal(expected, ConfigValidator.VoxtralDelays.Order().ToArray());
        foreach (int delay in expected)
            Assert.True(Validate(delay).IsValid);
        Assert.Contains(Validate(120).Issues, issue => issue.Path == "pipeline.speech.delayMs");
    }

    [Fact]
    public async Task CompatibleHealthResponseSucceedsWithoutAuthorization()
    {
        var handler = new RecordingHandler(_ => Response(HttpStatusCode.OK, HealthJson()));
        using var client = new HttpClient(handler);
        VoxtralHealthInfo result = await new VoxtralFoxHealthClient(client).CheckAsync(
            Settings(apiKey: "sensitive-value"),
            TestContext.Current.CancellationToken);
        Assert.Equal("voxtral-mini", result.Model);
        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public void ResolvedSettingsDiagnosticsRedactApiKey()
    {
        ResolvedVoxtralFoxSettings settings = Settings(apiKey: "sensitive-value");
        Assert.DoesNotContain("sensitive-value", settings.ToString());
        Assert.Contains("<redacted>", settings.ToString());
    }

    public static IEnumerable<object[]> IncompatibleHealthCases()
    {
        yield return ["\"ready\":true", "\"ready\":false", "server_not_ready"];
        yield return ["\"sample_rate\":16000", "\"sample_rate\":48000", "incompatible_server"];
        yield return ["\"channels\":1", "\"channels\":2", "incompatible_server"];
        yield return ["\"audio_format\":\"pcm_s16le\"", "\"audio_format\":\"float32\"", "incompatible_server"];
        yield return ["\"max_active_streams\":1", "\"max_active_streams\":2", "incompatible_server"];
        yield return ["\"supported_ms\":[80,160,240]", "\"supported_ms\":[80,160]", "unsupported_transcription_delay"];
    }

    [Theory]
    [MemberData(nameof(IncompatibleHealthCases))]
    public async Task RejectsIncompatibleHealth(string oldValue, string newValue, string code)
    {
        using var client = new HttpClient(new RecordingHandler(
            _ => Response(HttpStatusCode.OK, HealthJson().Replace(oldValue, newValue, StringComparison.Ordinal))));
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => new VoxtralFoxHealthClient(client).CheckAsync(
                Settings(),
                TestContext.Current.CancellationToken));
        Assert.Equal(code, exception.Code);
    }

    [Fact]
    public async Task BusyReportsMode()
    {
        string body = HealthJson()
            .Replace("\"busy\":false", "\"busy\":true", StringComparison.Ordinal)
            .Replace("\"busy_mode\":null", "\"busy_mode\":\"realtime\"", StringComparison.Ordinal);
        using var client = new HttpClient(new RecordingHandler(_ => Response(HttpStatusCode.OK, body)));
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => new VoxtralFoxHealthClient(client).CheckAsync(
                Settings(),
                TestContext.Current.CancellationToken));
        Assert.Equal("server_busy", exception.Code);
        Assert.Contains("realtime", exception.Message);
    }

    [Fact]
    public async Task MalformedAndBoundedHttpFailuresAreSafe()
    {
        using (var malformed = new HttpClient(new RecordingHandler(
                   _ => Response(HttpStatusCode.OK, "{no"))))
        {
            VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
                () => new VoxtralFoxHealthClient(malformed).CheckAsync(
                    Settings(),
                    TestContext.Current.CancellationToken));
            Assert.Equal("invalid_health_response", exception.Code);
        }

        string huge = new('x', 5000);
        using var failed = new HttpClient(new RecordingHandler(
            _ => Response(HttpStatusCode.ServiceUnavailable, huge)));
        VoxtralFoxException httpException = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => new VoxtralFoxHealthClient(failed).CheckAsync(
                Settings(apiKey: "sensitive-value"),
                TestContext.Current.CancellationToken));
        Assert.True(httpException.Message.Length < 1200);
        Assert.DoesNotContain("sensitive-value", httpException.ToString());
    }

    private static ConfigValidationResult Validate(int delay) =>
        ConfigValidator.Validate(new FoxTransConfig(
            Audio: new(),
            Pipeline: new(
                null,
                new VoxtralFoxConfig("http://localhost:8080", null, delay),
                new OpenAiChatConfig("https://example.test/v1", null, "m", "p"),
                new RealtimeConfig()),
            Outputs: [new VrChatOscConfig()]));

    private static ResolvedVoxtralFoxSettings Settings(string? apiKey = null) =>
        ConfigResolver.ResolveVoxtral(
            new VoxtralFoxConfig("http://localhost:8080", null, 240),
            apiKey);

    internal static string HealthJson() => """
        {
          "status":"ok",
          "ready":true,
          "busy":false,
          "busy_mode":null,
          "server_version":"1.1.0",
          "voxtral_version":"1.1.0",
          "model":"voxtral-mini",
          "capabilities":{
            "sample_rate":16000,
            "channels":1,
            "audio_format":"pcm_s16le",
            "max_active_streams":1,
            "transcription_delay":{"default_ms":480,"supported_ms":[80,160,240]}
          }
        }
        """;

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(response(request));
        }
    }
}
