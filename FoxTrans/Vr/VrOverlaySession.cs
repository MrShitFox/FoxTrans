public sealed class VrOverlaySession : IDisposable
{
    private IVrOverlayDevice? _device;
    private nint _overlay;

    internal VrOverlaySession(IVrOverlayDevice device, nint overlay)
    {
        _device = device;
        _overlay = overlay;
    }

    public bool IsClosed => Volatile.Read(ref _overlay) == 0;

    public int Submit(ReadOnlySpan<byte> rgba, uint width, uint height)
    {
        IVrOverlayDevice? device = _device;
        nint overlay = Volatile.Read(ref _overlay);
        return device is null || overlay == 0
            ? VrOverlaySupervisor.SessionClosedError
            : device.Submit(overlay, rgba, width, height);
    }

    public int SetPlacement(VrOverlayPlacement placement)
    {
        IVrOverlayDevice? device = _device;
        nint overlay = Volatile.Read(ref _overlay);
        return device is null || overlay == 0
            ? VrOverlaySupervisor.SessionClosedError
            : device.SetPlacement(overlay, placement);
    }

    public int SetVisible(bool visible)
    {
        IVrOverlayDevice? device = _device;
        nint overlay = Volatile.Read(ref _overlay);
        return device is null || overlay == 0
            ? VrOverlaySupervisor.SessionClosedError
            : device.SetVisible(overlay, visible);
    }

    public int Poll(out bool shouldQuit)
    {
        IVrOverlayDevice? device = _device;
        nint overlay = Volatile.Read(ref _overlay);
        if (device is null || overlay == 0)
        {
            shouldQuit = false;
            return VrOverlaySupervisor.SessionClosedError;
        }
        return device.Poll(overlay, out shouldQuit);
    }

    public void Dispose()
    {
        nint overlay = Interlocked.Exchange(ref _overlay, 0);
        IVrOverlayDevice? device = Interlocked.Exchange(ref _device, null);
        if (overlay != 0 && device is not null)
            device.Close(overlay);
    }
}
