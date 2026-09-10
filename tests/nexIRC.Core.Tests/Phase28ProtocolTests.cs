using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class Phase28ProtocolTests
{
    [Fact]
    public void OpaqueServerIdsPreserveCasePunctuationAndOrderingIndependence()
    {
        const string id = "MiXeD:opaque-09._~";
        var first = Parse($"@msgid={id};+reply={id} :bob!u@h PRIVMSG #room :parent");
        var second = Parse($"@+reply={id};msgid={id} :bob!u@h PRIVMSG #room :parent");

        Assert.Equal(id, first.ServerMessageId);
        Assert.Equal(id, first.ReplyParentMessageId);
        Assert.Equal(first.ServerMessageId, second.ServerMessageId);
        Assert.Equal(first.ReplyParentMessageId, second.ReplyParentMessageId);
    }

    [Fact]
    public void DraftMessageIdAliasIsAcceptedWithoutInventingAnIdShape()
    {
        const string id = "server.scope/2026:09:10/ABC";
        var message = Parse($"@draft/msgid={id} :bob!u@h PRIVMSG #room :parent");

        Assert.Equal(id, message.ServerMessageId);
        Assert.Equal(id, IrcMessageIdentity.FindServerMessageId(message.TagValues));
    }

    [Fact]
    public void ReplyReferencesRemainOpaqueAndRejectOnlyUnsafeWireValues()
    {
        const string id = "UPPER.lower:with/slash";

        var reference = IrcReplyReference.Create(id);

        Assert.Equal(id, reference.MessageId);
        Assert.True(IrcReplyReference.TryParse(id, out var parsed));
        Assert.Equal(id, parsed!.MessageId);
        Assert.False(IrcReplyReference.TryParse("has whitespace", out _));
    }

    [Fact]
    public void OptionalServerTagsDoNotChangeTheCanonicalEventIdentity()
    {
        var withoutOptionalTags = Parse("@msgid=opaque-1 :bob!u@h PRIVMSG #room :same");
        var withOptionalTags = Parse("@account=bob;time=2026-09-10T02:00:00.000Z;msgid=opaque-1 :bob!u@h PRIVMSG #room :same");

        Assert.Equal(withoutOptionalTags.ServerMessageId, withOptionalTags.ServerMessageId);
        Assert.Null(withoutOptionalTags.ServerTimestamp);
        Assert.NotNull(withOptionalTags.ServerTimestamp);
        Assert.Equal("bob", withOptionalTags.TagValues["account"]);
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
