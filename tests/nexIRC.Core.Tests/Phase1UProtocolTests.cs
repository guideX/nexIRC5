using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class Phase1UProtocolTests
{
    [Fact]
    public void BuildsTypedCtcpActionWithoutRawLineConcatenation()
    {
        var message = IrcParticipantCommandBuilder.BuildAction(new IrcCommandBuilder(), "#room", "waves hello");

        Assert.Equal("PRIVMSG #room :\u0001ACTION waves hello\u0001", message.Line);
        Assert.EndsWith("\r\n", System.Text.Encoding.UTF8.GetString(message.FramedBytes.Span));
    }

    [Fact]
    public void RejectsUnsafeActionText()
    {
        Assert.Throws<ArgumentException>(() => IrcParticipantCommandBuilder.BuildAction(new IrcCommandBuilder(), "#room", "bad\r\ntext"));
    }
}
