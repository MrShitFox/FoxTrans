using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Globalization;
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
public sealed record RealtimeAudioRouteActivated(
    int ConnectionGeneration,
    int NormalizedFrameBytes,
    int QueueCapacityFrames,
    int QueueCapacityBytes,
    TimeSpan QueueCapacityDuration,
    TimeSpan SessionCreatedWait) : StreamingTranscriptionEvent;
public sealed record RealtimeAudioPumpSnapshot(
    long RoutedBytes,
    long SentBytes,
    long GapBytes,
    long NormalizedFramesRouted,
    int MaximumObservedQueueFrames,
    TimeSpan MaximumObservedQueueDuration);

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

public sealed class RealtimeAudioQueueOverflowException(
    int connectionGeneration,
    int bufferedFrames,
    int bufferedPcmBytes,
    TimeSpan approximateBufferedDuration)
    : Exception(
        "The active Voxtral audio route could not keep up. " +
        $"Buffered audio reached {approximateBufferedDuration.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} seconds " +
        $"({bufferedPcmBytes} PCM bytes in {bufferedFrames} normalized frames) " +
        $"on connection generation {connectionGeneration}.")
{
    public int ConnectionGeneration { get; } = connectionGeneration;
    public int BufferedFrames { get; } = bufferedFrames;
    public int BufferedPcmBytes { get; } = bufferedPcmBytes;
    public TimeSpan ApproximateBufferedDuration { get; } = approximateBufferedDuration;
}
public sealed class RealtimeAudioSourceEndedException()
    : Exception("The microphone audio stream ended unexpectedly.");

public sealed record RealtimeAudioPumpTiming(
    Func<TimeSpan, CancellationToken, Task> Delay)
{
    public static RealtimeAudioPumpTiming System { get; } = new(Task.Delay);
}

public sealed class PreparedRealtimeAudioAttempt
{
    internal PreparedRealtimeAudioAttempt(int connectionGeneration, Channel<AudioFrame> channel)
    {
        ConnectionGeneration = connectionGeneration;
        Channel = channel;
    }

    internal Channel<AudioFrame> Channel { get; }
    internal long RoutedBytes;
    internal long SentBytes;
    internal bool IsActive;
    internal bool IsEnded;

    public int ConnectionGeneration { get; }
    public ChannelReader<AudioFrame> Reader => Channel.Reader;
}

public sealed class RealtimeAudioPump
{
    public static readonly AudioFormat RequiredFormat = new(16000, 16, 1);
    public const int NormalizedFrameDurationMilliseconds = 20;
    public const int ActiveQueueDurationMilliseconds = 5000;
    public const int NormalizedFrameByteCount =
        16000 * 1 * (16 / 8) * NormalizedFrameDurationMilliseconds / 1000;
    public const int ActiveQueueCapacity =
        ActiveQueueDurationMilliseconds / NormalizedFrameDurationMilliseconds;
    public const int ActiveQueueByteCapacity =
        ActiveQueueCapacity * NormalizedFrameByteCount;
    public static readonly TimeSpan ActiveQueueDuration =
        TimeSpan.FromMilliseconds(ActiveQueueDurationMilliseconds);
    public static readonly TimeSpan ActiveWriteTimeout = TimeSpan.FromMilliseconds(100);

    private readonly IAsyncEnumerable<AudioFrame> _source;
    private readonly AudioFormat _format;
    private readonly RealtimeAudioPumpTiming _timing;
    private readonly PcmFrameNormalizer _normalizer;
    private readonly object _gate = new();
    private PreparedRealtimeAudioAttempt? _prepared;
    private PreparedRealtimeAudioAttempt? _active;
    private PreparedRealtimeAudioAttempt? _carryDestination;
    private long _gapBytes;
    private long _totalRoutedBytes;
    private long _totalSentBytes;
    private long _totalGapBytes;
    private long _normalizedFramesRouted;
    private int _maximumObservedQueueFrames;
    private bool _gapStarted;

