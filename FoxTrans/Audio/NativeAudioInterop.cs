using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

internal static partial class NativeAudioInterop
{
    private const string LibraryName = "FoxTrans.Native";
    private const int DeviceIdSize = 256;
    private const int DeviceNameSize = 256;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private sealed class NativeAudioDevice
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DeviceIdSize)]
        public byte[] Id = [];

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = DeviceNameSize)]
        public byte[] Name = [];

        public int IsDefault;
    }

    private static readonly int NativeAudioDeviceSize =
        Marshal.SizeOf<NativeAudioDevice>();

    public static IReadOnlyList<AudioInputDevice> GetInputs()
    {
        int result = AudioEnumerate(IntPtr.Zero, 0, out uint count);
        ThrowOnError(result, "The microphone input devices could not be enumerated");
        if (count == 0)
            return [];

        nint bytes = checked((nint)(count * (uint)NativeAudioDeviceSize));
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            result = AudioEnumerate(buffer, count, out uint written);
            ThrowOnError(result, "The microphone input devices could not be enumerated");
            int deviceCount = checked((int)Math.Min(count, written));
            var devices = new List<AudioInputDevice>(deviceCount);
            for (int index = 0; index < deviceCount; index++)
            {
                IntPtr current = IntPtr.Add(
                    buffer,
                    checked(index * NativeAudioDeviceSize));
                NativeAudioDevice device =
                    Marshal.PtrToStructure<NativeAudioDevice>(current)!;
                string displayName = ReadUtf8(device.Name);
                devices.Add(new(
                    index,
                    string.IsNullOrWhiteSpace(displayName)
                        ? $"Input {index}"
                        : displayName,
                    Convert.ToBase64String(device.Id),
                    device.IsDefault != 0));
            }
            return devices;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static unsafe int CreateCapture(
        byte[]? deviceId,
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, void> callback,
        IntPtr userData,
        out IntPtr capture)
    {
        if (deviceId is not null && deviceId.Length != DeviceIdSize)
            throw new ArgumentException("The native microphone identifier is invalid.", nameof(deviceId));

        fixed (byte* device = deviceId)
        {
            return AudioCaptureCreate(
                device,
                deviceId is null ? 0 : 1,
                (IntPtr)callback,
                userData,
                out capture);
        }
    }

    public static string DescribeResult(int result)
    {
        IntPtr value = AudioResultDescription(result);
        return value == IntPtr.Zero
            ? $"native audio error {result}"
            : Marshal.PtrToStringUTF8(value) ?? $"native audio error {result}";
    }

    public static void ThrowOnError(int result, string operation)
    {
        if (result != 0)
            throw new AudioDeviceSelectionException(
                $"{operation}: {DescribeResult(result)}.");
    }

    private static string ReadUtf8(byte[] bytes)
    {
        int length = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, length < 0 ? bytes.Length : length);
    }

    [LibraryImport(LibraryName, EntryPoint = "foxtrans_audio_enumerate")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int AudioEnumerate(
        IntPtr devices,
        uint capacity,
        out uint total);

    [LibraryImport(LibraryName, EntryPoint = "foxtrans_audio_capture_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int AudioCaptureCreate(
        byte* deviceId,
        int hasDeviceId,
        IntPtr callback,
        IntPtr userData,
        out IntPtr capture);

    [LibraryImport(LibraryName, EntryPoint = "foxtrans_audio_capture_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int AudioCaptureStart(IntPtr capture);

    [LibraryImport(LibraryName, EntryPoint = "foxtrans_audio_capture_stop")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int AudioCaptureStop(IntPtr capture);

    [LibraryImport(LibraryName, EntryPoint = "foxtrans_audio_capture_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void AudioCaptureDestroy(IntPtr capture);

    [LibraryImport(LibraryName, EntryPoint = "foxtrans_audio_result_description")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr AudioResultDescription(int result);
}
