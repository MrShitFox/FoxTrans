using System.Globalization;
using System.Net;
using System.Net.Sockets;
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

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("short translation")]
    public void VrChatTextAtOrBelowLimitIsUnchanged(string text) =>
        Assert.Equal(text, VrChatTextFormatter.Format(text));

    [Fact]
    public void OneHundredFortyFourElementsRemainUnchanged()
    {
        string text = new('x', 144);
        Assert.Equal(text, VrChatTextFormatter.Format(text));
    }

    [Fact]
    public void TruncationKeepsNewestEndingAndEllipsisCountsTowardLimit()
    {
        string text = new('a', 145);
        string result = VrChatTextFormatter.Format(text);
        Assert.StartsWith("…", result);
        Assert.EndsWith(new string('a', 143), result);
        Assert.Equal(144, StringInfo.ParseCombiningCharacters(result).Length);
    }

    [Fact]
    public void TruncationPrefersAWordBoundary()
    {
        string ending = " newest words remain visible";
        string result = VrChatTextFormatter.Format(new string('x', 140) + ending);
        Assert.Equal("…newest words remain visible", result);
    }

    [Fact]
    public void LongWordEmojiCombiningTextAndNonLatinAreSafe()
    {
        string family = "👨‍👩‍👧‍👦";
        string combining = "e\u0301";
        string text = new string('x', 150) + family + combining + "最新の終わり";
        string result = VrChatTextFormatter.Format(text);
        Assert.EndsWith(family + combining + "最新の終わり", result);
        Assert.True(StringInfo.ParseCombiningCharacters(result).Length <= 144);
        Assert.DoesNotContain("\uFFFD", result);
        Assert.False(char.IsLowSurrogate(result[1]));
    }

    [Fact]
    public void NewlinesCountAsTextElementsConsistently()
    {
        string result = VrChatTextFormatter.Format(
            string.Join('\n', Enumerable.Repeat("line", 40)));
        Assert.True(StringInfo.ParseCombiningCharacters(result).Length <= 144);
        Assert.EndsWith("line", result);
    }

    [Fact]
    public async Task VrChatOutputFormatsEveryTranslationAndLeavesTypingPacketsUnchanged()
    {
        using var listener = new UdpClient(
            new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        await using var output = new VrChatOscOutput(
            new ResolvedOscEndpoint("127.0.0.1", port, true));
        string translation = new('a', 145);

        await output.PublishAsync(
            TranslationUpdate.Translated(translation),
            TestContext.Current.CancellationToken);
        UdpReceiveResult translated = await listener.ReceiveAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            VrChatTextFormatter.Format(translation),
            ReadOscStrings(translated.Buffer)[2]);

        await output.PublishAsync(
            TranslationUpdate.Typing(true),
            TestContext.Current.CancellationToken);
        UdpReceiveResult typing = await listener.ReceiveAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(",T", ReadOscStrings(typing.Buffer)[1]);
    }

    private static string[] ReadOscStrings(byte[] packet)
    {
        var values = new List<string>();
        int offset = 0;
        while (offset < packet.Length)
        {
            int end = Array.IndexOf(packet, (byte)0, offset);
            if (end < 0)
                break;
            values.Add(Encoding.UTF8.GetString(packet, offset, end - offset));
            offset = (end + 4) & ~3;
        }
        return values.ToArray();
    }
}
