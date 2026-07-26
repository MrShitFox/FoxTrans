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
        var transcriber = new OpenAiTranscriber(client, new(
            new Uri("https://example.test/v1/audio/transcriptions"),
            "secret-key",
            "whisper-1",
            "ru",
            OpenAiTranscriptionRequestFormat.Multipart));

        Assert.Equal("recognized speech", await transcriber.TranscribeAsync(Segment, TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/audio/transcriptions", handler.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-key", handler.AuthorizationParameter);
        Assert.StartsWith("multipart/form-data; boundary=", handler.ContentType);
        Assert.DoesNotContain("boundary=\"", handler.ContentType);
        Assert.Contains("name=model", handler.Body);
        Assert.Contains("whisper-1", handler.Body);
        Assert.Contains("name=language", handler.Body);
        Assert.Contains("\r\nru\r\n", handler.Body);
        Assert.Contains("name=file; filename=audio.wav", handler.Body);
        Assert.Contains("Content-Type: audio/wav", handler.Body);
        byte[] expected = WavPacker.Pack(Segment.Pcm.Span, Segment.Format);
        Assert.True(Contains(handler.BodyBytes, expected));
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(expected, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(expected, 8, 4));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => handler.CapturedContent!.ReadAsByteArrayAsync(
                TestContext.Current.CancellationToken));
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

    [Theory]
    [InlineData("ru")]
    [InlineData(null)]
    public async Task TranscriberSendsJsonBase64WavAndOptionalLanguage(string? language)
    {
        var handler = new RecordingHandler("""{"text":"json speech"}""");
        using var client = new HttpClient(handler);
        var transcriber = new OpenAiTranscriber(client, new(
            ConfigResolver.TranscriptionEndpoint("https://example.test/v1"),
            "secret-key",
            "openai/whisper-large-v3",
            language,
            OpenAiTranscriptionRequestFormat.Json));

        Assert.Equal(
            "json speech",
            await transcriber.TranscribeAsync(
                Segment,
                TestContext.Current.CancellationToken));
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/audio/transcriptions", handler.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("secret-key", handler.AuthorizationParameter);
        Assert.StartsWith("application/json", handler.ContentType);
        using JsonDocument body = JsonDocument.Parse(handler.BodyBytes);
        JsonElement root = body.RootElement;
        Assert.Equal("openai/whisper-large-v3", root.GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("file", out _));
        JsonElement input = root.GetProperty("input_audio");
        Assert.Equal("wav", input.GetProperty("format").GetString());
        byte[] decoded = Convert.FromBase64String(input.GetProperty("data").GetString()!);
        Assert.Equal(WavPacker.Pack(Segment.Pcm.Span, Segment.Format), decoded);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(decoded, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(decoded, 8, 4));
        if (language is null)
            Assert.False(root.TryGetProperty("language", out _));
        else
            Assert.Equal(language, root.GetProperty("language").GetString());
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
    [InlineData(OpenAiTranscriptionRequestFormat.Multipart, "{", "malformed JSON")]
    [InlineData(OpenAiTranscriptionRequestFormat.Multipart, "{}", "unexpected response")]
    [InlineData(OpenAiTranscriptionRequestFormat.Multipart, "{\"text\":\" \"}", "empty result")]
    [InlineData(OpenAiTranscriptionRequestFormat.Json, "{", "malformed JSON")]
    [InlineData(OpenAiTranscriptionRequestFormat.Json, "{}", "unexpected response")]
    [InlineData(OpenAiTranscriptionRequestFormat.Json, "{\"text\":\" \"}", "empty result")]
    public async Task TranscriberRejectsInvalidSuccessfulResponses(
        OpenAiTranscriptionRequestFormat requestFormat,
        string body,
        string expected)
    {
        using var client = new HttpClient(new RecordingHandler(body));
        var provider = new OpenAiTranscriber(client, new(
            new Uri("https://example.test/audio/transcriptions"),
            "secret-key",
            "m",
            null,
            requestFormat));
        OpenAiProviderException exception = await Assert.ThrowsAsync<OpenAiProviderException>(() => provider.TranscribeAsync(Segment, TestContext.Current.CancellationToken));
        Assert.Contains("audio transcription", exception.Message);
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("secret-key", exception.Message);
    }

    [Theory]
    [InlineData(OpenAiTranscriptionRequestFormat.Multipart)]
    [InlineData(OpenAiTranscriptionRequestFormat.Json)]
    public async Task TranscriberBoundsBadRequestDetailAndPropagatesCancellation(
        OpenAiTranscriptionRequestFormat requestFormat)
    {
        using var client = new HttpClient(new RecordingHandler(
            new string('x', 2000),
            HttpStatusCode.BadRequest));
        var provider = new OpenAiTranscriber(client, new(
            new Uri("https://example.test/audio/transcriptions"),
            "secret-key",
            "m",
            null,
            requestFormat));
        OpenAiProviderException exception = await Assert.ThrowsAsync<OpenAiProviderException>(
            () => provider.TranscribeAsync(Segment, TestContext.Current.CancellationToken));
        Assert.Equal("audio transcription", exception.Operation);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.True(exception.Message.Length < 1200);

        using var cancelledClient = new HttpClient(new ThrowingHandler());
        var cancelledProvider = new OpenAiTranscriber(
            cancelledClient,
            new(
                new Uri("https://example.test/audio/transcriptions"),
                "secret-key",
                "m",
                null,
                requestFormat));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledProvider.TranscribeAsync(Segment, cancellation.Token));
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
        public Uri? RequestUri { get; private set; }
        public string ContentType { get; private set; } = "";
        public string Body { get; private set; } = "";
        public byte[] BodyBytes { get; private set; } = [];
        public HttpContent? CapturedContent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method; AuthorizationScheme = request.Headers.Authorization?.Scheme; AuthorizationParameter = request.Headers.Authorization?.Parameter;
            RequestUri = request.RequestUri;
            CapturedContent = request.Content;
            ContentType = request.Content?.Headers.ContentType?.ToString() ?? "";
            BodyBytes = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Body = System.Text.Encoding.Latin1.GetString(BodyBytes);
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

    private static bool Contains(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle) >= 0;
}
