using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Xunit;

public sealed class ProductionBoundaryPipelineTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public async Task DirectPipelineUsesRealWavAndAudioProviderOverLoopback()
    {
        var received = new TaskCompletionSource<DirectRequest>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            byte[] body = await ReadBodyAsync(context.Request);
            using JsonDocument json = JsonDocument.Parse(body);
            JsonElement root = json.RootElement;
            JsonElement content =
                root.GetProperty("messages")[0].GetProperty("content");
            string wavBase64 =
                content[1].GetProperty("input_audio").GetProperty("data").GetString()!;
            received.TrySetResult(new(
                context.Request.Url!.AbsolutePath,
                context.Request.Headers["Authorization"] == "Bearer direct-test-key",
                root.GetProperty("model").GetString()!,
                content[0].GetProperty("text").GetString()!,
                Convert.FromBase64String(wavBase64)));
            await RespondAsync(
                context,
                HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"loopback translation"}}]}""");
        });

        using var http = new HttpClient();
        var first = new RecordingOutput("first");
        var second = new RecordingOutput("second");
        var reporter = new Reporter();
        await FoxTransApp.RunDirectAudioPipelineAsync(
            new Source([Frame(7)]),
            new Segmenter(),
            new OpenAiAudioTranslator(
                http,
                new(
                    ConfigResolver.ChatEndpoint(server.BaseUri + "v1"),
                    "direct-test-key",
                    "direct-model",
                    "direct prompt")),
            [first, second],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        DirectRequest request = await received.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.True(request.AuthorizationValid);
        Assert.Equal("direct-model", request.Model);
        Assert.Equal("direct prompt", request.Prompt);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(request.Wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(request.Wav, 8, 4));
        Assert.Equal(7, request.Wav[44]);
        Assert.Equal(["loopback translation"], first.Translations);
        Assert.Equal(["loopback translation"], second.Translations);
        Assert.True(first.Typing.First());
        Assert.False(first.Typing.Last());
        Assert.DoesNotContain(reporter.Events, item =>
            item.Kind == AppEventKind.ApiError);
    }

    [Fact]
    public async Task DirectProviderFailureIsVisibleAndTypingReturnsFalse()
    {
        await using var server = await LoopbackServer.StartAsync(context =>
            RespondAsync(
                context,
                HttpStatusCode.BadGateway,
                """{"error":"controlled failure"}"""));
        using var http = new HttpClient();
        var output = new RecordingOutput("direct failure");
        var reporter = new Reporter();

        await FoxTransApp.RunDirectAudioPipelineAsync(
            new Source([Frame(1)]),
            new Segmenter(),
            new OpenAiAudioTranslator(
                http,
                new(
                    ConfigResolver.ChatEndpoint(server.BaseUri + "v1"),
                    null,
                    "model",
                    "prompt")),
            [output],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(output.Translations);
        Assert.False(output.Typing.Last());
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.ApiError &&
            item.Message!.Contains("502", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassicPipelineUsesRealProvidersAndContinuesAfterFailedPhrase()
    {
        var routes = new ConcurrentQueue<string>();
        var successfulMultipart = new TaskCompletionSource<byte[]>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var translatedSource = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int transcription = 0;
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            string path = context.Request.Url!.AbsolutePath;
            routes.Enqueue(path);
            if (path == "/v1/audio/transcriptions")
            {
                byte[] body = await ReadBodyAsync(context.Request);
                int call = Interlocked.Increment(ref transcription);
                if (call == 1)
                {
                    await RespondAsync(
                        context,
                        HttpStatusCode.InternalServerError,
                        """{"error":"first phrase failed"}""");
                    return;
                }
                Assert.Equal(
                    "Bearer speech-test-key",
                    context.Request.Headers["Authorization"]);
                successfulMultipart.TrySetResult(body);
                await RespondAsync(
                    context,
                    HttpStatusCode.OK,
                    """{"text":"complete second transcript"}""");
                return;
            }

            Assert.Equal("/v1/chat/completions", path);
            Assert.Equal(
                "Bearer translation-test-key",
                context.Request.Headers["Authorization"]);
            byte[] chatBody = await ReadBodyAsync(context.Request);
            using JsonDocument json = JsonDocument.Parse(chatBody);
            JsonElement root = json.RootElement;
            Assert.Equal("translation-model", root.GetProperty("model").GetString());
            Assert.Equal(
                "translation prompt",
                root.GetProperty("messages")[0].GetProperty("content").GetString());
            string source =
                root.GetProperty("messages")[1].GetProperty("content").GetString()!;
            translatedSource.TrySetResult(source);
            await RespondAsync(
                context,
                HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"classic translation"}}]}""");
        });

        using var http = new HttpClient();
        var first = new RecordingOutput("first");
        var second = new RecordingOutput("second");
        var reporter = new Reporter();
        await FoxTransApp.RunBatchTranscriptionPipelineAsync(
            new Source([Frame(1), Frame(2)]),
            new Segmenter(),
            new OpenAiTranscriber(
                http,
                new(
                    ConfigResolver.TranscriptionEndpoint(server.BaseUri + "v1"),
                    "speech-test-key",
                    "whisper-model",
                    "ru")),
            new OpenAiTextTranslator(
                http,
                new(
                    ConfigResolver.ChatEndpoint(server.BaseUri + "v1"),
                    "translation-test-key",
                    "translation-model",
                    "translation prompt")),
            [first, second],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        byte[] multipart = await successfulMultipart.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        string multipartText = Encoding.Latin1.GetString(multipart);
        Assert.Contains("name=model", multipartText);
        Assert.Contains("whisper-model", multipartText);
        Assert.Contains("name=language", multipartText);
        Assert.Contains("name=file; filename=audio.wav", multipartText);
        Assert.Contains("Content-Type: audio/wav", multipartText);
        Assert.Contains("RIFF", multipartText);
        Assert.Contains("WAVE", multipartText);
        Assert.Equal(
            "complete second transcript",
            await translatedSource.Task);
        Assert.Equal(
            [
                "/v1/audio/transcriptions",
                "/v1/audio/transcriptions",
                "/v1/chat/completions"
            ],
            routes);
        Assert.Equal(["classic translation"], first.Translations);
        Assert.Equal(["classic translation"], second.Translations);
        Assert.Equal([true, false, true, false, false], first.Typing);
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.ApiError &&
            item.Message!.Contains("500", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClassicJsonPipelineCrossesRealVadWavAndProviderBoundaries()
    {
        int transcriptionRequests = 0;
        string? translatedSource = null;
        byte[]? receivedWav = null;
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            byte[] body = await ReadBodyAsync(context.Request);
            if (context.Request.Url!.AbsolutePath == "/v1/audio/transcriptions")
            {
                Interlocked.Increment(ref transcriptionRequests);
                Assert.StartsWith(
                    "application/json",
                    context.Request.ContentType,
                    StringComparison.OrdinalIgnoreCase);
                using JsonDocument json = JsonDocument.Parse(body);
                JsonElement root = json.RootElement;
                Assert.Equal("json-whisper", root.GetProperty("model").GetString());
                Assert.Equal("ru", root.GetProperty("language").GetString());
                JsonElement input = root.GetProperty("input_audio");
                Assert.Equal("wav", input.GetProperty("format").GetString());
                receivedWav = Convert.FromBase64String(
                    input.GetProperty("data").GetString()!);
                await RespondAsync(
                    context,
                    HttpStatusCode.OK,
                    """{"text":"полная расшифровка"}""");
                return;
            }

            Assert.Equal("/v1/chat/completions", context.Request.Url.AbsolutePath);
            using JsonDocument chat = JsonDocument.Parse(body);
            translatedSource = chat.RootElement.GetProperty("messages")[1]
                .GetProperty("content").GetString();
            await RespondAsync(
                context,
                HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"complete translation"}}]}""");
        });

        var frames = new List<AudioFrame>();
        frames.AddRange(Enumerable.Range(0, 90).Select(SpeechFrame));
        frames.AddRange(Enumerable.Range(0, 70).Select(_ => SilenceFrame()));
        using var http = new HttpClient();
        using var segmenter = new WebRtcVadSegmenter(
            ConfigResolver.ResolveVad(new WebRtcVadConfig("natural-speech")));
        var output = new RecordingOutput("json output");
        await FoxTransApp.RunBatchTranscriptionPipelineAsync(
            new Source(frames),
            segmenter,
            new OpenAiTranscriber(
                http,
                new(
                    ConfigResolver.TranscriptionEndpoint(server.BaseUri + "v1"),
                    "json-speech-key",
                    "json-whisper",
                    "ru",
                    OpenAiTranscriptionRequestFormat.Json)),
            new OpenAiTextTranslator(
                http,
                new(
                    ConfigResolver.ChatEndpoint(server.BaseUri + "v1"),
                    "json-translation-key",
                    "translation-model",
                    "translation prompt")),
            [output],
            new Reporter(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, transcriptionRequests);
        Assert.NotNull(receivedWav);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(receivedWav!, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(receivedWav!, 8, 4));
        Assert.Equal("полная расшифровка", translatedSource);
        Assert.Equal(["complete translation"], output.Translations);
        Assert.False(output.Typing.Last());
    }

    [Fact]
    public async Task ClassicJsonRealVadContinuesSequentiallyAfterProviderFailure()
    {
        int transcriptionRequests = 0;
        var translatedSources = new ConcurrentQueue<string>();
        await using var server = await LoopbackServer.StartAsync(async context =>
        {
            byte[] body = await ReadBodyAsync(context.Request);
            if (context.Request.Url!.AbsolutePath == "/v1/audio/transcriptions")
            {
                int request = Interlocked.Increment(ref transcriptionRequests);
                await RespondAsync(
                    context,
                    request == 1
                        ? HttpStatusCode.InternalServerError
                        : HttpStatusCode.OK,
                    request == 1
                        ? """{"error":"controlled first phrase failure"}"""
                        : """{"text":"second complete transcript"}""");
                return;
            }

            using JsonDocument chat = JsonDocument.Parse(body);
            translatedSources.Enqueue(chat.RootElement.GetProperty("messages")[1]
                .GetProperty("content").GetString()!);
            await RespondAsync(
                context,
                HttpStatusCode.OK,
                """{"choices":[{"message":{"content":"second translation"}}]}""");
        });

        var frames = new List<AudioFrame>();
        frames.AddRange(Enumerable.Range(0, 90).Select(SpeechFrame));
        frames.AddRange(Enumerable.Range(0, 70).Select(_ => SilenceFrame()));
        frames.AddRange(Enumerable.Range(100, 90).Select(SpeechFrame));
        frames.AddRange(Enumerable.Range(0, 70).Select(_ => SilenceFrame()));
        using var http = new HttpClient();
        using var segmenter = new WebRtcVadSegmenter(
            ConfigResolver.ResolveVad(new WebRtcVadConfig("natural-speech")));
        var output = new RecordingOutput("json recovery output");
        var reporter = new Reporter();
        await FoxTransApp.RunBatchTranscriptionPipelineAsync(
            new Source(frames),
            segmenter,
            new OpenAiTranscriber(
                http,
                new(
                    ConfigResolver.TranscriptionEndpoint(server.BaseUri + "v1"),
                    null,
                    "json-whisper",
                    null,
                    OpenAiTranscriptionRequestFormat.Json)),
            new OpenAiTextTranslator(
                http,
                new(
                    ConfigResolver.ChatEndpoint(server.BaseUri + "v1"),
                    null,
                    "translation-model",
                    "translation prompt")),
            [output],
            reporter,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, transcriptionRequests);
        Assert.Equal(["second complete transcript"], translatedSources);
        Assert.Equal(["second translation"], output.Translations);
        Assert.False(output.Typing.Last());
        Assert.Contains(
            reporter.Events,
            item => item.Kind == AppEventKind.ApiError &&
                    item.Message!.Contains("500", StringComparison.Ordinal));
    }

    private static AudioFrame Frame(byte marker)
    {
        byte[] pcm = new byte[640];
        pcm[0] = marker;
        return new(pcm, Format);
    }

    private static AudioFrame SpeechFrame(int frameIndex)
    {
        byte[] pcm = new byte[640];
        for (int sample = 0; sample < 320; sample++)
        {
            double time = (frameIndex * 320 + sample) / 16000d;
            double wave =
                Math.Sin(2 * Math.PI * 180 * time) +
                0.5 * Math.Sin(2 * Math.PI * 360 * time) +
                0.25 * Math.Sin(2 * Math.PI * 720 * time);
            short value = (short)(wave / 1.75 * 14000);
            pcm[sample * 2] = (byte)value;
            pcm[sample * 2 + 1] = (byte)(value >> 8);
        }
        return new(pcm, Format);
    }

    private static AudioFrame SilenceFrame() => new(new byte[640], Format);

    private static async Task<byte[]> ReadBodyAsync(HttpListenerRequest request)
    {
        using var stream = new MemoryStream();
        await request.InputStream.CopyToAsync(stream);
        return stream.ToArray();
    }

    private static async Task RespondAsync(
        HttpListenerContext context,
        HttpStatusCode status,
        string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private sealed record DirectRequest(
        string Path,
        bool AuthorizationValid,
        string Model,
        string Prompt,
        byte[] Wav);

    private sealed class Source(IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public AudioFormat Format => ProductionBoundaryPipelineTests.Format;

        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (AudioFrame frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }
    }

    private sealed class Segmenter : IAudioSegmenter
    {
        public async IAsyncEnumerable<SegmentationUpdate> SegmentAsync(
            IAsyncEnumerable<AudioFrame> frames,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (AudioFrame frame in
                frames.WithCancellation(cancellationToken))
            {
                yield return new(
                    SegmentationUpdateKind.SegmentCompleted,
                    new AudioSegment(frame.Pcm, frame.Format));
            }
        }
    }

    private sealed class RecordingOutput(string name) : IOutputSink
    {
        public string Name => name;
        public ConcurrentQueue<string> Translations { get; } = new();
        public ConcurrentQueue<bool> Typing { get; } = new();

        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            if (update.Kind == TranslationUpdateKind.Translation)
                Translations.Enqueue(update.Text!);
            else
                Typing.Enqueue(update.IsTyping);
            return Task.CompletedTask;
        }
    }

    private sealed class Reporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }

    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _worker;
        private readonly Func<HttpListenerContext, Task> _handler;

        private LoopbackServer(
            HttpListener listener,
            Uri baseUri,
            Func<HttpListenerContext, Task> handler)
        {
            _listener = listener;
            BaseUri = baseUri;
            _handler = handler;
            _worker = Task.Run(RunAsync);
        }

        public Uri BaseUri { get; }

        public static Task<LoopbackServer> StartAsync(
            Func<HttpListenerContext, Task> handler)
        {
            int port;
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            try
            {
                port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            }
            finally
            {
                reservation.Stop();
            }

            var uri = new Uri($"http://127.0.0.1:{port}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(uri.ToString());
            listener.Start();
            return Task.FromResult(new LoopbackServer(listener, uri, handler));
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel();
            _listener.Stop();
            try
            {
                await _worker;
            }
            catch (HttpListenerException) when (_cancellation.IsCancellationRequested)
            {
            }
            catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
            {
            }
            _listener.Close();
            _cancellation.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (HttpListenerException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    await _handler(context);
                }
                catch
                {
                    if (context.Response.OutputStream.CanWrite)
                        context.Response.Abort();
                    throw;
                }
            }
        }
    }
}
