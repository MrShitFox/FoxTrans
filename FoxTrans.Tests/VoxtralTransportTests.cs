using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

public sealed class VoxtralTransportTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Theory]
    [InlineData("secret", "secret")]
    [InlineData(null, null)]
    public async Task ConfiguresExactlyOnceBeforeAudioAndPassesUpgradeKey(
        string? configuredKey,
        string? expectedKey)
    {
        var socket = new FakeSocket();
        var factory = new FakeFactory(socket);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var transcriber = new VoxtralFoxTranscriber(Settings(configuredKey), factory);
        Task<List<StreamingTranscriptionEvent>> run =
            CollectAsync(transcriber, ContinuousFrames(cancellation.Token), cancellation.Token);

        await socket.SentSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(expectedKey, socket.ApiKey);
        Assert.Single(socket.Sent);
        SentMessage firstMessage = socket.Sent.First();
        Assert.Equal(WebSocketMessageType.Text, firstMessage.Type);
        using (JsonDocument configure = JsonDocument.Parse(firstMessage.Bytes))
        {
            JsonElement root = configure.RootElement;
            Assert.Equal("session.configure", root.GetProperty("type").GetString());
            Assert.Equal(240, root.GetProperty("transcription_delay_ms").GetInt32());
            Assert.Equal("pcm_s16le", root.GetProperty("audio").GetProperty("format").GetString());
            Assert.Equal(16000, root.GetProperty("audio").GetProperty("sample_rate").GetInt32());
            Assert.Equal(1, root.GetProperty("audio").GetProperty("channels").GetInt32());
            Assert.Equal("auto", root.GetProperty("language").GetString());
            Assert.False(root.GetProperty("events").GetProperty("token").GetBoolean());
            Assert.True(root.GetProperty("events").GetProperty("partial").GetBoolean());
        }

        Assert.DoesNotContain(socket.Sent, item => item.Type == WebSocketMessageType.Binary);
        socket.EnqueueText(SessionCreated());
        await WaitUntilAsync(
            () => socket.Sent.Any(item => item.Type == WebSocketMessageType.Binary),
            TestContext.Current.CancellationToken);
        Assert.Equal(1, socket.Sent.Count(item => IsTextType(item, "session.configure")));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await WaitUntilAsync(
            () => socket.Sent.Any(item => IsTextType(item, "session.cancel")),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReassemblesPartialsSuppressesDuplicateAndSurfacesWarning()
    {
        var socket = new FakeSocket();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        socket.EnqueueText(SessionCreated());
        string partial = """{"type":"transcript.partial","sequence":7,"text":"  cumulative text  ","audio_end_ms":880}""";
        socket.EnqueueText(partial[..25], end: false);
        socket.EnqueueText(partial[25..]);
        socket.EnqueueText(partial);
        socket.EnqueueText("""{"type":"session.warning","code":"processing_lag","message":"behind","backlog_ms":1200.5,"input_backlog_ms":2,"runtime_backlog_ms":3,"pcm_queue_ms":4,"outbound_messages":5,"outbound_bytes":6}""");
        await using var transcriber = new VoxtralFoxTranscriber(Settings(), new FakeFactory(socket));
        var events = new ConcurrentQueue<StreamingTranscriptionEvent>();
        Task run = CollectIntoAsync(
            transcriber,
            ContinuousFrames(cancellation.Token),
            events.Enqueue,
            cancellation.Token);
        await WaitUntilAsync(
            () => events.OfType<StreamingServerWarning>().Any(),
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        StreamingPartialTranscript emitted = Assert.Single(events.OfType<StreamingPartialTranscript>());
        Assert.Equal(7, emitted.Sequence);
        Assert.Equal("  cumulative text  ", emitted.Text);
        Assert.Equal(880, emitted.AudioEndMs);
        StreamingServerWarning warning = Assert.Single(events.OfType<StreamingServerWarning>());
        Assert.Equal(1200.5, warning.BacklogMs);
        Assert.Equal(6, warning.OutboundBytes);
    }

    [Fact]
    public async Task BackwardsSequenceFails()
    {
        var socket = new FakeSocket();
        socket.EnqueueText(SessionCreated());
        socket.EnqueueText("""{"type":"transcript.partial","sequence":2,"text":"two","audio_end_ms":200}""");
        socket.EnqueueText("""{"type":"transcript.partial","sequence":1,"text":"one","audio_end_ms":100}""");
        await using var transcriber = new VoxtralFoxTranscriber(Settings(), new FakeFactory(socket));
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => CollectAsync(transcriber, FiniteFrames(4, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal("invalid_sequence", exception.Code);
    }

    [Fact]
    public async Task FatalServerErrorPreservesSafeFields()
    {
        var socket = new FakeSocket();
        socket.EnqueueText(SessionCreated());
        socket.EnqueueText("""{"type":"error","code":"invalid_configuration","message":"bad delay","fatal":true,"supported_transcription_delay_ms":[80,240]}""");
        await using var transcriber = new VoxtralFoxTranscriber(Settings(), new FakeFactory(socket));
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => CollectAsync(transcriber, FiniteFrames(4, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal("invalid_configuration", exception.Code);
        Assert.True(exception.Fatal);
        Assert.Equal([80, 240], exception.SupportedTranscriptionDelayMs);
    }

    public static IEnumerable<object[]> InvalidEvents()
    {
        yield return ["{bad", "invalid_json"];
        yield return ["""{"type":"transcript.partial","sequence":"one","text":"x","audio_end_ms":1}""", "invalid_event"];
    }

    [Theory]
    [MemberData(nameof(InvalidEvents))]
    public async Task MalformedEventsFailSafely(string serverEvent, string expectedCode)
    {
        var socket = new FakeSocket();
        socket.EnqueueText(SessionCreated());
        socket.EnqueueText(serverEvent);
        await using var transcriber = new VoxtralFoxTranscriber(Settings(), new FakeFactory(socket));
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => CollectAsync(transcriber, FiniteFrames(4, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal(expectedCode, exception.Code);
    }

    [Fact]
    public async Task UnexpectedBinaryAndCloseFail()
    {
        foreach (bool binary in new[] { true, false })
        {
            var socket = new FakeSocket();
            socket.EnqueueText(SessionCreated());
            if (binary)
                socket.Enqueue([1, 2], WebSocketMessageType.Binary);
            else
                socket.Enqueue([], WebSocketMessageType.Close);
            await using var transcriber = new VoxtralFoxTranscriber(Settings(), new FakeFactory(socket));
            VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
                () => CollectAsync(transcriber, FiniteFrames(4, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
            Assert.Equal(binary ? "unexpected_binary_event" : "unexpected_close", exception.Code);
        }
    }

    [Fact]
    public async Task OversizedEventFails()
    {
        var socket = new FakeSocket();
        socket.EnqueueText(SessionCreated());
        byte[] fragment = new byte[16 * 1024];
        Array.Fill(fragment, (byte)' ');
        for (int index = 0; index < 257; index++)
            socket.Enqueue(fragment, WebSocketMessageType.Text, end: false);
        await using var transcriber = new VoxtralFoxTranscriber(Settings(), new FakeFactory(socket));
        VoxtralFoxException exception = await Assert.ThrowsAsync<VoxtralFoxException>(
            () => CollectAsync(transcriber, FiniteFrames(4, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken));
        Assert.Equal("event_too_large", exception.Code);
    }

    [Fact]
    public async Task OneSessionStreamsSpeechAndSilenceUntilCancellation()
    {
        var socket = new FakeSocket();
        var factory = new FakeFactory(socket);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var transcriber = new VoxtralFoxTranscriber(Settings(), factory);
        Task<List<StreamingTranscriptionEvent>> run =
            CollectAsync(transcriber, SpeechThenSilence(cancellation.Token), cancellation.Token);
        await socket.SentSignal.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        socket.EnqueueText(SessionCreated());
        await WaitUntilAsync(
            () => socket.Sent.Count(item => item.Type == WebSocketMessageType.Binary) >= 3,
            TestContext.Current.CancellationToken);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, socket.ConnectCount);
        Assert.All(
            socket.Sent.Where(item => item.Type == WebSocketMessageType.Binary),
            item => Assert.Equal(2560, item.Bytes.Length));
        Assert.Contains(
            socket.Sent.Where(item => item.Type == WebSocketMessageType.Binary).Skip(1),
            item => item.Bytes.All(value => value == 0));
        Assert.DoesNotContain(socket.Sent, item => IsTextType(item, "input_audio.end"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await WaitUntilAsync(
            () => socket.Sent.Any(item => IsTextType(item, "session.cancel")),
            TestContext.Current.CancellationToken);
        Assert.True(socket.Disposed);
        Assert.Equal(1, factory.CreateCount);
    }

    [Fact]
    public async Task RealtimePipelineTranslatesPartialsAndPublishesOutputs()
    {
        var reporter = new Reporter();
        var transcriber = new FakePipelineTranscriber();
        var output = new RecordingOutput();
        await FoxTransApp.RunRealtimeTranscriptionPipelineAsync(
            new FiniteSource(Format, [new AudioFrame(new byte[640], Format)]),
            transcriber,
            new FixedTranslator(),
            [output],
            new ResolvedRealtimeSettings(0, 0, 1, 1000, 100),
            reporter,
            TestContext.Current.CancellationToken);
        Assert.True(transcriber.Called);
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.LogicalUtteranceStarted);
        Assert.Contains(reporter.Events, item =>
            item.Kind == AppEventKind.RealtimeTranslationAccepted);
        Assert.Contains(output.Updates, item =>
            item.Kind == TranslationUpdateKind.Translation &&
            item.Text == "translated source cumulative");
    }

    [Fact]
    public async Task RealtimePipelineRejectsFormatBeforeReadingAudio()
    {
        var source = new FiniteSource(new AudioFormat(48000, 16, 2), []);
        await Assert.ThrowsAsync<VoxtralFoxException>(
            () => FoxTransApp.RunRealtimeTranscriptionPipelineAsync(
                source,
                new FakePipelineTranscriber(),
                new FixedTranslator(),
                [],
                new ResolvedRealtimeSettings(250, 650, 2, 1000, 600),
                new Reporter(),
                TestContext.Current.CancellationToken));
        Assert.False(source.Read);
    }

    private static ResolvedVoxtralFoxSettings Settings(string? key = null) =>
        ConfigResolver.ResolveVoxtral(new VoxtralFoxConfig("http://localhost:8080", null, 240), key);

    private static string SessionCreated() => """
        {"type":"session.created","session_id":"st_test","protocol_version":1,"model":"voxtral-mini","transcription_delay_ms":240,"audio":{"format":"pcm_s16le","sample_rate":16000,"channels":1}}
        """;

    private static bool IsTextType(SentMessage message, string type)
    {
        if (message.Type != WebSocketMessageType.Text)
            return false;
        using JsonDocument document = JsonDocument.Parse(message.Bytes);
        return document.RootElement.GetProperty("type").GetString() == type;
    }

    private static async Task<List<StreamingTranscriptionEvent>> CollectAsync(
        IStreamingTranscriber transcriber,
        IAsyncEnumerable<AudioFrame> frames,
        CancellationToken cancellationToken)
    {
        var events = new List<StreamingTranscriptionEvent>();
        await foreach (StreamingTranscriptionEvent item in
                           transcriber.TranscribeAsync(frames, cancellationToken)
                               .WithCancellation(cancellationToken))
            events.Add(item);
        return events;
    }

    private static async Task CollectIntoAsync(
        IStreamingTranscriber transcriber,
        IAsyncEnumerable<AudioFrame> frames,
        Action<StreamingTranscriptionEvent> add,
        CancellationToken cancellationToken)
    {
        await foreach (StreamingTranscriptionEvent item in
                           transcriber.TranscribeAsync(frames, cancellationToken)
                               .WithCancellation(cancellationToken))
            add(item);
    }

    private static async IAsyncEnumerable<AudioFrame> FiniteFrames(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new AudioFrame(
                Enumerable.Repeat((byte)(index + 1), 640).ToArray(),
                Format);
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ContinuousFrames(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            yield return new AudioFrame(new byte[640], Format);
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async IAsyncEnumerable<AudioFrame> SpeechThenSilence(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int index = 0; index < 4; index++)
        {
            yield return new AudioFrame(
                Enumerable.Repeat((byte)9, 640).ToArray(),
                Format);
            await Task.Yield();
        }
        while (true)
        {
            yield return new AudioFrame(new byte[640], Format);
            await Task.Delay(5, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 500 && !condition(); attempt++)
            await Task.Delay(10, cancellationToken);
        Assert.True(condition());
    }

    private sealed record SentMessage(byte[] Bytes, WebSocketMessageType Type);
    private sealed record Incoming(
        byte[] Bytes,
        WebSocketMessageType Type,
        bool End,
        WebSocketCloseStatus? CloseStatus = WebSocketCloseStatus.NormalClosure);

    private sealed class FakeFactory(FakeSocket socket) : IVoxtralWebSocketFactory
    {
        public int CreateCount { get; private set; }
        public IVoxtralWebSocket Create()
        {
            CreateCount++;
            return socket;
        }
    }

    private sealed class FakeSocket : IVoxtralWebSocket
    {
        private readonly Channel<Incoming> _incoming = Channel.CreateBounded<Incoming>(
            new BoundedChannelOptions(300)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });
        private int _sentSignal;

        public ConcurrentQueue<SentMessage> Sent { get; } = new();
        public TaskCompletionSource SentSignal { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? ApiKey { get; private set; }
        public int ConnectCount { get; private set; }
        public bool Disposed { get; private set; }
        public WebSocketState State { get; private set; } = WebSocketState.None;

        public Task ConnectAsync(Uri endpoint, string? apiKey, CancellationToken cancellationToken)
        {
            ApiKey = apiKey;
            ConnectCount++;
            State = WebSocketState.Open;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Enqueue(new SentMessage(buffer.ToArray(), messageType));
            if (messageType == WebSocketMessageType.Text)
            {
                using JsonDocument document = JsonDocument.Parse(buffer);
                if (document.RootElement.GetProperty("type").GetString() == "session.cancel")
                    EnqueueText("""{"type":"session.cancelled"}""");
            }
            if (Interlocked.Exchange(ref _sentSignal, 1) == 0)
                SentSignal.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            Incoming incoming = await _incoming.Reader.ReadAsync(cancellationToken);
            incoming.Bytes.CopyTo(buffer);
            if (incoming.Type == WebSocketMessageType.Close)
                State = WebSocketState.CloseReceived;
            return new(
                incoming.Bytes.Length,
                incoming.Type,
                incoming.End,
                incoming.CloseStatus,
                incoming.Type == WebSocketMessageType.Close ? "test close" : null);
        }

        public Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string statusDescription,
            CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            State = WebSocketState.Closed;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public void EnqueueText(string text, bool end = true) =>
            Enqueue(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, end);

        public void Enqueue(
            byte[] bytes,
            WebSocketMessageType type,
            bool end = true) =>
            Assert.True(_incoming.Writer.TryWrite(new Incoming(bytes, type, end)));
    }

    private sealed class Reporter : IAppReporter
    {
        public ConcurrentQueue<AppEvent> Events { get; } = new();
        public void Report(AppEvent appEvent) => Events.Enqueue(appEvent);
    }

    private sealed class FixedTranslator : ITextTranslator
    {
        public Task<string> TranslateAsync(
            string sourceText,
            CancellationToken cancellationToken) =>
            Task.FromResult("translated " + sourceText);
    }

    private sealed class RecordingOutput : IOutputSink
    {
        public ConcurrentQueue<TranslationUpdate> Updates { get; } = new();
        public string Name => "test";
        public Task PublishAsync(
            TranslationUpdate update,
            CancellationToken cancellationToken)
        {
            Updates.Enqueue(update);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePipelineTranscriber : IStreamingTranscriber
    {
        public bool Called { get; private set; }
        public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
            IAsyncEnumerable<AudioFrame> audio,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Called = true;
            yield return new StreamingSessionStarted("st", 1, "model", 240, 1);
            yield return new StreamingPartialTranscript(1, "source cumulative", 80);
            await Task.Yield();
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FiniteSource(
        AudioFormat format,
        IReadOnlyList<AudioFrame> frames) : IAudioSource
    {
        public bool Read { get; private set; }
        public AudioFormat Format => format;
        public async IAsyncEnumerable<AudioFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Read = true;
            foreach (AudioFrame frame in frames)
            {
                yield return frame;
                await Task.Yield();
            }
        }
    }
}
