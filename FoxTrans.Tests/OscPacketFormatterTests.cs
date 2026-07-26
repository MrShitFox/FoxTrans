using System.Text;
using Xunit;

public sealed class OscPacketFormatterTests
{
    [Fact]
    public void ChatboxPacketPreservesUtf8TextAndFourByteAlignment()
    {
        byte[] packet = OscPacketFormatter.ChatboxInput("Привет");

        Assert.Equal(0, packet.Length % 4);
        Assert.True(packet.AsSpan().IndexOf(Encoding.UTF8.GetBytes("Привет")) >= 0);
        Assert.True(packet.AsSpan().StartsWith(Encoding.UTF8.GetBytes("/chatbox/input")));
    }

    [Theory]
    [InlineData(true, ",T")]
    [InlineData(false, ",F")]
    public void TypingPacketUsesOscBooleanTypeTag(bool value, string tag)
    {
        byte[] packet = OscPacketFormatter.Typing(value);

        Assert.True(packet.AsSpan().IndexOf(Encoding.ASCII.GetBytes(tag)) >= 0);
        Assert.Equal(0, packet.Length % 4);
    }
}
