using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase50ReadStateProtocolTests
{
    [Fact]
    public void ReadStateFieldsRoundTripWithinBoundsAndRejectMalformedValues()
    {
        const string key = "query-account:account:alice";
        const string messageId = "msg-17";
        var parameters = new[]
        {
            "STATE", "UPDATE", "READ",
            NexIrcReadStateProtocol.EncodeField(key), "s17",
            NexIrcReadStateProtocol.EncodeField(messageId), "4", "2"
        };

        Assert.True(NexIrcReadStateProtocol.TryCreateMarker(parameters, 3, out var marker));
        Assert.Equal(key, marker!.ConversationKey);
        Assert.Equal(17, marker.Sequence);
        Assert.Equal(messageId, marker.MessageId);
        Assert.Equal(4, marker.Revision);
        Assert.Equal(2, marker.SessionEpoch);

        var invalidSequence = parameters.ToArray();
        invalidSequence[4] = "s99999999999999999999999";
        Assert.False(NexIrcReadStateProtocol.TryCreateMarker(invalidSequence, 3, out _));
        var invalidRevision = parameters.ToArray();
        invalidRevision[6] = "0";
        Assert.False(NexIrcReadStateProtocol.TryCreateMarker(invalidRevision, 3, out _));
        Assert.False(NexIrcReadStateProtocol.TryDecodeField("%%%", 128, out _));
        Assert.False(NexIrcReadStateProtocol.TryDecodeField(new string('a', 200), 128, out _));
    }

    [Fact]
    public void ReadStateDiagnosticsRedactPrivateConversationAndMessageIdentity()
    {
        const string line = ":server NEXIRC STATE UPDATE READ cXVlcnktYWNjb3VudDphY2NvdW50OmFsaWNl s17 bXNnLTE3 4 2";
        var redacted = IrcSensitiveData.RedactLine(line);
        Assert.DoesNotContain("cXVlcnktYWNjb3VudDphY2NvdW50OmFsaWNl", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("bXNnLTE3", redacted, StringComparison.Ordinal);
        Assert.Contains("NEXIRC STATE <redacted>", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void ServersWithoutTheOptionalCapabilityRemainUnsupportedForSynchronizedState()
    {
        Assert.False(NexIrcReadStateProtocol.IsSupported(CapabilitySnapshot.Empty));
    }
}
