using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase1XProtocolTests
{
    [Fact]
    public void ParsesChathistoryIsupportAndPreservesPreferenceOrder()
    {
        var state = new ISupportState();
        var snapshot = state.Apply(Parse(":srv 005 me CHATHISTORY=50 MSGREFTYPES=timestamp,msgid :supported"));

        Assert.True(snapshot.HasChathistory);
        Assert.Equal(50, snapshot.ChathistoryLimit);
        Assert.Equal([ChathistoryReferenceType.Timestamp, ChathistoryReferenceType.MessageId], snapshot.MessageReferenceTypes);
    }

    [Fact]
    public void ZeroAndMalformedChathistoryLimitsAreSafe()
    {
        var zero = new ISupportState().Apply(Parse(":srv 005 me CHATHISTORY=0 :supported"));
        var malformed = new ISupportState().Apply(Parse(":srv 005 me CHATHISTORY=not-a-number :supported"));

        Assert.Equal(0, zero.ChathistoryLimit);
        Assert.Null(malformed.ChathistoryLimit);
        Assert.True(malformed.HasChathistory);
    }

    [Fact]
    public void UnknownReferenceTypesDoNotBreakKnownTypes()
    {
        var snapshot = new ISupportState().Apply(Parse(":srv 005 me MSGREFTYPES=unknown,msgid,timestamp,unknown :supported"));

        Assert.Equal([ChathistoryReferenceType.MessageId, ChathistoryReferenceType.Timestamp], snapshot.MessageReferenceTypes);
    }

    [Fact]
    public void SupportRequiresTheSafeCapabilitySubstrateAndClampsUnlimitedServerLimit()
    {
        var negotiator = new IrcCapabilityNegotiator([IrcCapabilityCatalog.Chathistory, IrcCapabilityCatalog.Batch, IrcCapabilityCatalog.ServerTime, IrcCapabilityCatalog.MessageTags], completionGateCapabilities: []);
        negotiator.Start();
        negotiator.Handle(Parse(":srv CAP * LS :batch draft/chathistory server-time message-tags"));
        var snapshot = negotiator.Handle(Parse(":srv CAP * ACK :batch draft/chathistory server-time message-tags")).Snapshot;
        var isupport = new ISupportState().Apply(Parse(":srv 005 me CHATHISTORY=0 MSGREFTYPES=msgid :supported"));

        var support = ServerFeatureSet.Build(snapshot, isupport, null, ServerIdentity.Unknown, clientMaximumChathistoryRequestSize: 37).Chathistory;

        Assert.True(support.IsUsable);
        Assert.Equal(37, support.EffectiveMaximumRequestSize);
        Assert.Equal(ChathistoryReferenceType.MessageId, support.PreferredReferenceType);
    }

    [Fact]
    public void BuildsBoundedLatestAndBeforeCommandsWithoutRawEscapeHatch()
    {
        var support = new ChathistorySupport
        {
            CapabilityEnabled = true,
            BatchEnabled = true,
            ServerTimeEnabled = true,
            MessageTagsEnabled = true,
            ServerMaximumRequestSize = 5,
            ClientMaximumRequestSize = 100,
            SupportedReferenceTypes = [ChathistoryReferenceType.MessageId, ChathistoryReferenceType.Timestamp]
        };
        var latest = new ChathistoryRequest
        {
            NetworkId = Guid.NewGuid(),
            ConnectionGeneration = 1,
            Conversation = "Channel:#room",
            Target = "#room",
            Operation = ChathistoryOperation.Latest,
            Reference = ChathistoryReference.Wildcard,
            Limit = 50
        };
        var before = latest with
        {
            Operation = ChathistoryOperation.Before,
            Reference = ChathistoryReference.MessageId("abc")
        };

        Assert.Equal("CHATHISTORY LATEST #room * 5", ChathistoryCommandBuilder.Build(latest, support).Line);
        Assert.Equal("CHATHISTORY BEFORE #room abc 5", ChathistoryCommandBuilder.Build(before, support).Line);
        Assert.Throws<ArgumentException>(() => ChathistoryCommandBuilder.Build(latest with { Target = "#bad target" }, support));
        Assert.Throws<ArgumentOutOfRangeException>(() => ChathistoryCommandBuilder.Build(latest with { Limit = 0 }, support));
    }

    [Fact]
    public void TimestampReferenceNeverUsesLocalFormatting()
    {
        var reference = ChathistoryReference.Timestamp(new DateTimeOffset(2026, 9, 7, 12, 34, 56, TimeSpan.Zero));

        Assert.Equal("2026-09-07T12:34:56.000Z", reference.Serialize());
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
