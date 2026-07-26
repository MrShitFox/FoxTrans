using System.Net.Sockets;
using System.Text;

public enum TranslationUpdateKind
{
    Translation,
    Typing
}

public sealed record TranslationUpdate(
    TranslationUpdateKind Kind,
    string? Text = null,
    bool IsTyping = false)
{
    public static TranslationUpdate Translated(string text) =>
        new(TranslationUpdateKind.Translation, Text: text);

    public static TranslationUpdate Typing(bool isTyping) =>
        new(TranslationUpdateKind.Typing, IsTyping: isTyping);
}

public interface IOutputSink
{
    string Name { get; }

    Task PublishAsync(TranslationUpdate update, CancellationToken cancellationToken);
}

public static class OscPacketFormatter
{
    public static byte[] ChatboxInput(string text)
    {
        var packet = new List<byte>();
        packet.AddRange(PackString("/chatbox/input"));
        packet.AddRange(PackString(",sTT"));
        packet.AddRange(PackString(text));
        return packet.ToArray();
    }

    public static byte[] Typing(bool isTyping)
    {
        var packet = new List<byte>();
        packet.AddRange(PackString("/chatbox/typing"));
        packet.AddRange(PackString(isTyping ? ",T" : ",F"));
        return packet.ToArray();
    }

    private static byte[] PackString(string value)
    {
        byte[] stringBytes = Encoding.UTF8.GetBytes(value);
        int terminatedLength = stringBytes.Length + 1;
        int padding = (4 - terminatedLength % 4) % 4;
        byte[] result = new byte[terminatedLength + padding];
        stringBytes.CopyTo(result, 0);
        return result;
    }
}

public sealed class VrChatOscOutput : IOutputSink, IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly bool _typingEnabled;

    public VrChatOscOutput(AppConfig.OscConfig config)
    {
        _typingEnabled = config.EnableTypingIndicator;
        _udp = new UdpClient();
        _udp.Connect(config.IpAddress, config.Port);
    }

    public string Name => "VRChat OSC";

    public async Task PublishAsync(TranslationUpdate update, CancellationToken cancellationToken)
    {
        byte[]? packet = update.Kind switch
        {
            TranslationUpdateKind.Translation when !string.IsNullOrWhiteSpace(update.Text) =>
                OscPacketFormatter.ChatboxInput(update.Text),
            TranslationUpdateKind.Typing when _typingEnabled =>
                OscPacketFormatter.Typing(update.IsTyping),
            _ => null
        };

        if (packet is not null)
        {
            await _udp.SendAsync(packet, cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
