public enum VrOverlayStatus
{
    Disabled,
    WaitingForSteamVr,
    Connecting,
    Connected,
    Faulted
}

/// <summary>
/// A non-throwing, tick-driven attach/reconnect state machine for SteamVR.
/// It has no dependency on the desktop UI or on Avalonia.
/// </summary>
public sealed class VrOverlaySupervisor : IDisposable
{
    public const int SessionClosedError = -20001;
    public static readonly TimeSpan AvailabilityPollInterval =
        TimeSpan.FromSeconds(3);
    public static readonly TimeSpan UninstalledPollInterval =
        TimeSpan.FromSeconds(30);

    private readonly IVrOverlayDevice _device;
    private readonly TimeProvider _timeProvider;
    private VrOverlaySession? _session;
    private DateTimeOffset _nextAttemptAt;
    private DateTimeOffset _nextConnectedAvailabilityCheck;
    private VrOverlayPlacement? _appliedPlacement;
    private bool? _appliedVisibility;
    private bool _enabled;
    private bool _disposed;
    private int _unavailableCount;
    private int _connectionAttempt;
    private VrOverlayStatus _status = VrOverlayStatus.Disabled;

    public VrOverlaySupervisor(
        IVrOverlayDevice? device = null,
        TimeProvider? timeProvider = null)
    {
        _device = device ?? new NativeVrOverlayDevice();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event Action<VrOverlayStatus>? StatusChanged;

    public VrOverlayStatus Status => _status;
    public string LastError { get; private set; } = "";
    public bool IsConnected => _session is not null;

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (_disposed || _enabled == enabled)
                return;

            _enabled = enabled;
            _connectionAttempt = 0;
            _unavailableCount = 0;
            _nextAttemptAt = _timeProvider.GetUtcNow();
            _appliedPlacement = null;
            _appliedVisibility = null;
            if (!enabled)
            {
                CloseSession();
                SetStatus(VrOverlayStatus.Disabled);
                return;
            }
            SetStatus(VrOverlayStatus.WaitingForSteamVr);
        }
        catch (Exception exception)
        {
            LastError = SafeMessage(exception);
            SetStatus(VrOverlayStatus.Faulted);
        }
    }

    public void Tick(VrOverlayPlacement placement, bool visible = true)
    {
        if (_disposed || !_enabled)
            return;

        try
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_session is not null)
            {
                if (now >= _nextConnectedAvailabilityCheck &&
                    !_device.IsAvailable())
                {
                    DisconnectToWaiting(now);
                    return;
                }
                if (now >= _nextConnectedAvailabilityCheck)
                    _nextConnectedAvailabilityCheck = now + AvailabilityPollInterval;

                int poll = _session.Poll(out bool shouldQuit);
                if (poll != 0 || shouldQuit)
                {
                    if (poll != 0)
                        LastError = SafeDescription(poll);
                    DisconnectToWaiting(now);
                    return;
                }

                ApplyState(placement, visible, now);
                return;
            }

            if (now < _nextAttemptAt)
                return;

            if (_status == VrOverlayStatus.WaitingForSteamVr)
            {
                if (!_device.IsAvailable())
                {
                    _unavailableCount++;
                    _nextAttemptAt = now + (_unavailableCount >= 10
                        ? UninstalledPollInterval
                        : AvailabilityPollInterval);
                    return;
                }
                _unavailableCount = 0;
            }

            SetStatus(VrOverlayStatus.Connecting);
            Connect(placement, visible, now);
        }
        catch (Exception exception)
        {
            LastError = SafeMessage(exception);
            ScheduleConnectionRetry(_timeProvider.GetUtcNow());
        }
    }

    public void Submit(
        ReadOnlySpan<byte> rgba,
        uint width,
        uint height)
    {
        if (_disposed || !_enabled || _session is null)
            return;

        try
        {
            int result = _session.Submit(rgba, width, height);
            if (result == 0)
                return;
            LastError = SafeDescription(result);
            DisconnectToWaiting(_timeProvider.GetUtcNow());
        }
        catch (Exception exception)
        {
            LastError = SafeMessage(exception);
            DisconnectToWaiting(_timeProvider.GetUtcNow());
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CloseSession();
        SetStatus(VrOverlayStatus.Disabled);
    }

    private void Connect(
        VrOverlayPlacement placement,
        bool visible,
        DateTimeOffset now)
    {
        int result = _device.Open(
            "foxtrans.overlay",
            "FoxTrans Overlay",
            out nint handle);
        if (result != 0 || handle == 0)
        {
            LastError = result == 0
                ? "SteamVR did not return an overlay handle."
                : SafeDescription(result);
            ScheduleConnectionRetry(now);
            return;
        }

        _session = new VrOverlaySession(_device, handle);
        _appliedPlacement = null;
        _appliedVisibility = null;
        if (!ApplyState(placement, visible, now))
            return;
        _connectionAttempt = 0;
        _nextConnectedAvailabilityCheck = now + AvailabilityPollInterval;
        LastError = "";
        SetStatus(VrOverlayStatus.Connected);
    }

    private bool ApplyState(
        VrOverlayPlacement placement,
        bool visible,
        DateTimeOffset now)
    {
        if (_session is null)
            return false;

        if (_appliedPlacement != placement)
        {
            int result = _session.SetPlacement(placement);
            if (result != 0)
            {
                LastError = SafeDescription(result);
                DisconnectToWaiting(now);
                return false;
            }
            _appliedPlacement = placement;
        }
        if (_appliedVisibility != visible)
        {
            int result = _session.SetVisible(visible);
            if (result != 0)
            {
                LastError = SafeDescription(result);
                DisconnectToWaiting(now);
                return false;
            }
            _appliedVisibility = visible;
        }
        return true;
    }

    private void ScheduleConnectionRetry(DateTimeOffset now)
    {
        CloseSession();
        _connectionAttempt++;
        _nextAttemptAt = now + VoxtralRetryPolicy.DelayForAttempt(
            _connectionAttempt);
        SetStatus(VrOverlayStatus.Faulted);
    }

    private void DisconnectToWaiting(DateTimeOffset now)
    {
        CloseSession();
        _connectionAttempt = 0;
        _unavailableCount = 0;
        _nextAttemptAt = now;
        _nextConnectedAvailabilityCheck = default;
        SetStatus(VrOverlayStatus.WaitingForSteamVr);
    }

    private void CloseSession()
    {
        VrOverlaySession? session = Interlocked.Exchange(ref _session, null);
        try
        {
            session?.Dispose();
        }
        catch (Exception exception)
        {
            LastError = SafeMessage(exception);
        }
        _appliedPlacement = null;
        _appliedVisibility = null;
    }

    private string SafeDescription(int result)
    {
        try
        {
            return _device.DescribeResult(result);
        }
        catch (Exception exception)
        {
            return SafeMessage(exception);
        }
    }

    private static string SafeMessage(Exception exception)
    {
        string message = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
        return message.Length <= 300 ? message : message[..300] + "…";
    }

    private void SetStatus(VrOverlayStatus status)
    {
        if (_status == status)
            return;
        _status = status;
        if (StatusChanged is not { } changed)
            return;
        foreach (Action<VrOverlayStatus> subscriber in changed.GetInvocationList())
        {
            try
            {
                subscriber(status);
            }
            catch
            {
                // Status consumers are display-only and cannot disrupt recovery.
            }
        }
    }
}
