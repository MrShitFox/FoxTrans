using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

public enum VadOperatingMode
{
    HighQuality = 0,
    LowBitrate = 1,
    Aggressive = 2,
    VeryAggressive = 3
}

internal sealed partial class NativeWebRtcVad : IDisposable
{
    private IntPtr _instance;

    public NativeWebRtcVad(VadOperatingMode operatingMode)
    {
        _instance = Create();
        if (_instance == IntPtr.Zero)
            throw new InvalidOperationException(
                "WebRTC VAD could not allocate an instance.");
        if (Initialize(_instance) != 0)
        {
            Dispose();
            throw new InvalidOperationException(
                "WebRTC VAD initialization failed.");
        }
        if (SetMode(_instance, (int)operatingMode) != 0)
        {
            Dispose();
            throw new ArgumentOutOfRangeException(
                nameof(operatingMode),
                operatingMode,
                "Unsupported WebRTC VAD operating mode.");
        }
    }

    public unsafe bool HasSpeech(
        ReadOnlySpan<byte> pcm,
        int sampleRate)
    {
        ObjectDisposedException.ThrowIf(_instance == IntPtr.Zero, this);
        if ((pcm.Length & 1) != 0)
            throw new ArgumentException(
                "PCM16 audio must contain a whole number of samples.",
                nameof(pcm));

        fixed (byte* bytes = pcm)
        {
            int result = Process(
                _instance,
                sampleRate,
                (short*)bytes,
                (nuint)(pcm.Length / sizeof(short)));
            return result switch
            {
                0 => false,
                1 => true,
                _ => throw new InvalidOperationException(
                    "WebRTC VAD rejected the audio frame.")
            };
        }
    }

    public void Dispose()
    {
        IntPtr instance = Interlocked.Exchange(
            ref _instance,
            IntPtr.Zero);
        if (instance != IntPtr.Zero)
            Free(instance);
    }

    [LibraryImport("WebRtcVad.dll", EntryPoint = "Vad_Create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr Create();

    [LibraryImport("WebRtcVad.dll", EntryPoint = "Vad_Init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Initialize(IntPtr instance);

    [LibraryImport("WebRtcVad.dll", EntryPoint = "Vad_SetMode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SetMode(IntPtr instance, int mode);

    [LibraryImport("WebRtcVad.dll", EntryPoint = "Vad_Process")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int Process(
        IntPtr instance,
        int sampleRate,
        short* audioFrame,
        nuint frameLength);

    [LibraryImport("WebRtcVad.dll", EntryPoint = "Vad_Free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Free(IntPtr instance);
}
