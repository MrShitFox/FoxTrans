using System.Buffers.Binary;
using Xunit;

public sealed class AudioVisualFeatureTests
{
    private static readonly AudioFormat Format = new(16000, 16, 1);

    [Fact]
    public void SilenceProducesZeroBoundedFeatures()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var extractor = new Pcm16AudioFeatureExtractor(Format, () => now);

        Assert.True(extractor.TryProcess(
            new AudioFrame(new byte[1280], Format),
            out AudioVisualFrame frame));

        Assert.Equal(0, frame.Rms);
        Assert.Equal(0, frame.Peak);
        Assert.False(frame.IsSpeechActive);
        Assert.All(frame.Spectrum.ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(180, 1)]
    [InlineData(720, 4)]
    [InlineData(2100, 7)]
    [InlineData(5200, 10)]
    public void FixedSineWavesPeakNearTheirConfiguredBand(
        double frequency,
        int expectedBand)
    {
        var extractor = new Pcm16AudioFeatureExtractor(
            Format,
            () => DateTimeOffset.UnixEpoch);

        Assert.True(extractor.TryProcess(
            Frame(Sine(frequency, 0.65, 1024)),
            out AudioVisualFrame frame));

        int peak = Array.IndexOf(
            frame.Spectrum.ToArray(),
            frame.Spectrum.ToArray().Max());
        Assert.InRange(peak, expectedBand - 1, expectedBand + 1);
        Assert.All(frame.Spectrum.ToArray(), value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void IncreasingAmplitudeRaisesRmsAndPeak()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var quiet = new Pcm16AudioFeatureExtractor(Format, () => now);
        var loud = new Pcm16AudioFeatureExtractor(Format, () => now);

        quiet.TryProcess(Frame(Sine(480, 0.1, 640)), out AudioVisualFrame low);
        loud.TryProcess(Frame(Sine(480, 0.8, 640)), out AudioVisualFrame high);

        Assert.True(high.Rms > low.Rms * 6);
        Assert.True(high.Peak > low.Peak * 6);
    }

    [Fact]
    public void PositiveAndNegativeFullScaleSamplesReportClipping()
    {
        var extractor = new Pcm16AudioFeatureExtractor(
            Format,
            () => DateTimeOffset.UnixEpoch);
        short[] samples = [short.MaxValue, short.MinValue, short.MaxValue, short.MinValue];

        extractor.TryProcess(Frame(samples), out AudioVisualFrame frame);

        Assert.True(frame.IsClipping);
        Assert.InRange(frame.Peak, 0.999f, 1);
        Assert.InRange(frame.Rms, 0.99f, 1);
    }

    [Fact]
    public void NegativeSamplesHavePositiveEnergy()
    {
        var extractor = new Pcm16AudioFeatureExtractor(
            Format,
            () => DateTimeOffset.UnixEpoch);

        extractor.TryProcess(
            Frame(Enumerable.Repeat((short)-12000, 640).ToArray()),
            out AudioVisualFrame frame);

        Assert.InRange(frame.Rms, 0.36f, 0.37f);
        Assert.InRange(frame.Peak, 0.36f, 0.37f);
    }

    [Fact]
    public void UnsupportedFormatIsReportedHonestly()
    {
        var unsupported = new AudioFormat(16000, 8, 1);
        var extractor = new Pcm16AudioFeatureExtractor(
            unsupported,
            () => DateTimeOffset.UnixEpoch);

        Assert.True(extractor.TryProcess(
            new AudioFrame(new byte[640], unsupported),
            out AudioVisualFrame frame));
        Assert.False(frame.IsSupported);
        Assert.Empty(frame.Spectrum.ToArray());
        Assert.False(extractor.IsSupported);
    }

    [Fact]
    public void ExtractionNeverModifiesInputPcm()
    {
        byte[] pcm = Frame(Sine(1050, 0.5, 640)).Pcm.ToArray();
        byte[] original = [.. pcm];
        var extractor = new Pcm16AudioFeatureExtractor(
            Format,
            () => DateTimeOffset.UnixEpoch);

        extractor.TryProcess(new AudioFrame(pcm, Format), out _);

        Assert.Equal(original, pcm);
    }

    [Fact]
    public void WorkingMemoryHasAConstantDocumentedBound()
    {
        var extractor = new Pcm16AudioFeatureExtractor(
            Format,
            () => DateTimeOffset.UnixEpoch);
        int floats = extractor.FixedWorkingFloatCount;

        for (int index = 0; index < 1000; index++)
            extractor.TryProcess(Frame(Sine(300, 0.2, 320)), out _);

        Assert.Equal(Pcm16AudioFeatureExtractor.WindowSampleCount, extractor.RetainedSignalSampleCount);
        Assert.Equal(floats, extractor.FixedWorkingFloatCount);
        Assert.InRange(floats, 1, 20_000);
    }

    [Fact]
    public void PublicationRateIsBoundedToFifteenFramesPerSecond()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        var extractor = new Pcm16AudioFeatureExtractor(Format, () => now);
        AudioFrame input = Frame(Sine(480, 0.5, 320));

        Assert.True(extractor.TryProcess(input, out _));
        now += TimeSpan.FromMilliseconds(10);
        Assert.False(extractor.TryProcess(input, out _));
        now += TimeSpan.FromMilliseconds(57);
        Assert.True(extractor.TryProcess(input, out _));
    }

    [Fact]
    public void LatestPublisherRetainsOnlyNewestFeatureFrame()
    {
        var latest = new LatestAudioVisualFrame();
        for (int sequence = 1; sequence <= 10_000; sequence++)
        {
            latest.Publish(new(
                0.1f,
                0.2f,
                false,
                false,
                new float[Pcm16AudioFeatureExtractor.SpectrumBandCount],
                sequence,
                DateTimeOffset.UnixEpoch));
        }

        Assert.Equal(10_000, latest.PublishedCount);
        Assert.Equal(10_000, latest.Latest!.Sequence);
        Assert.Equal(Pcm16AudioFeatureExtractor.SpectrumBandCount, latest.Latest.Spectrum.Length);
    }

    private static AudioFrame Frame(short[] samples)
    {
        byte[] pcm = new byte[samples.Length * 2];
        for (int index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteInt16LittleEndian(
                pcm.AsSpan(index * 2, 2),
                samples[index]);
        return new(pcm, Format);
    }

    private static short[] Sine(
        double frequency,
        double amplitude,
        int sampleCount) =>
        Enumerable.Range(0, sampleCount)
            .Select(index => (short)Math.Round(
                Math.Sin(2 * Math.PI * frequency * index / Format.SampleRate) *
                short.MaxValue *
                amplitude))
            .ToArray();
}
