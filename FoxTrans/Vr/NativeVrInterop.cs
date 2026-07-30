using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

internal sealed partial class NativeVrOverlayDevice : IVrOverlayDevice
{
    public bool IsAvailable() => VrIsAvailable() != 0;

    public int Open(string key, string name, out nint overlay) =>
        VrOpen(key, name, out overlay);

    public unsafe int Submit(
        nint overlay,
        ReadOnlySpan<byte> rgba,
        uint width,
        uint height)
    {
        fixed (byte* pixels = rgba)
            return VrSubmit(overlay, pixels, width, height);
    }

    public int SetPlacement(nint overlay, VrOverlayPlacement placement)
    {
        int result = VrSetPlacement(
            overlay,
            placement.WidthMeters,
            placement.DistanceMeters,
            placement.PitchDegrees,
            placement.YawDegrees,
            placement.VerticalOffsetMeters);
        return result != 0
            ? result
            : VrSetOpacity(overlay, placement.Opacity);
    }

    public int SetVisible(nint overlay, bool visible) =>
        VrSetVisible(overlay, visible ? 1 : 0);

    public int Poll(nint overlay, out bool shouldQuit)
    {
        int result = VrPoll(overlay, out int quit);
        shouldQuit = quit != 0;
        return result;
    }

    public void Close(nint overlay) => VrClose(overlay);

    public string DescribeResult(int result)
    {
        nint message = VrResultDescription(result);
        return message == 0
            ? $"native VR error {result}"
            : Marshal.PtrToStringUTF8(message) ?? $"native VR error {result}";
    }

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_is_available")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int VrIsAvailable();

    [LibraryImport(
        "FoxTrans.Native",
        EntryPoint = "foxtrans_vr_open",
        StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int VrOpen(string key, string name, out nint overlay);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_submit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int VrSubmit(
        nint overlay,
        byte* rgba,
        uint width,
        uint height);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_set_placement")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int VrSetPlacement(
        nint overlay,
        float widthMeters,
        float distanceMeters,
        float pitchDegrees,
        float yawDegrees,
        float verticalOffsetMeters);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_set_opacity")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int VrSetOpacity(nint overlay, float opacity);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_set_visible")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int VrSetVisible(nint overlay, int visible);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_poll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int VrPoll(nint overlay, out int shouldQuit);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_close")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void VrClose(nint overlay);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vr_result_description")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint VrResultDescription(int result);
}
