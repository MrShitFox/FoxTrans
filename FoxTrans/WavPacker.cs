using System.Text;

public static class WavPacker
{
    public static byte[] Pack(byte[] rawPcmData)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // RIFF header
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + rawPcmData.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk (16000Hz, 16-bit, Mono)
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // Channels
        writer.Write(16000);    // Sample Rate
        writer.Write(16000 * 1 * 2); // Byte Rate
        writer.Write((short)2); // Block Align
        writer.Write((short)16);// Bits per Sample

        // data chunk
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(rawPcmData.Length);
        writer.Write(rawPcmData);

        return ms.ToArray();
    }
}