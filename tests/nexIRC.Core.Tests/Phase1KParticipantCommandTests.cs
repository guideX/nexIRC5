using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class Phase1KParticipantCommandTests
{
    [Fact]
    public void BuildsTypedParticipantCommands()
    {
        var builder = new IrcCommandBuilder();

        Assert.Equal("WHOIS Alex", IrcParticipantCommandBuilder.BuildWhois(builder, "Alex").Line);
        Assert.Equal("NOTICE Alex :hello", IrcParticipantCommandBuilder.BuildNotice(builder, "Alex", "hello").Line);
        Assert.Equal("PRIVMSG Alex :\u0001PING\u0001", IrcParticipantCommandBuilder.BuildCtcp(builder, "Alex", "PING").Line);
        Assert.Equal("MODE #general +q Alex", IrcParticipantCommandBuilder.BuildMemberMode(builder, "#general", 'q', true, "Alex").Line);
        Assert.Equal("MODE #general +b *!user@example.test", IrcParticipantCommandBuilder.BuildBan(builder, "#general", "*!user@example.test").Line);
        Assert.Equal("KICK #general Alex :cleanup", IrcParticipantCommandBuilder.BuildKick(builder, "#general", "Alex", "cleanup").Line);
        Assert.Equal("INVITE Alex #general", IrcParticipantCommandBuilder.BuildInvite(builder, "Alex", "#general").Line);
    }

    [Theory]
    [InlineData("Alex\r\nNICK injected")]
    [InlineData("#general bad")]
    [InlineData("*!user@example.test\r\n")]
    public void RejectsIdentityInjection(string value)
    {
        Assert.Throws<ArgumentException>(() => IrcParticipantCommandBuilder.BuildWhois(new IrcCommandBuilder(), value));
    }

    [Fact]
    public void RejectsControlCharactersAndOversizedParticipantText()
    {
        Assert.Throws<ArgumentException>(() => IrcParticipantCommandBuilder.BuildCtcp(new IrcCommandBuilder(), "Alex", "PING", "\u0001bad"));
        Assert.Throws<InvalidOperationException>(() => IrcParticipantCommandBuilder.BuildNotice(new IrcCommandBuilder(32), "Alex", new string('x', 64)));
    }
}
