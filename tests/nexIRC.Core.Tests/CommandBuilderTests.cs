using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class CommandBuilderTests
{
    [Fact]
    public void BuildsMiddleAndTrailingParametersWithCrLf()
    {
        var command = new IrcCommandBuilder().Build("PRIVMSG", ["#room"], "hello world");

        Assert.Equal("PRIVMSG #room :hello world", command.Line);
        Assert.Equal("PRIVMSG #room :hello world\r\n", System.Text.Encoding.UTF8.GetString(command.FramedBytes.Span));
    }

    [Fact]
    public void BuildsEmptyTrailingParameter()
    {
        var command = new IrcCommandBuilder().Build("TOPIC", ["#room"], string.Empty);

        Assert.Equal("TOPIC #room :", command.Line);
    }

    [Theory]
    [InlineData("PRIVMSG #room :bad\r\nNICK injected")]
    [InlineData("PRIVMSG #room\n")]
    public void RejectsCrLfInjection(string value)
    {
        Assert.Throws<ArgumentException>(() => new IrcCommandBuilder().BuildRaw(value));
    }

    [Fact]
    public void RawEscapeHatchStillEnforcesFramingAndLineLimit()
    {
        var command = new IrcCommandBuilder(32).BuildRaw("FOO a:b");

        Assert.Equal("FOO a:b", command.Line);
        Assert.EndsWith("\r\n", System.Text.Encoding.UTF8.GetString(command.FramedBytes.Span));
        Assert.Throws<InvalidOperationException>(() => new IrcCommandBuilder(8).BuildRaw("LONGCOMMAND"));
    }

    [Fact]
    public void TaggedCommandEscapesValuesAndUsesDeterministicTagOrdering()
    {
        var command = new IrcCommandBuilder().BuildWithTags(
            new Dictionary<string, string?> { ["zeta"] = "two words", ["label"] = "nexirc;1" },
            "WHOIS",
            ["Mira"]);

        Assert.Equal("@label=nexirc\\:1;zeta=two\\swords WHOIS Mira", command.Line);
    }

    [Fact]
    public void TaggedCommandRejectsEmptyTagSetAndLineBreaks()
    {
        Assert.Throws<ArgumentException>(() => new IrcCommandBuilder().BuildWithTags(new Dictionary<string, string?>(), "WHOIS", ["Mira"]));
        Assert.Throws<ArgumentException>(() => new IrcCommandBuilder().BuildWithTags(new Dictionary<string, string?> { ["label"] = "bad\nvalue" }, "WHOIS", ["Mira"]));
    }
}
