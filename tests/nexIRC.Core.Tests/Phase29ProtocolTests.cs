using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class Phase29ProtocolTests
{
    [Fact]
    public void InspIRCdOpaqueMsgidShapeRemainsAnUnmodifiedRelationshipKey()
    {
        const string parentId = "597~1789175860~2";

        var parent = Parse($"@time=2026-09-13T04:15:41.054Z;msgid={parentId} :alice!u@h PRIVMSG #room :phase29-parent-marker");
        var reply = Parse($"@time=2026-09-13T04:15:41.477Z;msgid=597~1789175860~3;inspircd.org/echo;+reply={parentId} :bob!u@h PRIVMSG #room :phase29-reply-marker");

        Assert.Equal(parentId, parent.ServerMessageId);
        Assert.Equal(parentId, reply.ReplyParentMessageId);
        Assert.Equal("597~1789175860~3", reply.ServerMessageId);
    }

    [Fact]
    public void InspIRCdRelayedReactionUsesServerAddedTagsAndTrailingTarget()
    {
        var reaction = Parse("@time=2026-09-13T04:15:41.680Z;msgid=597~1789175860~4;inspircd.org/echo;+reply=597~1789175860~2;+draft/react=👍 :bob!u@h TAGMSG :#room");

        Assert.Equal("TAGMSG", reaction.Command);
        Assert.Single(reaction.Parameters);
        Assert.Equal("#room", reaction.Parameters[0]);
        Assert.Equal("597~1789175860~2", reaction.ReplyParentMessageId);
        Assert.Equal("👍", reaction.Reaction!.Value);
        Assert.Equal("597~1789175860~4", reaction.Reaction.EventMessageId);
    }

    [Fact]
    public void InspIRCdUnreactionPreservesOpaqueParentAndActorEvidence()
    {
        var unreaction = Parse("@time=2026-09-13T04:15:41.881Z;msgid=597~1789175860~5;+reply=597~1789175860~2;+draft/unreact=👍 :bob!u@h TAGMSG :#room");

        Assert.Equal(IrcReactionOperation.Unreact, unreaction.Reaction!.Operation);
        Assert.Equal("597~1789175860~2", unreaction.Reaction.ParentMessageId);
        Assert.Equal("bob", unreaction.Prefix!.Name);
        Assert.Equal("👍", unreaction.Reaction.Value);
    }

    [Fact]
    public void InspIRCdServerTagOrderDoesNotChangeReactionSemantics()
    {
        const string first = "@time=2026-09-13T04:15:41.680Z;msgid=597~1789175860~4;+reply=597~1789175860~2;+draft/react=👍 :bob!u@h TAGMSG :#room";
        const string second = "@+draft/react=👍;+reply=597~1789175860~2;msgid=597~1789175860~4;time=2026-09-13T04:15:41.680Z :bob!u@h TAGMSG :#room";

        var firstMessage = Parse(first);
        var secondMessage = Parse(second);

        Assert.Equal(firstMessage.ServerMessageId, secondMessage.ServerMessageId);
        Assert.Equal(firstMessage.ReplyParentMessageId, secondMessage.ReplyParentMessageId);
        Assert.Equal(firstMessage.Reaction!.Operation, secondMessage.Reaction!.Operation);
        Assert.Equal(firstMessage.Reaction.Value, secondMessage.Reaction.Value);
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
