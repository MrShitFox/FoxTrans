using System.Net;
using System.Text.Json;
using Xunit;

public sealed class OpenAiProviderTests
{
    private static readonly AudioSegment Segment = new(new byte[] { 1, 2, 3, 4 }, new AudioFormat(16000, 16, 1));

    [Theory]
    [InlineData("https://example.test/v1", "https://example.test/v1/chat/completions")]
    [InlineData("https://example.test/v1/", "https://example.test/v1/chat/completions")]
    [InlineData("https://example.test/v1/chat/completions", "https://example.test/v1/chat/completions")]
    public void ChatEndpointIsNormalized(string baseUrl, string expected) => Assert.Equal(expected, ConfigResolver.ChatEndpoint(baseUrl).ToString());

    [Theory]
    [InlineData("https://example.test/v1", "https://example.test/v1/audio/transcriptions")]
    [InlineData("https://example.test/v1/", "https://example.test/v1/audio/transcriptions")]
    [InlineData("https://example.test/v1/audio/transcriptions", "https://example.test/v1/audio/transcriptions")]
    public void TranscriptionEndpointIsNormalized(string baseUrl, string expected) => Assert.Equal(expected, ConfigResolver.TranscriptionEndpoint(baseUrl).ToString());

    [Fact]
    public async Task TranscriberSendsWavMultipartAndOptionalLanguage()
    {
        var handler = new RecordingHandler("""{"text":"  recognized speech  "}""");
        using var client = new HttpClient(handler);
        var transcriber = new OpenAiTranscriber(client, new(new Uri("https://example.test/v1/audio/transcriptions"), "secret-key", "whisper-1", "ru"));

        Assert.Equal("recognized speech", await transcriber.TranscribeAsync(Segment, TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-key", handler.AuthorizationParameter);
        Assert.Contains("name=model", handler.Body);
        Assert.Contains("whisper-1", handler.Body);
        Assert.Contains("name=language", handler.Body);
        Assert.Contains("name=file; filename=audio.wav", handler.Body);
        Assert.Contains("Content-Type: audio/wav", handler.Body);
    }

    [Fact]
    public async Task TranscriberOmitsAuthorizationAndLanguageWhenAbsent()
    {
        var handler = new RecordingHandler("""{"text":"ok"}""");
        using var client = new HttpClient(handler);
        var transcriber = new OpenAiTranscriber(client, new(ConfigResolver.TranscriptionEndpoint("https://example.test/v1/"), null, "m", null));
        await transcriber.TranscribeAsync(Segment, TestContext.Current.CancellationToken);
        Assert.Null(handler.AuthorizationScheme);
        Assert.DoesNotContain("name=language", handler.Body);
    }

    [Fact]
    public async Task TextTranslatorSendsCompleteTranscriptAndParsesTextParts()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":[{"type":"text","text":" hello "},{"type":"text","text":"world "}]}}]}""");
        using var client = new HttpClient(handler);
        var translator = new OpenAiTextTranslator(client, new(ConfigResolver.ChatEndpoint("https://example.test/v1"), "secret-key", "chat", "translate faithfully"));
        string source = "Complete transcript; do not alter it.";
        Assert.Equal("hello world", await translator.TranslateAsync(source, TestContext.Current.CancellationToken));
        using JsonDocument body = JsonDocument.Parse(handler.Body);
        Assert.Equal("chat", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("translate faithfully", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Equal(source, body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task AudioTranslatorRegressionSendsPromptAndWav()
    {
        var handler = new RecordingHandler("""{"choices":[{"message":{"content":" translated "}}]}""");
        using var client = new HttpClient(handler);
        var translator = new OpenAiAudioTranslator(client, new(ConfigResolver.ChatEndpoint("https://example.test/v1"), null, "m", "audio prompt"));
        Assert.Equal("translated", await translator.TranslateAsync(Segment, TestContext.Current.CancellationToken));
        Assert.Null(handler.AuthorizationScheme);
        Assert.Contains("audio prompt", handler.Body);
        Assert.Contains("input_audio", handler.Body);
        Assert.Contains("data", handler.Body);
    }

    [Theory]
    [InlineData("{", "malformed JSON")]
    [InlineData("{}", "unexpected response")]
    [InlineData("{\"text\":\" \"}", "empty result")]
    public async Task TranscriberRejectsInvalidSuccessfulResponses(string body, string expected)
    {
        using var client = new HttpClient(new RecordingHandler(body));
        var provider = new OpenAiTranscriber(client, new(new Uri("https://example.test/audio/transcriptions"), "secret-key", "m", null));
        OpenAiProviderException exception = await Assert.ThrowsAsync<OpenAiProviderException>(() => provider.TranscribeAsync(Segment, TestContext.Current.CancellationToken));
        Assert.Contains("audio transcription", exception.Message);
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("secret-key", exception.Message);
    }

    [Fact]
    public async Task TextTranslatorReportsStatusWithBoundedSafeDetail()
    {
        using var client = new HttpClient(new RecordingHandler(new string('x', 2000), HttpStatusCode.BadRequest));
        var provider = new OpenAiTextTranslator(client, new(new Uri("https://example.test/chat/completions"), "secret-key", "m", "p"));
        OpenAiProviderException exception = await Assert.ThrowsAsync<OpenAiProviderException>(() => provider.TranslateAsync("source", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("text translation", exception.Message);
        Assert.True(exception.Message.Length < 1200);
        Assert.DoesNotContain("secret-key", exception.Message);
    }

    [Fact]
    public async Task ConnectionFailureAndCancellationPropagateSafely()
    {
        using var client = new HttpClient(new ThrowingHandler());
        var provider = new OpenAiTextTranslator(client, new(new Uri("https://example.test/chat/completions"), "secret-key", "m", "p"));
        OpenAiProviderException exception = await Assert.ThrowsAsync<OpenAiProviderException>(() => provider.TranslateAsync("source", TestContext.Current.CancellationToken));
        Assert.Contains("text translation", exception.Message);
        Assert.DoesNotContain("secret-key", exception.Message);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranslateAsync("source", cancelled.Token));
    }

    private sealed class RecordingHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? AuthorizationScheme { get; private set; }
        public string? AuthorizationParameter { get; private set; }
        public string Body { get; private set; } = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method; AuthorizationScheme = request.Headers.Authorization?.Scheme; AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("offline"));
    }
}
