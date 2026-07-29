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
                checked((uint)(pcm.Length / sizeof(short))));
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

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vad_create")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial IntPtr Create();

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vad_set_mode")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SetMode(IntPtr instance, int mode);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vad_process")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe partial int Process(
        IntPtr instance,
        int sampleRate,
        short* audioFrame,
        uint frameLength);

    [LibraryImport("FoxTrans.Native", EntryPoint = "foxtrans_vad_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Free(IntPtr instance);
}