    public RealtimeAudioPump(
        IAsyncEnumerable<AudioFrame> source,
        AudioFormat format,
        RealtimeAudioPumpTiming? timing = null)
    {
        if (format != RequiredFormat)
        {
            throw new ArgumentException(
                "Realtime Voxtral audio must be mono 16000 Hz PCM16.",
                nameof(format));
        }
        _source = source;
        _format = format;
        _timing = timing ?? RealtimeAudioPumpTiming.System;
        _normalizer = new PcmFrameNormalizer(
            _format,
            NormalizedFrameDurationMilliseconds);
    }

    public event Action? GapStarted;

    public RealtimeAudioPumpSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                int maximumFrames = Volatile.Read(ref _maximumObservedQueueFrames);
                return new(
                    _totalRoutedBytes,
                    _totalSentBytes,
                    _totalGapBytes,
                    _normalizedFramesRouted,
                    maximumFrames,
                    TimeSpan.FromMilliseconds(
                        maximumFrames * NormalizedFrameDurationMilliseconds));
            }
        }
    }

    public PreparedRealtimeAudioAttempt PrepareAttempt(int connectionGeneration)
    {
        lock (_gate)
        {
            if (_prepared is not null)
                throw new InvalidOperationException("Only one Voxtral audio connection attempt may be prepared.");
            if (_active is not null)
                throw new InvalidOperationException("An active Voxtral audio route must end before another attempt is prepared.");

            var channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(ActiveQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            _prepared = new PreparedRealtimeAudioAttempt(connectionGeneration, channel);
            return _prepared;
        }
    }

    public void ActivateAttempt(PreparedRealtimeAudioAttempt attempt)
    {
        bool notifyGap;
        lock (_gate)
        {
            if (!ReferenceEquals(_prepared, attempt) || attempt.IsEnded)
                throw new InvalidOperationException("Only the current prepared Voxtral audio attempt can be activated.");
            if (_active is not null)
                throw new InvalidOperationException("Only one Voxtral audio route may be active.");
            notifyGap = AccountCarryAsGapLocked();
            _prepared = null;
            _active = attempt;
            attempt.IsActive = true;
        }
        if (notifyGap)
            GapStarted?.Invoke();
    }

    public void ReportSent(PreparedRealtimeAudioAttempt attempt, int byteCount)
    {
        lock (_gate)
        {
            if (byteCount < 0)
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            if (attempt.IsEnded)
                return;
            attempt.SentBytes = checked(attempt.SentBytes + byteCount);
            _totalSentBytes = checked(_totalSentBytes + byteCount);
        }
    }

    public void EndFailedAttempt(
        PreparedRealtimeAudioAttempt attempt,
        Exception failure)
    {
        bool notifyGap = false;
        lock (_gate)
        {
            if (attempt.IsEnded)
                return;
            if (!ReferenceEquals(_prepared, attempt) && !ReferenceEquals(_active, attempt))
                return;

            if (ReferenceEquals(_active, attempt))
            {
                if (ReferenceEquals(_carryDestination, attempt))
                    notifyGap |= AccountCarryAsGapLocked();
                long unsent = Math.Max(0, attempt.RoutedBytes - attempt.SentBytes);
                _gapBytes += unsent;
                _totalGapBytes += unsent;
                notifyGap |= StartGapLocked();
                _active = null;
            }
            if (ReferenceEquals(_prepared, attempt))
                _prepared = null;
            attempt.IsActive = false;
            attempt.IsEnded = true;
            attempt.Channel.Writer.TryComplete(failure);
        }
        if (notifyGap)
            GapStarted?.Invoke();
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

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (AudioFrame sourceFrame in _source.WithCancellation(cancellationToken))
        {
            AudioFrame[] normalized;
            PreparedRealtimeAudioAttempt? destination;
            bool notifyGap;
            lock (_gate)
            {
                destination = _active;
                notifyGap = false;
                if (!ReferenceEquals(_carryDestination, destination))
                    notifyGap = AccountCarryAsGapLocked();
                _carryDestination = destination;
                normalized = _normalizer.Normalize(sourceFrame).ToArray();
            }
            if (notifyGap)
                GapStarted?.Invoke();
            foreach (AudioFrame frame in normalized)
                await RouteAsync(frame, destination, cancellationToken);
        }
        AudioFrame? final;
        PreparedRealtimeAudioAttempt? finalDestination;
        lock (_gate)
        {
            final = _normalizer.Complete();
            finalDestination = _carryDestination;
            _carryDestination = null;
        }
        if (final is not null)
            await RouteAsync(final, finalDestination, cancellationToken);
        CompleteRoutes();
    }

    public void CompleteRoutes()
    {
        lock (_gate)
        {
            _prepared?.Channel.Writer.TryComplete();
            _active?.Channel.Writer.TryComplete();
            if (_prepared is not null)
                _prepared.IsEnded = true;
            if (_active is not null)
                _active.IsEnded = true;
            _prepared = null;
            _active = null;
        }
    }

    private async Task RouteAsync(
        AudioFrame frame,
        PreparedRealtimeAudioAttempt? destination,
        CancellationToken cancellationToken)
    {
        PreparedRealtimeAudioAttempt? attempt = destination;
        bool notifyGap = false;
        lock (_gate)
        {
            if (attempt is null || attempt.IsEnded)
            {
                _gapBytes += frame.Pcm.Length;
                _totalGapBytes += frame.Pcm.Length;
                notifyGap = StartGapLocked();
                attempt = null;
            }
            else
            {
                attempt.RoutedBytes = checked(attempt.RoutedBytes + frame.Pcm.Length);
                _totalRoutedBytes = checked(_totalRoutedBytes + frame.Pcm.Length);
                _normalizedFramesRouted++;
            }
        }
        if (notifyGap)
            GapStarted?.Invoke();
        if (attempt is null)
            return;

        using var writeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task write = attempt.Channel.Writer
            .WriteAsync(frame, writeCancellation.Token)
            .AsTask();
        if (write.IsCompleted)
        {
            if (await ObserveRouteWriteAsync(write, attempt))
                RecordQueueDepth(attempt);
            return;
        }

        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task timeout = _timing.Delay(ActiveWriteTimeout, timeoutCancellation.Token);
        Task first = await Task.WhenAny(write, timeout);
        if (ReferenceEquals(first, write))
        {
            timeoutCancellation.Cancel();
            try
            {
                await timeout;
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
            }
            if (await ObserveRouteWriteAsync(write, attempt))
                RecordQueueDepth(attempt);
            return;
        }

        await timeout;
        cancellationToken.ThrowIfCancellationRequested();
        writeCancellation.Cancel();
        try
        {
            await write;
        }
        catch (OperationCanceledException) when (writeCancellation.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (attempt.IsEnded)
        {
            return;
        }

        var overflow = new RealtimeAudioQueueOverflowException(
            attempt.ConnectionGeneration,
            ActiveQueueCapacity,
            ActiveQueueByteCapacity,
            ActiveQueueDuration);
        lock (_gate)
        {
            if (ReferenceEquals(_active, attempt))
            {
                _active = null;
                attempt.IsActive = false;
                attempt.IsEnded = true;
                attempt.Channel.Writer.TryComplete(overflow);
            }
        }
        throw overflow;
    }

    private static async Task<bool> ObserveRouteWriteAsync(
        Task write,
        PreparedRealtimeAudioAttempt attempt)
    {
        try
        {
            await write;
            return true;
        }
        catch (ChannelClosedException) when (attempt.IsEnded)
        {
            return false;
        }
    }

    private bool StartGapLocked()
    {
        if (_gapStarted)
            return false;
        _gapStarted = true;
        return true;
    }

    private bool AccountCarryAsGapLocked()
    {
        int carryBytes = _normalizer.DiscardCarry();
        _carryDestination = null;
        if (carryBytes == 0)
            return false;
        _gapBytes += carryBytes;
        _totalGapBytes += carryBytes;
        return StartGapLocked();
    }

    private void RecordQueueDepth(PreparedRealtimeAudioAttempt attempt)
    {
        int depth = attempt.Reader.CanCount ? attempt.Reader.Count : 0;
        int current;
        while (depth > (current = Volatile.Read(ref _maximumObservedQueueFrames)))
        {
            if (Interlocked.CompareExchange(
                    ref _maximumObservedQueueFrames,
                    depth,
                    current) == current)
                break;
        }
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
    private RealtimeAudioPump? _audioPump;
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

    public RealtimeAudioPumpSnapshot? AudioSnapshot => _audioPump?.Snapshot;

    public async IAsyncEnumerable<StreamingTranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("The Voxtral connection supervisor can only be started once.");

        using var pumpCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = new RealtimeAudioPump(audio, _format);
        _audioPump = pump;
        var sideEvents = Channel.CreateBounded<StreamingTranscriptionEvent>(
            new BoundedChannelOptions(4)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });
        pump.GapStarted += () => sideEvents.Writer.TryWrite(new RealtimeAudioGapStarted());
        Task? pumpTask = null;
        int generation = 1;
        int retryAttempt = 0;
        bool establishedOnce = false;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PreparedRealtimeAudioAttempt attempt = pump.PrepareAttempt(generation);
                using var attemptCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                await using IStreamingTranscriber transcriber =
                    _transcriberFactory(
                        generation,
                        byteCount => pump.ReportSent(attempt, byteCount));
                Exception? failure = null;
                long attemptStartedTimestamp = Stopwatch.GetTimestamp();
                await using IAsyncEnumerator<StreamingTranscriptionEvent> events = transcriber
                    .TranscribeAsync(
                        ReadFrames(attempt.Reader, attemptCancellation.Token),
                        attemptCancellation.Token)
                    .GetAsyncEnumerator(attemptCancellation.Token);
                while (failure is null)
                {
                    bool hasNext;
                    StreamingTranscriptionEvent? item = null;
                    try
                    {
                        Task<bool> moveNext = events.MoveNextAsync().AsTask();
                        Task first = pumpTask is null
                            ? moveNext
                            : await Task.WhenAny(moveNext, pumpTask);
                        if (pumpTask is not null && ReferenceEquals(first, pumpTask))
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
                        pump.EndFailedAttempt(attempt, exception);
                        break;
                    }

                    if (!hasNext)
                    {
                        failure = new VoxtralFoxException(
                            "unexpected_close",
                            "The Voxtral session ended without application cancellation.");
                        pump.EndFailedAttempt(attempt, failure);
                        break;
                    }

                    while (sideEvents.Reader.TryRead(out StreamingTranscriptionEvent? side))
                        yield return side;
                    if (item is StreamingSessionStarted)
                    {
                        pump.ActivateAttempt(attempt);
                        pumpTask ??= pump.RunAsync(pumpCancellation.Token);
                        TimeSpan sessionCreatedWait =
                            Stopwatch.GetElapsedTime(attemptStartedTimestamp);
                        yield return new RealtimeAudioRouteActivated(
                            generation,
                            RealtimeAudioPump.NormalizedFrameByteCount,
                            RealtimeAudioPump.ActiveQueueCapacity,
                            RealtimeAudioPump.ActiveQueueByteCapacity,
                            RealtimeAudioPump.ActiveQueueDuration,
                            sessionCreatedWait);
                        while (sideEvents.Reader.TryRead(out StreamingTranscriptionEvent? side))
                            yield return side;
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
            pump.CompleteRoutes();
            if (pumpTask is not null)
            {
                try
                {
                    await pumpTask;
                }
                catch (OperationCanceledException) when (pumpCancellation.IsCancellationRequested)
                {
                }
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
