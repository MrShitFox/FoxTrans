using System.Text;
using Xunit;

public sealed class WavPackerTests
{
    [Fact]
    public void PackWritesFormatAndPcmWithoutMutatingInput()
    {
        var format = new AudioFormat(48000, 24, 2);
        byte[] pcm = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        byte[] original = [.. pcm];

        byte[] wav = WavPacker.Pack(pcm, format);

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + pcm.Length, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal((short)format.Channels, BitConverter.ToInt16(wav, 22));
        Assert.Equal(format.SampleRate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(format.BytesPerSecond, BitConverter.ToInt32(wav, 28));
        Assert.Equal(format.BitsPerSample, BitConverter.ToInt16(wav, 34));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));
        Assert.Equal(pcm, wav[44..]);
        Assert.Equal(original, pcm);
    }
}
