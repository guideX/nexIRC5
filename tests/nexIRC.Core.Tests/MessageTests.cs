using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class MessageTests
{
    [Fact]
    public void ParsesTagsAndAllPrefixForms()
    {
        var result = IrcMessageParser.Parse("@time=2026-08-30T12:00:00.000Z;escaped=a\\:b\\sc\\r\\n\\\\ :nick!user@host PRIVMSG #room :hello world");

        var message = Assert.IsType<IrcMessage>(result.Message);
        Assert.Equal("@time=2026-08-30T12:00:00.000Z;escaped=a\\:b\\sc\\r\\n\\\\ :nick!user@host PRIVMSG #room :hello world", message.RawLine);
        Assert.Equal("a;b c\r\n\\", message.TagValues["escaped"]);
        Assert.Equal("nick", message.Prefix!.Name);
        Assert.Equal("user", message.Prefix.User);
        Assert.Equal("host", message.Prefix.Host);
        Assert.Equal("PRIVMSG", message.Command);
        Assert.Null(message.NumericCommand);
        Assert.Equal(["#room"], message.MiddleParameters);
        Assert.Equal("hello world", message.TrailingParameter);
    }

    [Fact]
    public void PreservesValuelessAndEmptyTags()
    {
        var message = IrcMessageParser.Parse("@label;empty= COMMAND").Message!;

        Assert.False(message.Tags[0].HasValue);
        Assert.Null(message.Tags[0].Value);
        Assert.True(message.Tags[1].HasValue);
        Assert.Equal(string.Empty, message.Tags[1].Value);
    }

    [Fact]
    public void ParsesUnknownNumericWithoutLosingTheCode()
    {
        var message = IrcMessageParser.Parse(":server 742 nick :future reply").Message!;

        Assert.True(message.IsNumeric);
        Assert.Equal(742, message.NumericCommand);
        Assert.Equal("742", message.RawCommand);
        Assert.Equal("future reply", message.TrailingParameter);
    }

    [Fact]
    public void ParsesServerNickAndMalformedSafePrefixes()
    {
        var server = IrcMessageParser.Parse(":irc.example PING").Message!;
        var nick = IrcMessageParser.Parse(":nick PRIVMSG #c :hi").Message!;
        var malformed = IrcMessageParser.Parse(":!@ NOTICE :safe").Message!;

        Assert.Equal("irc.example", server.Prefix!.Name);
        Assert.Equal(IrcPrefixKind.ServerOrNick, server.Prefix.Kind);
        Assert.Equal("nick", nick.Prefix!.Name);
        Assert.Equal(string.Empty, malformed.Prefix!.Name);
        Assert.Equal(string.Empty, malformed.Prefix.User);
        Assert.Equal("NOTICE", malformed.Command);
    }

    [Fact]
    public void EmptyTrailingParameterIsDifferentFromNoTrailingParameter()
    {
        var empty = IrcMessageParser.Parse("COMMAND :").Message!;
        var absent = IrcMessageParser.Parse("COMMAND").Message!;

        Assert.True(empty.HasTrailingParameter);
        Assert.Equal(string.Empty, empty.TrailingParameter);
        Assert.False(absent.HasTrailingParameter);
        Assert.Null(absent.TrailingParameter);
    }

    [Fact]
    public void UnknownCommandIsRetainedAndMalformedInputReturnsResult()
    {
        var unknown = IrcMessageParser.Parse("X-NEXIRC :future");
        var malformed = IrcMessageParser.Parse("@broken");

        Assert.Equal("X-NEXIRC", unknown.Message!.Command);
        Assert.False(malformed.Success);
        Assert.NotNull(malformed.Error);
    }
}
