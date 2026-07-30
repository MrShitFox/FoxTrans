using System.Text;

public static class WavPacker
{
    public static byte[] Pack(ReadOnlySpan<byte> rawPcmData, AudioFormat format)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + rawPcmData.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(format.Channels);
        writer.Write(format.SampleRate);
        writer.Write(format.BytesPerSecond);
        writer.Write(format.BlockAlign);
        writer.Write(format.BitsPerSample);

        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(rawPcmData.Length);
        writer.Write(rawPcmData);

        return stream.ToArray();
    }
}
