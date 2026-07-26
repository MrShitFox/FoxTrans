using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

public sealed record VoxtralConnectionLost(string Code, string Message)
    : StreamingTranscriptionEvent;
public sealed record VoxtralReconnectScheduled(int Attempt, TimeSpan Delay)
    : StreamingTranscriptionEvent;
public sealed record VoxtralReconnectAttempt(int Attempt)
    : StreamingTranscriptionEvent;
public sealed record VoxtralReconnected(int ConnectionGeneration)
    : StreamingTranscriptionEvent;
public sealed record RealtimeAudioGapStarted : StreamingTranscriptionEvent;
public sealed record RealtimeAudioGapCompleted(long LostBytes, TimeSpan ApproximateDuration)
    : StreamingTranscriptionEvent;

public static class VoxtralRetryPolicy
{
    private static readonly IReadOnlySet<string> RetryableCodes = new HashSet<string>(
        [
            "health_request_failed",
            "server_not_ready",
            "server_busy",
            "shutting_down",
            "backend_error",
            "internal_error",
            "idle_timeout",
            "realtime_capacity_exceeded",
            "unexpected_close"
        ],
        StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NonRetryableCodes = new HashSet<string>(
        [
            "unauthorized",
            "invalid_configuration",
            "unsupported_audio_format",
            "invalid_audio_frame",
            "invalid_audio_stream",
            "invalid_sequence",
            "invalid_json",
            "invalid_event",
            "event_too_large",
            "incompatible_session",
            "incompatible_server",
            "unsupported_transcription_delay",
            "unexpected_binary_event",
            "invalid_state"
        ],
        StringComparer.Ordinal);

    public static bool IsRetryable(Exception exception) => exception switch
    {
        OperationCanceledException => false,
        VoxtralFoxException voxtral when NonRetryableCodes.Contains(voxtral.Code) => false,
        VoxtralFoxException
        {
            Code: "unexpected_close",
            CloseStatus: WebSocketCloseStatus.PolicyViolation
        } => false,
        VoxtralFoxException voxtral => RetryableCodes.Contains(voxtral.Code),
        WebSocketException webSocket
            when webSocket.InnerException is HttpRequestException
            {
                StatusCode: System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden
                    or System.Net.HttpStatusCode.BadRequest
            } => false,
        WebSocketException => true,
        HttpRequestException => true,
        IOException => true,
        _ => false
    };

    public static TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 1)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        int seconds = attempt switch
        {
            1 => 1,
            2 => 2,
            3 => 4,
            4 => 8,
            _ => 10
        };
        return TimeSpan.FromSeconds(seconds);
    }

    public static string SafeCode(Exception exception) =>
        exception is VoxtralFoxException voxtral ? voxtral.Code : "transport_failure";

    public static string SafeMessage(Exception exception)
    {
        string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 300 ? message : message[..300] + "…";
    }
}

public sealed class RealtimeAudioQueueOverflowException(int capacity)
    : Exception($"The active Voxtral connection audio queue reached its capacity of {capacity} frames.");
public sealed class RealtimeAudioSourceEndedException()
    : Exception("The microphone audio stream ended unexpectedly.");

public sealed class RealtimeAudioPump
{
    public const int ActiveQueueCapacity = 250;

    private readonly IAsyncEnumerable<AudioFrame> _source;
    private readonly AudioFormat _format;
    private readonly object _gate = new();
    private Channel<AudioFrame>? _active;
    private long _attemptRoutedBytes;
    private long _attemptSentBytes;
    private long _gapBytes;
    private bool _gapStarted;

    public RealtimeAudioPump(IAsyncEnumerable<AudioFrame> source, AudioFormat format)
    {
        _source = source;
        _format = format;
    }

    public event Action? GapStarted;

