internal sealed class DelegateTimeProvider(
    Func<DateTimeOffset>? getUtcNow = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null) :
    TimeProvider
{
    private readonly Func<DateTimeOffset> _getUtcNow =
        getUtcNow ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay =
        delay ?? Task.Delay;

    public override DateTimeOffset GetUtcNow() => _getUtcNow();

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period) =>
        new DelegateTimer(_delay, callback, state, dueTime, period);

    private sealed class DelegateTimer : ITimer
    {
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private readonly TimeSpan _period;
        private readonly CancellationTokenSource _cancellation = new();

        public DelegateTimer(
            Func<TimeSpan, CancellationToken, Task> delay,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            _delay = delay;
            _callback = callback;
            _state = state;
            _period = period;
            _ = RunAsync(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose() => _cancellation.Cancel();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task RunAsync(TimeSpan dueTime)
        {
            try
            {
                TimeSpan next = dueTime;
                while (true)
                {
                    await _delay(next, _cancellation.Token);
                    if (_cancellation.IsCancellationRequested)
                        return;
                    _callback(_state);
                    if (_period == Timeout.InfiniteTimeSpan)
                        return;
                    next = _period;
                }
            }
            catch (OperationCanceledException)
                when (_cancellation.IsCancellationRequested)
            {
            }
        }
    }
}
