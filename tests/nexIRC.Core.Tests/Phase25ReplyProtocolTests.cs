using System.Text;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase25ReplyProtocolTests
{
    [Fact]
    public void ParsesReplyAsOpaqueCaseSensitiveValueAndPreservesTagPresence()
    {
        var message = IrcMessageParser.Parse("@+reply=Parent.ID-7 PRIVMSG #room :answer").Message!;

        Assert.True(message.HasReplyTag);
        Assert.Equal("Parent.ID-7", message.ReplyParentMessageId);
        Assert.Equal("Parent.ID-7", message.ReplyReference!.MessageId);
        Assert.Equal("Parent.ID-7", message.TagValues["+reply"]);
    }

    [Fact]
    public void EmptyOrInvalidReplyValueDoesNotBreakTheMessageParser()
    {
        var empty = IrcMessageParser.Parse("@+reply= PRIVMSG #room :answer").Message!;
        var invalid = IrcMessageParser.Parse("@+reply=bad\\sid PRIVMSG #room :answer").Message!;

        Assert.True(empty.HasReplyTag);
        Assert.Null(empty.ReplyReference);
        Assert.NotNull(empty.ReplyValidationError);
        Assert.Null(invalid.ReplyReference);
        Assert.Equal("PRIVMSG", invalid.Command);
    }

    [Fact]
    public void BuildsReplyWithEscapedOpaqueParentAndCountsUtf8FrameBytes()
    {
        var message = IrcReplyCommandBuilder.Build(
            new IrcCommandBuilder(),
            "#room",
            "résumé",
            "Parent.ID-7");

        Assert.Equal("@+reply=Parent.ID-7 PRIVMSG #room :résumé", message.Line);
        Assert.Equal(message.FramedBytes.Length, Encoding.UTF8.GetByteCount(message.Line) + 2);
    }

    [Fact]
    public void ReplyBuilderRejectsInvalidParentAndOverlongFrame()
    {
        Assert.Throws<ArgumentException>(() => IrcReplyCommandBuilder.Build(new IrcCommandBuilder(), "#room", "body", "bad id"));
        Assert.Throws<InvalidOperationException>(() => IrcReplyCommandBuilder.Build(new IrcCommandBuilder(64), "#room", new string('x', 64), "parent"));
    }

    [Fact]
    public void PreferredCapabilitiesIncludeEchoMessageWithExistingTagDependencies()
    {
        Assert.Contains(IrcCapabilityCatalog.MessageTags, IrcCapabilityCatalog.PreferredPhase1Y);
        Assert.Contains(IrcCapabilityCatalog.EchoMessage, IrcCapabilityCatalog.PreferredPhase1Y);
    }
}
