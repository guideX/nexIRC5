using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase1VProtocolTests
{
    [Fact]
    public void CapNegotiatesAdvertisedSubsetAndExposesPerCapabilityStatus()
    {
        var negotiator = new IrcCapabilityNegotiator(["server-time", "away-notify", "future-cap"]);

        Assert.Equal("CAP LS 302", negotiator.Start().Commands.Single().Line);
        negotiator.Handle(Parse(":srv CAP * LS :server-time away-notify"));
        var request = negotiator.Handle(Parse(":srv CAP * LS :")).Snapshot;

        Assert.Equal(CapabilityNegotiationStatus.Requested, request.GetStatus("server-time"));
        Assert.Equal(CapabilityNegotiationStatus.Unavailable, request.GetStatus("future-cap"));
        Assert.Equal(["server-time", "away-notify"], request.Available.Keys);

        var ack = negotiator.Handle(Parse(":srv CAP * ACK :server-time")).Snapshot;
        Assert.Equal(CapabilityNegotiationStatus.Enabled, ack.GetStatus("server-time"));
        Assert.Equal(CapabilityNegotiationStatus.Requested, ack.GetStatus("away-notify"));
        Assert.Equal(CapNegotiationState.Ended, ack.NegotiationState);
    }

    [Fact]
    public void CapNakIsRecordedWithoutEnablingOrFailingTheNegotiator()
    {
        var negotiator = new IrcCapabilityNegotiator(["account-tag", "away-notify"]);
        negotiator.Start();
        negotiator.Handle(Parse(":srv CAP * LS :account-tag away-notify"));

        var result = negotiator.Handle(Parse(":srv CAP * NAK :account-tag"));

        Assert.Equal("CAP END", result.Commands.Single().Line);
        Assert.Equal(CapabilityNegotiationStatus.Rejected, result.Snapshot.GetStatus("account-tag"));
        Assert.False(result.Snapshot.IsEnabled("account-tag"));
        Assert.False(result.Snapshot.IsEnabled("away-notify"));
    }

    [Fact]
    public void CapEndIsEmittedOnlyOnceAndResetClearsLiveState()
    {
        var negotiator = new IrcCapabilityNegotiator(["server-time"]);
        negotiator.Start();
        negotiator.Handle(Parse(":srv CAP * LS :server-time"));
        var first = negotiator.Handle(Parse(":srv CAP * ACK :server-time"));
        var second = negotiator.Handle(Parse(":srv CAP * ACK :server-time"));

        Assert.Single(first.Commands);
        Assert.Empty(second.Commands);
        negotiator.Reset();
        Assert.Equal(CapNegotiationState.NotStarted, negotiator.Snapshot.NegotiationState);
        Assert.Empty(negotiator.Snapshot.Available);
        Assert.Empty(negotiator.Snapshot.Enabled);
        Assert.Empty(negotiator.Snapshot.Rejected);
    }

    [Fact]
    public void CapabilityCatalogKeepsPhase1VRequestsBounded()
    {
        Assert.Contains(IrcCapabilityCatalog.ServerTime, IrcCapabilityCatalog.PreferredPhase1V);
        Assert.Contains(IrcCapabilityCatalog.AccountNotify, IrcCapabilityCatalog.Known);
        Assert.DoesNotContain("batch", IrcCapabilityCatalog.PreferredPhase1V);
    }

    [Theory]
    [InlineData("a\\:b", "a;b")]
    [InlineData("a\\sb", "a b")]
    [InlineData("a\\\\b", "a\\b")]
    [InlineData("a\\r\\n", "a\r\n")]
    public void ParsesEachIrcv3TagEscape(string rawValue, string expected)
    {
        var message = Assert.IsType<IrcMessage>(IrcMessageParser.Parse($"@tag={rawValue} PRIVMSG #room :body").Message);

        Assert.Equal(expected, message.TagValues["tag"]);
    }

    [Fact]
    public void TagsPreserveMultipleEmptyValuelessAndUnknownMetadata()
    {
        var message = Assert.IsType<IrcMessage>(IrcMessageParser.Parse("@one=1;empty=;flag;unknown=value PRIVMSG #room :body").Message);

        Assert.Equal(4, message.Tags.Count);
        Assert.Equal("value", message.TagValues["unknown"]);
        Assert.Null(message.TagValues["flag"]);
        Assert.Equal(string.Empty, message.TagValues["empty"]);
    }

    [Theory]
    [InlineData("@tag=bad\\q PRIVMSG #room :body")]
    [InlineData("@tag=bad\\ PRIVMSG #room :body")]
    public void MalformedEscapeIsSafeAndCannotShiftTheCommand(string rawLine)
    {
        var result = IrcMessageParser.Parse(rawLine);

        Assert.True(result.Success);
        Assert.Equal("PRIVMSG", result.Message!.Command);
        Assert.StartsWith("bad\\", result.Message.TagValues["tag"]);
    }

    [Fact]
    public void MalformedTagSegmentsReturnAParseErrorWithoutThrowing()
    {
        var result = IrcMessageParser.Parse("@tag=one;;other=two PRIVMSG #room :body");

        Assert.False(result.Success);
        Assert.Contains("empty tag", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ServerTimeIsStrictlyCachedAndInvalidTimeFallsBack()
    {
        var valid = IrcMessageParser.Parse("@time=2026-09-07T12:34:56.789Z PRIVMSG #room :body").Message!;
        var invalid = IrcMessageParser.Parse("@time=not-a-time PRIVMSG #room :body").Message!;

        Assert.Equal(new DateTimeOffset(2026, 9, 7, 12, 34, 56, 789, TimeSpan.Zero), valid.ServerTimestamp);
        Assert.Null(invalid.ServerTimestamp);
    }

    [Fact]
    public void OrdinaryAndExtendedJoinRemainOneTypedCommandShape()
    {
        var ordinary = IrcMessageParser.Parse(":Nick!u@h JOIN :#room").Message!;
        var extended = IrcMessageParser.Parse(":Nick!u@h JOIN #room account :Real Name With Spaces").Message!;

        Assert.Equal("JOIN", ordinary.Command);
        Assert.Equal(["#room"], ordinary.Parameters);
        Assert.Equal(["#room", "account", "Real Name With Spaces"], extended.Parameters);
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
