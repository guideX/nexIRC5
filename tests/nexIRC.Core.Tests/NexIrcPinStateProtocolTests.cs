using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class NexIrcPinStateProtocolTests
{
    [Fact]
    public void PinsRequireTheirOwnCapabilityAlongsideStateAndResumeV1()
    {
        var pinsOnly = Negotiate("nexirc/pins=1 nexirc/state=1 nexirc/resume=1", "nexirc/pins");
        Assert.False(NexIrcPinStateProtocol.IsSupported(pinsOnly));

        var supported = Negotiate("nexirc/pins=1 nexirc/state=1 nexirc/resume=1",
            "nexirc/pins nexirc/state nexirc/resume");
        Assert.True(NexIrcPinStateProtocol.IsSupported(supported));

        var wrongVersion = Negotiate("nexirc/pins=2 nexirc/state=1 nexirc/resume=1",
            "nexirc/pins nexirc/state nexirc/resume");
        Assert.False(NexIrcPinStateProtocol.IsSupported(wrongVersion));

        var unspecifiedVersion = Negotiate("nexirc/pins nexirc/state=1 nexirc/resume=1",
            "nexirc/pins nexirc/state nexirc/resume");
        Assert.False(NexIrcPinStateProtocol.IsSupported(unspecifiedVersion));
    }

    [Fact]
    public void ChannelKeysFoldCasemappingAndIncludeNetworkScope()
    {
        var rfcUpper = NexIrcPinStateProtocol.BuildChannelKey("network-a", "#Dev{", IrcCaseMapping.Rfc1459);
        var rfcLower = NexIrcPinStateProtocol.BuildChannelKey("network-a", "#dev[", IrcCaseMapping.Rfc1459);
        var otherNetwork = NexIrcPinStateProtocol.BuildChannelKey("network-b", "#dev[", IrcCaseMapping.Rfc1459);

        Assert.Equal(rfcUpper, rfcLower);
        Assert.NotEqual(rfcUpper, otherNetwork);
        Assert.True(NexIrcPinStateProtocol.IsValidConversationKey(rfcUpper));
    }

    [Fact]
    public void AccountQueryKeysUseStablePeerIdentityAndStrictFields()
    {
        var firstNickKey = NexIrcPinStateProtocol.BuildAccountQueryKey("network-a", "bob-account");
        var changedNickKey = NexIrcPinStateProtocol.BuildAccountQueryKey("network-a", "bob-account");
        var reusedNickKey = NexIrcPinStateProtocol.BuildAccountQueryKey("network-a", "charlie-account");

        Assert.Equal(firstNickKey, changedNickKey);
        Assert.NotEqual(firstNickKey, reusedNickKey);
        Assert.True(NexIrcPinStateProtocol.IsValidConversationKey(firstNickKey));
        Assert.False(NexIrcPinStateProtocol.IsValidConversationKey("query-account:bob-account"));
        Assert.False(NexIrcPinStateProtocol.IsValidConversationKey("channel:v1:@@:eA"));
        Assert.False(NexIrcPinStateProtocol.TryDecodeField("YQ==", 8, out _));
    }

    [Fact]
    public void PinEntriesRejectMalformedRevisionsStateAndOrdering()
    {
        var key = NexIrcPinStateProtocol.BuildChannelKey("network-a", "#alpha", IrcCaseMapping.Rfc1459);
        var valid = new[] { "PINS", "ENTRY", NexIrcPinStateProtocol.EncodeField(key), "2", "1", "5",
            NexIrcPinStateProtocol.EncodeField("#alpha") };
        Assert.True(NexIrcPinStateProtocol.TryCreatePin(valid, 2, out var entry));
        Assert.Equal(new NexIrcConversationPin(key, "#alpha", 2, true, 5), entry);

        Assert.False(NexIrcPinStateProtocol.TryCreatePin([.. valid[..3], "0", .. valid[4..]], 2, out _));
        Assert.False(NexIrcPinStateProtocol.TryCreatePin([.. valid[..4], "2", .. valid[5..]], 2, out _));
        Assert.False(NexIrcPinStateProtocol.TryCreatePin([.. valid[..5], "0", .. valid[6..]], 2, out _));
        Assert.False(NexIrcPinStateProtocol.IsValidMutationId("not-a-mutation-id"));
    }

    [Fact]
    public void PinProtocolDiagnosticsNeverExposeConversationKeysOrLabels()
    {
        const string sensitive = "NEXIRC PINS SET PIN 1 private-key private-label";
        var redacted = IrcSensitiveData.RedactLine(sensitive);

        Assert.DoesNotContain("private-key", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("private-label", redacted, StringComparison.Ordinal);
        Assert.Contains("<redacted>", redacted, StringComparison.Ordinal);
        var parsed = Assert.IsType<IrcMessage>(IrcMessageParser.Parse(sensitive).Message);
        var safe = IrcSensitiveData.RedactParsedMessage(parsed);
        Assert.DoesNotContain("private-key", safe.RawLine, StringComparison.Ordinal);
    }

    private static CapabilitySnapshot Negotiate(string advertised, string acknowledged)
    {
        var capabilities = new IrcCapabilityNegotiator(["nexirc/pins", "nexirc/state", "nexirc/resume"]);
        capabilities.Start();
        capabilities.Handle(IrcMessageParser.Parse($":srv CAP * LS :{advertised}").Message!);
        capabilities.Handle(IrcMessageParser.Parse($":srv CAP * ACK :{acknowledged}").Message!);
        return capabilities.Snapshot;
    }
}
