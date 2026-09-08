using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase20ProtocolTests
{
    [Fact]
    public void ExactBetweenUsesTypedReferencesAndExcludesBothSelectorsByDraftContract()
    {
        var request = ChathistoryRequest.ForBetween(
            Guid.NewGuid(),
            1,
            "Channel:#room",
            "#room",
            ChathistoryReference.MessageId("older"),
            ChathistoryReference.MessageId("newer"),
            50);
        var support = new ChathistorySupport
        {
            CapabilityEnabled = true,
            BatchEnabled = true,
            ServerTimeEnabled = true,
            MessageTagsEnabled = true,
            SupportedReferenceTypes = [ChathistoryReferenceType.MessageId]
        };

        var built = ChathistoryCommandBuilder.BuildValidated(request, support);

        Assert.Equal("CHATHISTORY BETWEEN #room msgid=older msgid=newer 50", built.Command.Line);
        Assert.True(ChathistoryReference.AreCompatible(request.Reference, request.SecondaryReference!));
    }

    [Fact]
    public void HistoryEndTagIsRetainedAsTypedMessageEvidence()
    {
        var message = IrcMessageParser.Parse("@draft/chathistory-end :srv BATCH +history chathistory #room").Message!;

        Assert.True(message.HasChathistoryEnd);
    }

    [Fact]
    public void GenericBuilderAllowsProtocolValidMixedReferencesButExactPolicyCanRejectThem()
    {
        var request = ChathistoryRequest.ForBetween(
            Guid.NewGuid(),
            1,
            "Channel:#room",
            "#room",
            ChathistoryReference.Timestamp("2026-09-07T12:00:00.000Z"),
            ChathistoryReference.MessageId("newer"),
            10);
        var support = new ChathistorySupport
        {
            CapabilityEnabled = true,
            BatchEnabled = true,
            ServerTimeEnabled = true,
            MessageTagsEnabled = true,
            SupportedReferenceTypes = [ChathistoryReferenceType.Timestamp, ChathistoryReferenceType.MessageId]
        };

        Assert.Equal(
            "CHATHISTORY BETWEEN #room timestamp=2026-09-07T12:00:00.000Z msgid=newer 10",
            ChathistoryCommandBuilder.Build(request, support).Line);
    }
}
