using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase1YProtocolTests
{
    [Fact]
    public void BuildsTargetsWithTwoUtcTimestampBoundsAndNoConversationTarget()
    {
        var support = Support();
        var request = ChathistoryRequest.ForTargets(
            Guid.NewGuid(),
            2,
            ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)),
            ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 13, 0, 0, TimeSpan.Zero)),
            40);

        var built = ChathistoryCommandBuilder.BuildValidated(request, support);

        Assert.Equal("CHATHISTORY TARGETS timestamp=2026-09-07T12:00:00.000Z timestamp=2026-09-07T13:00:00.000Z 5", built.Command.Line);
        Assert.Null(built.Request.Target);
        Assert.Null(built.Request.Conversation);
        Assert.Equal(5, built.EffectiveLimit);
    }

    [Fact]
    public void BuildsAroundAndBetweenWithDraftReferencePrefixes()
    {
        var support = Support();
        var around = new ChathistoryRequest
        {
            NetworkId = Guid.NewGuid(),
            ConnectionGeneration = 1,
            Conversation = "Channel:#room",
            Target = "#room",
            Operation = ChathistoryOperation.Around,
            Reference = ChathistoryReference.MessageId("anchor"),
            Limit = 10
        };
        var between = around with
        {
            Operation = ChathistoryOperation.Between,
            Reference = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero)),
            SecondaryReference = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 13, 0, 0, TimeSpan.Zero))
        };

        Assert.Equal("CHATHISTORY AROUND #room msgid=anchor 5", ChathistoryCommandBuilder.Build(around, support).Line);
        Assert.Equal("CHATHISTORY BETWEEN #room timestamp=2026-09-07T12:00:00.000Z timestamp=2026-09-07T13:00:00.000Z 5", ChathistoryCommandBuilder.Build(between, support).Line);
    }

    [Fact]
    public void RejectsMalformedTargetIntervalsAndConversationShapes()
    {
        var support = Support();
        var from = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 13, 0, 0, TimeSpan.Zero));
        var to = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var invalid = ChathistoryRequest.ForTargets(Guid.NewGuid(), 1, from, to, 10);

        Assert.Throws<ArgumentException>(() => ChathistoryCommandBuilder.Build(invalid, support));
        Assert.Throws<ArgumentException>(() => ChathistoryCommandBuilder.Build(invalid with { Conversation = "Channel:#room" }, support));
        Assert.Throws<ArgumentException>(() => ChathistoryCommandBuilder.Build(invalid with { Limit = 0 }, support));
    }

    [Fact]
    public void EventPlaybackIsAnExactDraftCapabilityAndFeatureGate()
    {
        var negotiator = new IrcCapabilityNegotiator(
            [IrcCapabilityCatalog.Chathistory, IrcCapabilityCatalog.EventPlayback, IrcCapabilityCatalog.Batch, IrcCapabilityCatalog.ServerTime, IrcCapabilityCatalog.MessageTags]);
        negotiator.Start();
        negotiator.Handle(Parse(":srv CAP * LS :batch draft/chathistory draft/event-playback server-time message-tags"));
        var snapshot = negotiator.Handle(Parse(":srv CAP * ACK :batch draft/chathistory draft/event-playback server-time message-tags")).Snapshot;

        Assert.True(snapshot.IsEnabled(IrcCapabilityCatalog.EventPlayback));
        Assert.False(snapshot.IsEnabled("event-playback"));
        Assert.True(ServerFeatureSet.Build(snapshot, new ISupportState().Apply(Parse(":srv 005 me CHATHISTORY=50 MSGREFTYPES=msgid,timestamp :supported")), null, ServerIdentity.Unknown).Chathistory.EventPlaybackEnabled);
    }

    private static ChathistorySupport Support() => new()
    {
        CapabilityEnabled = true,
        BatchEnabled = true,
        ServerTimeEnabled = true,
        MessageTagsEnabled = true,
        ServerMaximumRequestSize = 5,
        ClientMaximumRequestSize = 100,
        SupportedReferenceTypes = [ChathistoryReferenceType.MessageId, ChathistoryReferenceType.Timestamp]
    };

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
