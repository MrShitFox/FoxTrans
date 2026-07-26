using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

public abstract record StreamingTranscriptionEvent;

public sealed record StreamingSessionStarted(
    string SessionId,
    int ProtocolVersion,
    string Model,
    int TranscriptionDelayMs,
    int ConnectionGeneration) : StreamingTranscriptionEvent;

public sealed record StreamingPartialTranscript(
    long Sequence,
    string Text,
    long AudioEndMs) : StreamingTranscriptionEvent;

public sealed record StreamingFinalTranscript(
    long Sequence,
    string Text,
    long AudioEndMs) : StreamingTranscriptionEvent;

public sealed record StreamingServerWarning(
    string Code,
    string Message,
    double? BacklogMs,
    double? InputBacklogMs,
    double? RuntimeBacklogMs,
    double? PcmQueueMs,
    int? OutboundMessages,
    long? OutboundBytes,
    double? WebSocketWriteAgeMs,
    double? WorkerOutboundWaitMs) : StreamingTranscriptionEvent;

public sealed record StreamingSessionCompleted(
    long Sequence,
    long AudioDurationMs) : StreamingTranscriptionEvent;

public sealed record StreamingSessionCancelled : StreamingTranscriptionEvent;

public interface IStreamingTranscriber : IAsyncDisposable
{
    IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        CancellationToken cancellationToken);
}

public sealed class VoxtralFoxException : Exception
{
    public VoxtralFoxException(
        string code,
        string message,
        bool fatal = true,
        IReadOnlyList<int>? supportedTranscriptionDelayMs = null,
        WebSocketCloseStatus? closeStatus = null,
        Exception? inner = null)
        : base($"VoxtralFox {code}: {message}", inner)
    {
        Code = code;
        Fatal = fatal;
        SupportedTranscriptionDelayMs = supportedTranscriptionDelayMs ?? [];
        CloseStatus = closeStatus;
    }

    public string Code { get; }
    public bool Fatal { get; }
    public IReadOnlyList<int> SupportedTranscriptionDelayMs { get; }
    public WebSocketCloseStatus? CloseStatus { get; }
}

public sealed record VoxtralHealthInfo(
    string Status,
    bool Ready,
    bool Busy,
    string? BusyMode,
    string ServerVersion,
    string VoxtralVersion,
    string Model,
    int SampleRate,
    int Channels,
    string AudioFormat,
    int MaxActiveStreams,
    int DefaultTranscriptionDelayMs,
    IReadOnlyList<int> SupportedTranscriptionDelayMs);

public sealed class VoxtralFoxHealthClient(HttpClient httpClient)
{
    private const int MaxErrorDetailLength = 1000;

    public async Task<VoxtralHealthInfo> CheckAsync(
        ResolvedVoxtralFoxSettings settings,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.HealthEndpoint);
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new VoxtralFoxException("health_request_failed", exception.Message, inner: exception);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                string detail = SafeDetail(body);
                throw new VoxtralFoxException(
                    "health_http_error",
                    $"GET /health returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
            }

