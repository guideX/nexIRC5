using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class Phase1WProtocolTests
{
    [Theory]
    [InlineData("msgid")]
    [InlineData("draft/msgid")]
    public void MessageIdTagsArePreservedAsNeutralTypedIdentity(string tagName)
    {
        var message = IrcMessageParser.Parse($"@{tagName}=abc\\:123 :peer!u@h PRIVMSG #room :hello").Message!;

        Assert.Equal("abc;123", message.ServerMessageId);
        Assert.Equal("abc;123", message.TagValues[tagName]);
    }

    [Fact]
    public void InvalidOrOversizedMessageIdDoesNotBecomeAuthoritativeIdentity()
    {
        var message = IrcMessageParser.Parse($"@msgid={new string('x', 257)} :peer!u@h PRIVMSG #room :hello").Message!;

        Assert.Null(message.ServerMessageId);
        Assert.Equal(257, message.TagValues["msgid"]!.Length);
    }

    [Fact]
    public void BatchAssociationIsPreservedWithoutMakingBatchMetadataConversationIdentity()
    {
        var message = IrcMessageParser.Parse("@batch=history-1;msgid=m-1 :peer!u@h PRIVMSG #room :hello").Message!;

        Assert.Equal("history-1", message.BatchId);
        Assert.Equal("m-1", message.ServerMessageId);
    }
}
