using Xunit;

public sealed class NativeVadTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Theory]
    [InlineData(VadOperatingMode.HighQuality, 265)]
    [InlineData(VadOperatingMode.LowBitrate, 265)]
    // The application passes the PCM16 samples directly. libfvad's wavtest
    // rescales them through doubles, which changes one mode-2 threshold frame.
    [InlineData(VadOperatingMode.Aggressive, 257)]
    [InlineData(VadOperatingMode.VeryAggressive, 241)]
    public void WebRtcVadMatchesVendoredReferenceVector(
        VadOperatingMode mode,
        int expectedSpeechFrames)
    {
        using var vad = new NativeWebRtcVad(mode);

        int speechFrames = ReferenceFrames()
            .Count(frame => vad.HasSpeech(frame.Span, Format.SampleRate));

        Assert.Equal(expectedSpeechFrames, speechFrames);
    }

    [Theory]
    [InlineData(VadOperatingMode.HighQuality)]
    [InlineData(VadOperatingMode.LowBitrate)]
    [InlineData(VadOperatingMode.Aggressive)]
    [InlineData(VadOperatingMode.VeryAggressive)]
    public async Task WebRtcVadTreatsReferenceSilenceAsNonSpeech(
        VadOperatingMode mode)
    {
        using var segmenter = new WebRtcVadSegmenter(new(
            MinSpeechFrames: 1,
            MinSilenceFrames: 1,
            PreRollFrames: 0,
            MinimumPhraseMs: 0,
            OperatingMode: mode));

        var updates = new List<SegmentationUpdate>();
        await foreach (SegmentationUpdate update in segmenter.SegmentAsync(
            SilenceFrames(12),
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Empty(updates);
    }

    [Fact]
    public void ResolvingAudioSelectionRetainsTheOpaqueNativeIdentifier()
    {
        var device = new AudioInputDevice(
            2,
            "Cross-platform microphone",
            "opaque-native-device-id");

        ResolvedAudioInput resolved = AudioDeviceSelection.Resolve(
            "2",
            [device],
            Format);

        Assert.Equal(device.NativeId, resolved.NativeId);
    }

    [Fact]
    public void DefaultSelectionUsesTheNativeDefaultWithoutRenumberingDevices()
    {
        AudioInputDevice[] devices =
        [
            new(0, "First", "first-id"),
            new(7, "System default", "default-id", IsDefault: true)
        ];

        ResolvedAudioInput resolved = AudioDeviceSelection.Resolve(
            "default",
            devices,
            Format);

        Assert.Equal(7, resolved.DeviceNumber);
        Assert.Equal("default-id", resolved.NativeId);
    }

    private static async IAsyncEnumerable<AudioFrame> SilenceFrames(int count)
    {
        for (int index = 0; index < count; index++)
        {
            yield return new AudioFrame(new byte[640], Format);
            await Task.Yield();
        }
    }

    private static IEnumerable<ReadOnlyMemory<byte>> ReferenceFrames()
    {
        const string resourceName = "FoxTrans.Tests.ReferenceAudio.audio_tiny16.wav";
        using Stream stream = typeof(NativeVadTests).Assembly
            .GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing VAD reference resource '{resourceName}'.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        byte[] wave = buffer.ToArray();

        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wave, 8, 4));
        int offset = 12;
        ReadOnlyMemory<byte> pcm = default;
        while (offset + 8 <= wave.Length)
        {
            int length = BitConverter.ToInt32(wave, offset + 4);
            int dataOffset = checked(offset + 8);
            if (length < 0 || dataOffset + length > wave.Length)
                throw new InvalidDataException("The vendored VAD reference wave is malformed.");

            if (System.Text.Encoding.ASCII.GetString(wave, offset, 4) == "fmt ")
            {
                Assert.Equal((short)1, BitConverter.ToInt16(wave, dataOffset));
                Assert.Equal((short)1, BitConverter.ToInt16(wave, dataOffset + 2));
                Assert.Equal(16000, BitConverter.ToInt32(wave, dataOffset + 4));
                Assert.Equal((short)16, BitConverter.ToInt16(wave, dataOffset + 14));
            }
            else if (System.Text.Encoding.ASCII.GetString(wave, offset, 4) == "data")
            {
                pcm = wave.AsMemory(dataOffset, length);
                break;
            }

            offset = checked(dataOffset + length + (length & 1));
        }

        Assert.False(pcm.IsEmpty);
        Assert.Equal(0, pcm.Length % 640);
        Assert.Equal(270, pcm.Length / 640);
        for (int frameOffset = 0; frameOffset < pcm.Length; frameOffset += 640)
            yield return pcm.Slice(frameOffset, 640);
    }
}