            VoxtralHealthInfo health = Parse(body);
            Validate(health, settings.DelayMs);
            return health;
        }
    }

    public static VoxtralHealthInfo Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = RequireObject(document.RootElement, "health response");
            JsonElement capabilities = RequireObject(Property(root, "capabilities"), "capabilities");
            JsonElement delay = RequireObject(
                Property(capabilities, "transcription_delay"),
                "capabilities.transcription_delay");
            JsonElement supported = Property(delay, "supported_ms");
            if (supported.ValueKind != JsonValueKind.Array)
                throw Protocol("capabilities.transcription_delay.supported_ms must be an array.");

            int[] supportedValues = supported.EnumerateArray()
                .Select((value, index) => RequiredIntValue(
                    value,
                    $"capabilities.transcription_delay.supported_ms[{index}]"))
                .ToArray();
            return new(
                RequiredString(root, "status"),
                RequiredBool(root, "ready"),
                RequiredBool(root, "busy"),
                OptionalString(root, "busy_mode"),
                RequiredString(root, "server_version"),
                RequiredString(root, "voxtral_version"),
                RequiredString(root, "model"),
                RequiredInt(capabilities, "sample_rate"),
                RequiredInt(capabilities, "channels"),
                RequiredString(capabilities, "audio_format"),
                RequiredInt(capabilities, "max_active_streams"),
                RequiredInt(delay, "default_ms"),
                supportedValues);
        }
        catch (VoxtralFoxException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new VoxtralFoxException(
                "invalid_health_response",
                "GET /health returned malformed JSON.",
                inner: exception);
        }
    }

    private static void Validate(VoxtralHealthInfo health, int configuredDelayMs)
    {
        if (!string.Equals(health.Status, "ok", StringComparison.Ordinal))
            throw new VoxtralFoxException("server_not_ready", $"Server status is '{health.Status}'.");
        if (!health.Ready)
            throw new VoxtralFoxException("server_not_ready", "The server reports ready=false.");
        if (health.Busy)
            throw new VoxtralFoxException(
                "server_busy",
                $"The server is busy{(health.BusyMode is null ? "." : $" in {health.BusyMode} mode.")}");
        if (health.SampleRate != 16000)
            throw new VoxtralFoxException("incompatible_server", $"Expected sample rate 16000, server reports {health.SampleRate}.");
        if (health.Channels != 1)
            throw new VoxtralFoxException("incompatible_server", $"Expected one channel, server reports {health.Channels}.");
        if (!string.Equals(health.AudioFormat, "pcm_s16le", StringComparison.Ordinal))
            throw new VoxtralFoxException("incompatible_server", $"Expected pcm_s16le, server reports '{health.AudioFormat}'.");
        if (health.MaxActiveStreams != 1)
            throw new VoxtralFoxException("incompatible_server", $"Expected max_active_streams=1, server reports {health.MaxActiveStreams}.");
        if (!health.SupportedTranscriptionDelayMs.Contains(configuredDelayMs))
            throw new VoxtralFoxException(
                "unsupported_transcription_delay",
                $"pipeline.speech.delayMs is {configuredDelayMs}, but the server supports: {string.Join(", ", health.SupportedTranscriptionDelayMs)}.",
                supportedTranscriptionDelayMs: health.SupportedTranscriptionDelayMs);
    }

    private static string SafeDetail(string detail)
    {
        string bounded = detail.Length <= MaxErrorDetailLength
            ? detail
            : detail[..MaxErrorDetailLength] + "…";
        return bounded.Replace('\r', ' ').Replace('\n', ' ');
    }

    private static VoxtralFoxException Protocol(string message) =>
        new("invalid_health_response", message);

    private static JsonElement Property(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement value)
            ? value
            : throw Protocol($"GET /health is missing required field '{name}'.");

    private static JsonElement RequireObject(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Object
            ? value
            : throw Protocol($"{path} must be an object.");

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement value = Property(parent, name);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw Protocol($"GET /health field '{name}' must be a string.");
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        JsonElement value = Property(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString(),
            _ => throw Protocol($"GET /health field '{name}' must be a string or null.")
        };
    }

    private static bool RequiredBool(JsonElement parent, string name)
    {
        JsonElement value = Property(parent, name);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw Protocol($"GET /health field '{name}' must be a boolean.");
    }

    private static int RequiredInt(JsonElement parent, string name) =>
        RequiredIntValue(Property(parent, name), name);

    private static int RequiredIntValue(JsonElement value, string path) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : throw Protocol($"GET /health field '{path}' must be an integer.");
}

public sealed record VoxtralSocketReceiveResult(
    int Count,
    WebSocketMessageType MessageType,
    bool EndOfMessage,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseStatusDescription = null);

public interface IVoxtralWebSocket : IAsyncDisposable
{
    WebSocketState State { get; }
    Task ConnectAsync(Uri endpoint, string? apiKey, CancellationToken cancellationToken);
    ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken);
    ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken);
    Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string statusDescription,
        CancellationToken cancellationToken);
}

public interface IVoxtralWebSocketFactory
{
    IVoxtralWebSocket Create();
}

public sealed class ClientVoxtralWebSocketFactory : IVoxtralWebSocketFactory
{
    public IVoxtralWebSocket Create() => new ClientVoxtralWebSocket();
}

public sealed class ClientVoxtralWebSocket : IVoxtralWebSocket
{
    private readonly ClientWebSocket _socket = new();

    public WebSocketState State => _socket.State;

    public async Task ConnectAsync(Uri endpoint, string? apiKey, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            _socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
        await _socket.ConnectAsync(endpoint, cancellationToken);
    }

