using System.Net.Sockets;
using System.Text;

public static class VRChatOsc
{
    private static byte[] PackOscString(string value)
    {
        byte[] stringBytes = Encoding.UTF8.GetBytes(value);

        int len = stringBytes.Length + 1;
        int padding = (4 - (len % 4)) % 4;

        byte[] result = new byte[len + padding];
        Buffer.BlockCopy(stringBytes, 0, result, 0, stringBytes.Length);

        return result;
    }

    public static async Task SendTextAsync(string text, AppConfig.OscConfig config)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            using var udp = new UdpClient(config.IpAddress, config.Port);

            var packet = new List<byte>();
            packet.AddRange(PackOscString("/chatbox/input"));
            packet.AddRange(PackOscString(",sTT"));
            packet.AddRange(PackOscString(text));

            byte[] data = packet.ToArray();
            await udp.SendAsync(data, data.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[OSC] Error sending text: {ex.Message}");
        }
    }

    public static async Task SetTypingAsync(bool isTyping, AppConfig.OscConfig config)
    {
        if (!config.EnableTypingIndicator) return;

        try
        {
            using var udp = new UdpClient(config.IpAddress, config.Port);

            var packet = new List<byte>();
            packet.AddRange(PackOscString("/chatbox/typing"));
            packet.AddRange(PackOscString(isTyping ? ",T" : ",F"));

            byte[] data = packet.ToArray();
            await udp.SendAsync(data, data.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[OSC] Error updating typing status: {ex.Message}");
        }
    }
}