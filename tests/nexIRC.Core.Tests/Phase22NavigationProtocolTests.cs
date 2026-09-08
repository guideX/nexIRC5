using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Core.Tests;

public sealed class Phase22NavigationProtocolTests
{
    [Fact]
    public void AfterUsesConcreteMsgidSelectorAndLoadNewerPurpose()
    {
        var support = Support(ChathistoryReferenceType.MessageId, ChathistoryReferenceType.Timestamp);
        var request = ChathistoryRequest.ForAfter(
            Guid.NewGuid(),
            3,
            "Channel:#room",
            "#room",
            ChathistoryReference.MessageId("n1"),
            50);

        var built = ChathistoryCommandBuilder.BuildValidated(request, support);

        Assert.Equal("CHATHISTORY AFTER #room msgid=n1 50", built.Command.Line);
        Assert.Equal(ChathistoryRequestPurpose.LoadNewer, built.Request.Purpose);
    }

    [Fact]
    public void NavigationPurposePreservesTimestampAroundReference()
    {
        var support = Support(ChathistoryReferenceType.Timestamp);
        var request = new ChathistoryRequest
        {
            NetworkId = Guid.NewGuid(),
            ConnectionGeneration = 2,
            Conversation = "Channel:#room",
            Target = "#room",
            Operation = ChathistoryOperation.Around,
            Reference = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)),
            Limit = 20,
            Purpose = ChathistoryRequestPurpose.NavigateToTimestamp
        };

        Assert.Equal(
            "CHATHISTORY AROUND #room timestamp=2026-09-07T12:00:00.000Z 20",
            ChathistoryCommandBuilder.Build(request, support).Line);
    }

    [Fact]
    public void ContextRowsAreNotCountedAsOrdinaryHistoryMessages()
    {
        var request = ChathistoryRequest.ForAfter(
            Guid.NewGuid(),
            1,
            "Channel:#room",
            "#room",
            ChathistoryReference.MessageId("msg-1"),
            2);
        var ordinary = new IrcPrivmsgEvent(
            IrcMessageParser.Parse("@msgid=msg-2 :alice!u@h PRIVMSG #room :ordinary").Message!,
            "#room",
            "ordinary",
            false)
        {
            IsHistorical = true
        };
        var context = new IrcPrivmsgEvent(
            IrcMessageParser.Parse("@draft/chathistory-context=1;msgid=msg-context :alice!u@h PRIVMSG #room :related").Message!,
            "#room",
            "related",
            false)
        {
            IsHistorical = true
        };

        var result = new ChathistoryResult(1, request, ChathistoryRequestCompletion.Succeeded, [context, ordinary]);

        Assert.True(ChathistoryContext.IsContextRow(context.Message));
        Assert.False(ChathistoryContext.IsContextRow(ordinary.Message));
        Assert.Equal(1, result.MessageCount);
        Assert.Equal(1, result.ContextMessageCount);
    }

    private static ChathistorySupport Support(params ChathistoryReferenceType[] references) => new()
    {
        CapabilityEnabled = true,
        BatchEnabled = true,
        ServerTimeEnabled = true,
        MessageTagsEnabled = true,
        SupportedReferenceTypes = references
    };
}