    public ValueTask SendAsync(
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken cancellationToken) =>
        _socket.SendAsync(buffer, messageType, endOfMessage, cancellationToken);

    public async ValueTask<VoxtralSocketReceiveResult> ReceiveAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer, cancellationToken);
        return new(
            result.Count,
            result.MessageType,
            result.EndOfMessage,
            _socket.CloseStatus,
            _socket.CloseStatusDescription);
    }

    public Task CloseOutputAsync(
        WebSocketCloseStatus closeStatus,
        string statusDescription,
        CancellationToken cancellationToken) =>
        _socket.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}

public static class PcmChunkAggregator
{
    public const int ChunkBytes = 2560;

    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> AggregateAsync(
        IAsyncEnumerable<AudioFrame> frames,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        byte[] chunk = new byte[ChunkBytes];
        int buffered = 0;
        await foreach (AudioFrame frame in frames.WithCancellation(cancellationToken))
        {
            ReadOnlyMemory<byte> remaining = frame.Pcm;
            while (!remaining.IsEmpty)
            {
                int count = Math.Min(ChunkBytes - buffered, remaining.Length);
                remaining[..count].CopyTo(chunk.AsMemory(buffered));
                buffered += count;
                remaining = remaining[count..];
                if (buffered == ChunkBytes)
                {
                    yield return chunk;
                    chunk = new byte[ChunkBytes];
                    buffered = 0;
                }
            }
        }

        if (buffered != 0)
        {
            if ((buffered & 1) != 0)
                throw new VoxtralFoxException("invalid_audio_stream", "The PCM audio stream ended with an odd byte count.");
            yield return chunk.AsMemory(0, buffered);
        }
    }
}