    public ChannelReader<AudioFrame> BeginAttempt()
    {
        lock (_gate)
        {
            if (_active is not null)
                throw new InvalidOperationException("Only one Voxtral audio connection queue may be active.");
            _attemptRoutedBytes = 0;
            _attemptSentBytes = 0;
            _active = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(ActiveQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            return _active.Reader;
        }
    }

    public void ReportSent(int byteCount)
    {
        lock (_gate)
            _attemptSentBytes += byteCount;
    }

    public void EndFailedAttempt(Exception failure)
    {
        lock (_gate)
        {
            if (_active is null)
                return;
            _gapBytes += Math.Max(0, _attemptRoutedBytes - _attemptSentBytes);
            StartGapLocked();
            _active.Writer.TryComplete(failure);
            _active = null;
        }
    }

    public RealtimeAudioGapCompleted? CompleteGap()
    {
        lock (_gate)
        {
            if (!_gapStarted)
                return null;
            var completed = new RealtimeAudioGapCompleted(
                _gapBytes,
                TimeSpan.FromSeconds((double)_gapBytes / _format.BytesPerSecond));
            _gapBytes = 0;
            _gapStarted = false;
            return completed;
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _active?.Writer.TryComplete();
            _active = null;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (AudioFrame frame in _source.WithCancellation(cancellationToken))
        {
            Channel<AudioFrame>? active;
            lock (_gate)
            {
                active = _active;
                if (active is null)
                {
                    _gapBytes += frame.Pcm.Length;
                    StartGapLocked();
                    continue;
                }
                _attemptRoutedBytes += frame.Pcm.Length;
                if (active.Writer.TryWrite(frame))
                    continue;
                var overflow = new RealtimeAudioQueueOverflowException(ActiveQueueCapacity);
                active.Writer.TryComplete(overflow);
                _active = null;
                throw overflow;
            }
        }
        Complete();
    }

    private void StartGapLocked()
    {
        if (_gapStarted)
            return;
        _gapStarted = true;
        GapStarted?.Invoke();
    }
}

public sealed record VoxtralSupervisorTiming(
    Func<TimeSpan, CancellationToken, Task> Delay)
{
    public static VoxtralSupervisorTiming System { get; } = new(Task.Delay);
}

public sealed class VoxtralConnectionSupervisor : IStreamingTranscriber
{
    private readonly Func<int, Action<int>, IStreamingTranscriber> _transcriberFactory;
    private readonly Func<CancellationToken, Task> _healthCheck;
    private readonly VoxtralSupervisorTiming _timing;
    private readonly AudioFormat _format;
    private int _started;

    public VoxtralConnectionSupervisor(
        AudioFormat format,
        Func<int, Action<int>, IStreamingTranscriber> transcriberFactory,
        Func<CancellationToken, Task> healthCheck,
        VoxtralSupervisorTiming? timing = null)
    {
        _format = format;
        _transcriberFactory = transcriberFactory;
        _healthCheck = healthCheck;
        _timing = timing ?? VoxtralSupervisorTiming.System;
    }

    public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The Voxtral connection supervisor can only be started once.");

        using var pumpCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = new RealtimeAudioPump(audio, _format);
        var sideEvents = Channel.CreateBounded<StreamingTranscriptionEvent>(
            new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        pump.GapStarted += () => sideEvents.Writer.TryWrite(new RealtimeAudioGapStarted());
        ChannelReader<AudioFrame>? preparedAttempt = pump.BeginAttempt();
        Task pumpTask = pump.RunAsync(pumpCancellation.Token);
        int generation = 1;
        int retryAttempt = 0;
        bool establishedOnce = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ChannelReader<AudioFrame> attemptAudio =
                    preparedAttempt ?? pump.BeginAttempt();
                preparedAttempt = null;
                using var attemptCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await using IStreamingTranscriber transcriber =
                    _transcriberFactory(generation, pump.ReportSent);
                Exception? failure = null;
                await using IAsyncEnumerator<StreamingTranscriptionEvent> events = transcriber
                    .TranscribeAsync(
                        ReadFrames(attemptAudio, attemptCancellation.Token),
                        attemptCancellation.Token)
                    .GetAsyncEnumerator(attemptCancellation.Token);
                while (failure is null)
                {
                    bool hasNext;
                    StreamingTranscriptionEvent? item = null;
                    try
                    {
                        Task<bool> moveNext = events.MoveNextAsync().AsTask();
                        Task first = await Task.WhenAny(moveNext, pumpTask);
                        if (ReferenceEquals(first, pumpTask))
                        {
                            attemptCancellation.Cancel();
                            try
                            {
                                await moveNext;
                            }
                            catch (OperationCanceledException)
                            {
                            }
                            await pumpTask;
                            throw new RealtimeAudioSourceEndedException();
                        }
                        hasNext = await moveNext;
                        if (hasNext)
                            item = events.Current;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                        pump.EndFailedAttempt(exception);
                        break;
                    }

                    if (!hasNext)
                    {
                        failure = new VoxtralFoxException(
                            "unexpected_close",
                            "The Voxtral session ended without application cancellation.");
                        pump.EndFailedAttempt(failure);
                        break;
                    }

                    while (sideEvents.Reader.TryRead(out StreamingTranscriptionEvent? side))
                        yield return side;
                    if (item is StreamingSessionStarted)
                    {
                        if (establishedOnce)
                            yield return new VoxtralReconnected(generation);
                        establishedOnce = true;
                        retryAttempt = 0;
                        RealtimeAudioGapCompleted? gap = pump.CompleteGap();
                        if (gap is not null)
                            yield return gap;
                    }
                    yield return item!;
                }

                while (sideEvents.Reader.TryRead(out StreamingTranscriptionEvent? side))
                    yield return side;
                if (!establishedOnce || failure is null || !VoxtralRetryPolicy.IsRetryable(failure))
                    throw failure ?? new VoxtralFoxException("unexpected_close", "The Voxtral session ended.");

                yield return new VoxtralConnectionLost(
                    VoxtralRetryPolicy.SafeCode(failure),
                    VoxtralRetryPolicy.SafeMessage(failure));

                while (true)
                {
                    retryAttempt++;
                    TimeSpan delay = VoxtralRetryPolicy.DelayForAttempt(retryAttempt);
                    yield return new VoxtralReconnectScheduled(retryAttempt, delay);
                    await _timing.Delay(delay, cancellationToken);
                    yield return new VoxtralReconnectAttempt(retryAttempt);
                    try
                    {
                        await _healthCheck(cancellationToken);
                        generation++;
                        break;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception healthFailure) when (VoxtralRetryPolicy.IsRetryable(healthFailure))
                    {
                        failure = healthFailure;
                    }
                }
            }
        }
        finally
        {
            pumpCancellation.Cancel();
            pump.Complete();
            try
            {
                await pumpTask;
            }
            catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested)
            {
            }
        }
    }

    private static async IAsyncEnumerable<AudioFrame> ReadFrames(
        ChannelReader<AudioFrame> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (AudioFrame frame in reader.ReadAllAsync(cancellationToken))
            yield return frame;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
