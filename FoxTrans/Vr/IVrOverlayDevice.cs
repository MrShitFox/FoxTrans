/// <summary>
/// Native SteamVR boundary. Keeping the device flat makes the overlay
/// supervisor deterministic and testable without a SteamVR installation.
/// </summary>
public interface IVrOverlayDevice
{
    bool IsAvailable();
    int Open(string key, string name, out nint overlay);
    int Submit(nint overlay, ReadOnlySpan<byte> rgba, uint width, uint height);
    int SetPlacement(nint overlay, VrOverlayPlacement placement);
    int SetVisible(nint overlay, bool visible);
    int Poll(nint overlay, out bool shouldQuit);
    void Close(nint overlay);
    string DescribeResult(int result);
}