public sealed class VoxtralFoxTranscriber : IStreamingTranscriber
{
    private const int MaxEventBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan SessionCreatedTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);
    private static readonly AudioFormat RequiredFormat = new(16000, 16, 1);

    private readonly ResolvedVoxtralFoxSettings _settings;
    private readonly IVoxtralWebSocketFactory _socketFactory;
    private readonly int _connectionGeneration;
    private readonly Action<int>? _audioSent;
    private int _started;
    private int _disposed;

    public VoxtralFoxTranscriber(
        ResolvedVoxtralFoxSettings settings,
        IVoxtralWebSocketFactory? socketFactory = null,
        int connectionGeneration = 1,
        Action<int>? audioSent = null)
    {
        _settings = settings;
        _socketFactory = socketFactory ?? new ClientVoxtralWebSocketFactory();
        _connectionGeneration = connectionGeneration;
        _audioSent = audioSent;
    }

    public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("A VoxtralFox transcriber instance owns exactly one session.");

        var events = Channel.CreateBounded<StreamingTranscriptionEvent>(
            new BoundedChannelOptions(32)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task session = RunSessionAsync(audio, events.Writer, sessionCancellation, cancellationToken);
        try
        {
            await foreach (StreamingTranscriptionEvent item in
                events.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }

            await session;
        }
        finally
        {
            sessionCancellation.Cancel();
            try
            {
                await session;
            }
            catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
            {
                // The application cancellation is propagated by the enumerator.
            }
        }
    }

    private async Task RunSessionAsync(
        IAsyncEnumerable<AudioFrame> audio,
        ChannelWriter<StreamingTranscriptionEvent> events,
        CancellationTokenSource linkedCancellation,
        CancellationToken applicationCancellation)
    {
        await using IVoxtralWebSocket socket = _socketFactory.Create();
        bool configured = false;
        bool cancellationRequested = false;
        try
        {
            await socket.ConnectAsync(
                _settings.RealtimeEndpoint,
                _settings.ApiKey,
                linkedCancellation.Token);

            using (var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(linkedCancellation.Token))
            {
                startupTimeout.CancelAfter(SessionCreatedTimeout);
                Task<ParsedServerEvent> createdReceive = ReceiveEventAsync(
                    socket,
                    startupTimeout.Token);
                await SendJsonAsync(socket, ConfigureJson(), linkedCancellation.Token);
                ParsedServerEvent created;
                try
                {
                    created = await createdReceive;
                }
                catch (OperationCanceledException) when (
                    !linkedCancellation.IsCancellationRequested &&
                    startupTimeout.IsCancellationRequested)
                {
                    throw new VoxtralFoxException(
                        "session_created_timeout",
                        "The server did not send session.created within five seconds.");
                }

                StreamingSessionStarted started = ValidateCreated(created);
                configured = true;
                await events.WriteAsync(started, linkedCancellation.Token);
            }

            Task send = SendAudioAsync(socket, audio, linkedCancellation.Token);
            Task receive = ReceiveLoopAsync(socket, events, applicationCancellation, linkedCancellation.Token);
            Task first = await Task.WhenAny(send, receive);
            if (first.IsFaulted || first.IsCanceled)
            {
                linkedCancellation.Cancel();
                await ObserveOtherAsync(ReferenceEquals(first, send) ? receive : send);
                await first;
            }

            await Task.WhenAll(send, receive);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            cancellationRequested = true;
            if (configured)
            {
                await BestEffortCancelAsync(socket);
                await BestEffortDrainCancellationAsync(socket);
            }
            throw;
        }
        catch (Exception exception)
        {
            linkedCancellation.Cancel();
            events.TryComplete(exception);
            return;
        }
        finally
        {
            linkedCancellation.Cancel();
            if (cancellationRequested && socket.State == WebSocketState.CloseReceived)
                await BestEffortAcknowledgeCloseAsync(socket);
            events.TryComplete();
        }
    }

    private static async Task ObserveOtherAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // The first failure is the one propagated to the consumer.
        }
    }

    private async Task SendAudioAsync(
        IVoxtralWebSocket socket,
        IAsyncEnumerable<AudioFrame> audio,
        CancellationToken cancellationToken)
    {
        await foreach (ReadOnlyMemory<byte> chunk in
            PcmChunkAggregator.AggregateAsync(ValidateFrames(audio, cancellationToken), cancellationToken))
        {
            if (chunk.IsEmpty)
                continue;
            await socket.SendAsync(
                chunk,
                WebSocketMessageType.Binary,
                true,
                cancellationToken);
            _audioSent?.Invoke(chunk.Length);
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ValidateFrames(
        IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (AudioFrame frame in audio.WithCancellation(cancellationToken))
        {
            if (frame.Format != RequiredFormat)
                throw new VoxtralFoxException(
                    "unsupported_audio_format",
                    $"VoxtralFox requires mono 16000 Hz signed PCM16LE; received {frame.Format.SampleRate} Hz, {frame.Format.BitsPerSample}-bit, {frame.Format.Channels} channel(s).");
            yield return frame;
        }
    }

    private async Task ReceiveLoopAsync(
        IVoxtralWebSocket socket,
        ChannelWriter<StreamingTranscriptionEvent> events,
        CancellationToken applicationCancellation,
        CancellationToken cancellationToken)
    {
        long? lastTranscriptSequence = null;
        while (true)
        {
            ParsedServerEvent parsed = await ReceiveEventAsync(socket, cancellationToken);
            switch (parsed.Type)
            {
                case "transcript.partial":
                case "transcript.final":
                    long sequence = RequiredLong(parsed.Root, "sequence", parsed.Type);
                    if (lastTranscriptSequence == sequence)
                        continue;
                    if (lastTranscriptSequence is not null && sequence < lastTranscriptSequence)
                        throw new VoxtralFoxException(
                            "invalid_sequence",
                            $"Transcript sequence moved backwards from {lastTranscriptSequence} to {sequence}.");
                    lastTranscriptSequence = sequence;
                    string text = RequiredString(parsed.Root, "text", parsed.Type);
                    long audioEndMs = RequiredLong(parsed.Root, "audio_end_ms", parsed.Type);
                    StreamingTranscriptionEvent transcript = parsed.Type == "transcript.partial"
                        ? new StreamingPartialTranscript(sequence, text, audioEndMs)
                        : new StreamingFinalTranscript(sequence, text, audioEndMs);
                    await events.WriteAsync(transcript, cancellationToken);
                    break;
                case "session.warning":
                    await events.WriteAsync(ParseWarning(parsed.Root), cancellationToken);
                    break;
                case "session.completed":
                    throw new VoxtralFoxException(
                        "unexpected_completion",
                        "The persistent session completed without application cancellation.");
                case "session.cancelled":
                    throw new VoxtralFoxException(
                        "unexpected_cancellation",
                        "The server cancelled the persistent session unexpectedly.");
                case "pong":
                    break;
                case "error":
                    throw ParseError(parsed.Root);
                case "session.created":
                    throw new VoxtralFoxException(
                        "invalid_state",
                        "The server sent session.created more than once.");
                case "__close":
                    if (applicationCancellation.IsCancellationRequested)
                        return;
                    throw new VoxtralFoxException(
                        "unexpected_close",
                        $"The WebSocket closed unexpectedly ({parsed.CloseStatus?.ToString() ?? "no status"}{SafeCloseDescription(parsed.CloseDescription)}).",
                        closeStatus: parsed.CloseStatus);
                default:
                    await events.WriteAsync(
                        new StreamingServerWarning(
                            "unknown_server_event",
                            $"Ignored unknown VoxtralFox event type '{parsed.Type}'.",
                            null, null, null, null, null, null, null, null),
                        cancellationToken);
                    break;
            }
        }
    }

    private StreamingSessionStarted ValidateCreated(ParsedServerEvent created)
    {
        if (created.Type == "error")
            throw ParseError(created.Root);
        if (created.Type != "session.created")
            throw new VoxtralFoxException(
                "invalid_state",
                $"Expected session.created, received '{created.Type}'.");
        int protocol = RequiredInt(created.Root, "protocol_version", created.Type);
        int delay = RequiredInt(created.Root, "transcription_delay_ms", created.Type);
        JsonElement audio = RequiredObject(created.Root, "audio", created.Type);
        string format = RequiredString(audio, "format", "session.created.audio");
        int sampleRate = RequiredInt(audio, "sample_rate", "session.created.audio");
        int channels = RequiredInt(audio, "channels", "session.created.audio");
        if (protocol != 1 || delay != _settings.DelayMs || format != "pcm_s16le" ||
            sampleRate != 16000 || channels != 1)
        {
            throw new VoxtralFoxException(
                "incompatible_session",
                $"session.created returned protocol={protocol}, delay={delay}, audio={format}/{sampleRate}/{channels}.");
        }

        return new(
            RequiredString(created.Root, "session_id", created.Type),
            protocol,
            RequiredString(created.Root, "model", created.Type),
            delay,
            _connectionGeneration);
    }

    private byte[] ConfigureJson() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        type = "session.configure",
        transcription_delay_ms = _settings.DelayMs,
        audio = new
        {
            format = "pcm_s16le",
            sample_rate = 16000,
            channels = 1
        },
        language = "auto",
        events = new
        {
            token = false,
            partial = true
        }
    });

    private static async Task SendJsonAsync(
        IVoxtralWebSocket socket,
        byte[] json,
        CancellationToken cancellationToken) =>
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken);

    private static async Task<ParsedServerEvent> ReceiveEventAsync(
        IVoxtralWebSocket socket,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            VoxtralSocketReceiveResult result =
                await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await BestEffortAcknowledgeCloseAsync(socket);
                return new ParsedServerEvent(
                    "__close",
                    default,
                    result.CloseStatus,
                    result.CloseStatusDescription);
            }
            if (result.MessageType != WebSocketMessageType.Text)
                throw new VoxtralFoxException(
                    "unexpected_binary_event",
                    "The server sent an unexpected binary WebSocket message.");
            if (message.Length + result.Count > MaxEventBytes)
                throw new VoxtralFoxException(
                    "event_too_large",
                    "A server event exceeded the 4 MiB client limit.");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;
            if (message.Length == 0)
                throw new VoxtralFoxException("invalid_event", "The server sent an empty text event.");
            try
            {
                using JsonDocument document = JsonDocument.Parse(
                    message.GetBuffer().AsMemory(0, (int)message.Length));
                JsonElement root = document.RootElement.Clone();
                if (root.ValueKind != JsonValueKind.Object)
                    throw new VoxtralFoxException("invalid_event", "A server event must be a JSON object.");
                return new ParsedServerEvent(
                    RequiredString(root, "type", "server event"),
                    root);
            }
            catch (VoxtralFoxException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new VoxtralFoxException(
                    "invalid_json",
                    "The server sent malformed JSON.",
                    inner: exception);
            }
        }
    }

    private static StreamingServerWarning ParseWarning(JsonElement root) => new(
        RequiredString(root, "code", "session.warning"),
        RequiredString(root, "message", "session.warning"),
        OptionalDouble(root, "backlog_ms", "session.warning"),
        OptionalDouble(root, "input_backlog_ms", "session.warning"),
        OptionalDouble(root, "runtime_backlog_ms", "session.warning"),
        OptionalDouble(root, "pcm_queue_ms", "session.warning"),
        OptionalInt(root, "outbound_messages", "session.warning"),
        OptionalLong(root, "outbound_bytes", "session.warning"),
        OptionalDouble(root, "websocket_write_age_ms", "session.warning"),
        OptionalDouble(root, "worker_outbound_wait_ms", "session.warning"));

    private static VoxtralFoxException ParseError(JsonElement root)
    {
        IReadOnlyList<int> supported = [];
        if (root.TryGetProperty("supported_transcription_delay_ms", out JsonElement delays))
        {
            if (delays.ValueKind != JsonValueKind.Array)
                throw new VoxtralFoxException(
                    "invalid_event",
                    "error.supported_transcription_delay_ms must be an array.");
            supported = delays.EnumerateArray()
                .Select((value, index) =>
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int delay)
                        ? delay
                        : throw new VoxtralFoxException(
                            "invalid_event",
                            $"error.supported_transcription_delay_ms[{index}] must be an integer."))
                .ToArray();
        }

        return new(
            RequiredString(root, "code", "error"),
            RequiredString(root, "message", "error"),
            RequiredBool(root, "fatal", "error"),
            supported);
    }

    private static JsonElement RequiredObject(JsonElement parent, string name, string context)
    {
        JsonElement value = RequiredProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw InvalidField(context, name, "an object");
    }

    private static string RequiredString(JsonElement parent, string name, string context)
    {
        JsonElement value = RequiredProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw InvalidField(context, name, "a string");
    }

    private static bool RequiredBool(JsonElement parent, string name, string context)
    {
        JsonElement value = RequiredProperty(parent, name, context);
        return value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw InvalidField(context, name, "a boolean");
    }

    private static int RequiredInt(JsonElement parent, string name, string context)
    {
        JsonElement value = RequiredProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : throw InvalidField(context, name, "an integer");
    }

    private static long RequiredLong(JsonElement parent, string name, string context)
    {
        JsonElement value = RequiredProperty(parent, name, context);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : throw InvalidField(context, name, "an integer");
    }

    private static double? OptionalDouble(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result)
            ? result
            : throw InvalidField(context, name, "a number");
    }

    private static int? OptionalInt(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result)
            ? result
            : throw InvalidField(context, name, "an integer");
    }

    private static long? OptionalLong(JsonElement parent, string name, string context)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long result)
            ? result
            : throw InvalidField(context, name, "an integer");
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name, string context) =>
        parent.TryGetProperty(name, out JsonElement value)
            ? value
            : throw new VoxtralFoxException(
                "invalid_event",
                $"{context} is missing required field '{name}'.");

    private static VoxtralFoxException InvalidField(string context, string name, string expected) =>
        new("invalid_event", $"{context}.{name} must be {expected}.");

    private static async Task BestEffortCancelAsync(IVoxtralWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var timeout = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            await SendJsonAsync(
                socket,
                Encoding.UTF8.GetBytes("{\"type\":\"session.cancel\"}"),
                timeout.Token);
        }
        catch
        {
            // Cancellation is best effort during shutdown.
        }
    }

    private static async Task BestEffortDrainCancellationAsync(IVoxtralWebSocket socket)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        using var timeout = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                ParsedServerEvent parsed = await ReceiveEventAsync(socket, timeout.Token);
                if (parsed.Type is "session.cancelled" or "__close")
                    return;
                if (parsed.Type == "error")
                    return;
            }
        }
        catch
        {
            // The terminal event or close is optional during best-effort shutdown.
        }
    }

    private static async Task BestEffortAcknowledgeCloseAsync(IVoxtralWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            await socket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure,
                "ack",
                timeout.Token);
        }
        catch
        {
            // The peer or transport may already be gone.
        }
    }

    private static string SafeCloseDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return "";
        string safe = description.Replace('\r', ' ').Replace('\n', ' ');
        return $": {(safe.Length <= 200 ? safe : safe[..200])}";
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private sealed record ParsedServerEvent(
        string Type,
        JsonElement Root,
        WebSocketCloseStatus? CloseStatus = null,
        string? CloseDescription = null);
}
