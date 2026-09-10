using System.Text;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Core.Tests;

public sealed class Phase27ProtocolTests
{
    [Fact]
    public void ServerTagOrderingIsIndependentOfClientTagOrdering()
    {
        var first = Parse("@msgid=event-1;time=2026-09-10T02:00:00.000Z;+reply=parent;+draft/react=👍 :bob!u@h TAGMSG #room");
        var second = Parse("@+draft/react=👍;+reply=parent;time=2026-09-10T02:00:00.000Z;msgid=event-1 :bob!u@h TAGMSG #room");

        Assert.Equal(first.ServerMessageId, second.ServerMessageId);
        Assert.Equal(first.ServerTimestamp, second.ServerTimestamp);
        Assert.Equal(first.ReplyParentMessageId, second.ReplyParentMessageId);
        Assert.Equal(first.Reaction!.Operation, second.Reaction!.Operation);
        Assert.Equal(first.Reaction.Value, second.Reaction.Value);
        Assert.Equal(first.Reaction.ParentMessageId, second.Reaction.ParentMessageId);
        Assert.Equal(first.Reaction.EventMessageId, second.Reaction.EventMessageId);
    }

    [Fact]
    public void ServerAddedTagsCanBeMixedWithClientOnlyTags()
    {
        var message = Parse("@account=bob;msgid=event-2;+reply=parent;+draft/react=😂;batch=history-1 :bob!u@h TAGMSG #room");

        Assert.Equal("bob", message.TagValues["account"]);
        Assert.Equal("event-2", message.ServerMessageId);
        Assert.Equal("history-1", message.BatchId);
        Assert.Equal("parent", message.ReplyParentMessageId);
        Assert.Equal("😂", message.Reaction!.Value);
    }

    [Fact]
    public void EchoMessageCarriesTheCanonicalServerMessageId()
    {
        var echo = Parse("@msgid=echo-1;time=2026-09-10T02:00:01.000Z :me!u@h PRIVMSG #room :parent");

        Assert.Equal("me", echo.Prefix!.Name);
        Assert.Equal("echo-1", echo.ServerMessageId);
        Assert.Equal("parent", echo.TrailingParameter);
    }

    [Fact]
    public void DistinctCanonicalEventsKeepDistinctMessageIds()
    {
        var first = Parse("@msgid=event-a :bob!u@h PRIVMSG #room :same");
        var second = Parse("@msgid=event-b :bob!u@h PRIVMSG #room :same");

        Assert.NotEqual(first.ServerMessageId, second.ServerMessageId);
    }

    [Fact]
    public void TagmsgPrefixAndTargetParsingDoNotRequireATrailingParameter()
    {
        var message = Parse("@msgid=event-3;+reply=parent;+draft/react=👍 :bob!u@h TAGMSG #room");

        Assert.Equal("bob", message.Prefix!.Name);
        Assert.Equal("u", message.Prefix.User);
        Assert.Equal("h", message.Prefix.Host);
        Assert.Single(message.Parameters);
        Assert.Equal("#room", message.Parameters[0]);
    }

    [Fact]
    public void ReactionEchoIsParsedAsAReactionEvent()
    {
        var message = Parse("@msgid=event-4;+reply=parent;+draft/react=👍 :me!u@h TAGMSG #room");

        Assert.Equal(IrcReactionOperation.React, message.Reaction!.Operation);
        Assert.Equal("parent", message.Reaction.ParentMessageId);
        Assert.Equal("event-4", message.Reaction.EventMessageId);
    }

    [Fact]
    public void UnreactionEchoIsParsedAsAnUnreactionEvent()
    {
        var message = Parse("@msgid=event-5;+reply=parent;+draft/unreact=👍 :me!u@h TAGMSG #room");

        Assert.Equal(IrcReactionOperation.Unreact, message.Reaction!.Operation);
        Assert.Equal("👍", message.Reaction.Value);
    }

    [Fact]
    public void ServerTimeAttachedToTagmsgIsAuthoritative()
    {
        var message = Parse("@time=2026-09-10T02:00:02.125Z;+reply=parent;+draft/react=👍 TAGMSG #room");

        Assert.Equal(new DateTimeOffset(2026, 9, 10, 2, 0, 2, 125, TimeSpan.Zero), message.ServerTimestamp);
    }

    [Fact]
    public void AccountTagAttachedToTagmsgIsPreserved()
    {
        var message = Parse("@account=bob-account;+reply=parent;+draft/react=👍 :bob!u@h TAGMSG #room");

        Assert.Equal("bob-account", message.TagValues["account"]);
        Assert.Equal("bob", message.Prefix!.Name);
    }

    [Fact]
    public void EscapedReactionValueRoundTripsThroughTheCommandBuilder()
    {
        var built = IrcReactionCommandBuilder.Build(
            new IrcCommandBuilder(),
            "#room",
            "heart;blue",
            IrcReactionOperation.React,
            "parent");

        var parsed = Parse(built.Line);
        Assert.Equal("heart;blue", parsed.Reaction!.Value);
        Assert.Equal("parent", parsed.Reaction.ParentMessageId);
        Assert.Equal(Encoding.UTF8.GetByteCount(built.Line) + 2, built.FramedBytes.Length);
    }

    [Fact]
    public void DuplicateCanonicalDeliveryRetainsTheSameOpaqueIdentity()
    {
        var first = Parse("@msgid=duplicate-1 :bob!u@h PRIVMSG #room :same");
        var replay = Parse("@msgid=duplicate-1;time=2026-09-10T02:00:03.000Z :bob!u@h PRIVMSG #room :same");

        Assert.Equal(first.ServerMessageId, replay.ServerMessageId);
        Assert.Equal("duplicate-1", IrcMessageIdentity.FindServerMessageId(replay.TagValues));
    }

    [Fact]
    public void ReconnectGenerationIsRetainedByDiagnosticEvents()
    {
        var received = new RawIrcLineEvent(DateTimeOffset.UtcNow, ":srv 001 me :Welcome", Encoding.UTF8.GetBytes(":srv 001 me :Welcome"), 2);
        var sent = new OutboundIrcCommandEvent(DateTimeOffset.UtcNow, "JOIN #room", Encoding.UTF8.GetBytes("JOIN #room\r\n"), 2);

        Assert.Equal(2, received.ConnectionGeneration);
        Assert.Equal(2, sent.ConnectionGeneration);
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
