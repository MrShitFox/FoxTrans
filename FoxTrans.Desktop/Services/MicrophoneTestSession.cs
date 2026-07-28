namespace FoxTrans.Desktop.Services;

public sealed class MicrophoneTestSession : IAsyncDisposable
{
    private readonly LatestAudioVisualFrame _latest = new();
    private CancellationTokenSource? _cancellation;
    private NAudioMicrophoneSource? _source;
    private Task? _pump;
    private int _disposed;

    public bool IsRunning =>
        _pump is { IsCompleted: false } &&
        _cancellation is { IsCancellationRequested: false };

    public AudioVisualFrame? LatestFrame => _latest.Latest;

    public Task StartAsync(
        ResolvedAudioInput input,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (IsRunning)
            throw new InvalidOperationException(
                "The microphone test is already running.");

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _source = new NAudioMicrophoneSource(input);
        _pump = PumpAsync(_source, _cancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation =
            Interlocked.Exchange(ref _cancellation, null);
        Task? pump = Interlocked.Exchange(ref _pump, null);
        NAudioMicrophoneSource? source =
            Interlocked.Exchange(ref _source, null);
        cancellation?.Cancel();
        source?.Dispose();
        if (pump is not null)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        cancellation?.Dispose();
    }

    private async Task PumpAsync(
        IAudioSource source,
        CancellationToken cancellationToken)
    {
        var extractor = new Pcm16AudioFeatureExtractor(source.Format);
        await foreach (AudioFrame frame in source
            .ReadFramesAsync(cancellationToken)
            .WithCancellation(cancellationToken)
            .ConfigureAwait(false))
        {
            if (extractor.TryProcess(frame, out AudioVisualFrame visual))
                _latest.Publish(visual);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await StopAsync().ConfigureAwait(false);
    }
}
