using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class AdaptiveProtocolTests
{
    [Fact]
    public void CapNegotiatesMultilineListingAndAck()
    {
        var negotiator = new IrcCapabilityNegotiator(["multi-prefix", "server-time", "message-tags"]);

        Assert.Equal("CAP LS 302", negotiator.Start().Commands.Single().Line);
        var first = negotiator.Handle(Parse(":srv CAP * LS * :multi-prefix server-time=1"));
        var second = negotiator.Handle(Parse(":srv CAP * LS :message-tags account-tag"));

        Assert.Empty(first.Commands);
        Assert.Equal("CAP REQ :multi-prefix server-time message-tags", second.Commands.Single().Line);
        var ack = negotiator.Handle(Parse(":srv CAP * ACK :multi-prefix server-time message-tags"));

        Assert.Equal("CAP END", ack.Commands.Single().Line);
        Assert.True(ack.Snapshot.IsAvailable("SERVER-TIME"));
        Assert.True(ack.Snapshot.IsEnabled("message-tags"));
        Assert.Equal(CapNegotiationState.Ended, ack.Snapshot.NegotiationState);
        Assert.Contains("server-time=1", ack.Snapshot.RawAdvertisedTokens);
    }

    [Fact]
    public void CapNakEndsNegotiationWithoutEnablingCapability()
    {
        var negotiator = new IrcCapabilityNegotiator(["sasl"]);
        negotiator.Start();
        negotiator.Handle(Parse(":srv CAP * LS :sasl=PLAIN"));

        var result = negotiator.Handle(Parse(":srv CAP * NAK :sasl"));

        Assert.Equal("CAP END", result.Commands.Single().Line);
        Assert.False(result.Snapshot.IsEnabled("sasl"));
    }

    [Fact]
    public void CapNewAndDelUpdateNormalizedState()
    {
        var negotiator = new IrcCapabilityNegotiator();
        negotiator.Start();

        var added = negotiator.Handle(Parse(":srv CAP * NEW :draft/example=one"));
        var removed = negotiator.Handle(Parse(":srv CAP * DEL :draft/example"));

        Assert.True(added.Snapshot.IsAvailable("DRAFT/EXAMPLE"));
        Assert.False(removed.Snapshot.IsAvailable("draft/example"));
    }

    [Fact]
    public void ParsesTypedISupportAndPreservesUnknownTokensAndRemoval()
    {
        var state = new ISupportState();
        var snapshot = state.Apply(Parse(":srv 005 nick NETWORK=Example CASEMAPPING=rfc1459 CHANTYPES=#& PREFIX=(qaohv)~&@%+ CHANMODES=b,k,l,imnpst STATUSMSG=@+ EXCEPTS INVEX LINELEN=600 MAXLIST=beI:25 TARGMAX=NICK:1,PRIVMSG:4 UTF8ONLY FOO=bar :are supported"));

        Assert.Equal("Example", snapshot.NetworkName);
        Assert.Equal(IrcCaseMapping.Rfc1459, snapshot.CaseMapping);
        Assert.Equal(new HashSet<char>("#&"), snapshot.ChannelTypes);
        Assert.Equal("qaohv", new string(snapshot.Prefix!.Modes.ToArray()));
        Assert.Equal("~&@%+", new string(snapshot.Prefix.Prefixes.ToArray()));
        Assert.Equal('@', snapshot.Prefix.ModeToPrefix['o']);
        Assert.Contains('b', snapshot.ChannelModes!.ListModes);
        Assert.Contains('k', snapshot.ChannelModes.ParameterAlwaysModes);
        Assert.Contains('l', snapshot.ChannelModes.ParameterWhenSetModes);
        Assert.Contains('i', snapshot.ChannelModes.NoParameterModes);
        Assert.Equal("@+", snapshot.StatusMessagePrefixes);
        Assert.True(snapshot.SupportsExcepts);
        Assert.True(snapshot.SupportsInvex);
        Assert.Equal(600, snapshot.LineLength);
        Assert.Equal(25, snapshot.MaxList['b']);
        Assert.Equal(4, snapshot.TargetMax["PRIVMSG"]);
        Assert.True(snapshot.Utf8Only);
        Assert.Equal("bar", snapshot.Tokens["FOO"].Value);

        var removed = state.Apply(Parse(":srv 005 nick -EXCEPTS -FOO :are supported"));
        Assert.False(removed.SupportsExcepts);
        Assert.Contains("EXCEPTS", removed.RemovedTokens);
        Assert.Contains("FOO", removed.RemovedTokens);
        Assert.Equal("bar", removed.RawTokens.First(token => token.Name == "FOO").Value);
    }

    [Fact]
    public void UnknownServerRemainsUnknownWithoutStrongEvidence()
    {
        var detector = new ServerIdentityDetector();

        detector.ObserveHostname("irc.example.test");

        Assert.Equal(IrcdFamily.Unknown, detector.Snapshot.ProbableIrcd);
        Assert.Equal(0, detector.Snapshot.Confidence);
    }

    [Fact]
    public void Strong004EvidenceWinsOverWeakConflictingHostnameEvidence()
    {
        var detector = new ServerIdentityDetector();

        detector.ObserveHostname("inspircd.example.test");
        detector.ObserveMyInfo(Parse(":srv 004 nick UnrealIRCd-6.1 +iwx +nt"));

        Assert.Equal(IrcdFamily.UnrealIRCd, detector.Snapshot.ProbableIrcd);
        Assert.True(detector.Snapshot.Confidence >= 0.9);
    }

    [Fact]
    public void NetworkIdentityAndIrcdIdentityAreIndependent()
    {
        var detector = new ServerIdentityDetector();
        detector.ObserveISupport(new ISupportState().Apply(Parse(":srv 005 nick NETWORK=ExampleNet :supported")));
        detector.ObserveMyInfo(Parse(":srv 004 nick Solanum-1.0 +iwx +nt"));

        Assert.Equal("ExampleNet", detector.Snapshot.NetworkName);
        Assert.Equal(IrcdFamily.Solanum, detector.Snapshot.ProbableIrcd);
    }

    [Fact]
    public void RuntimeISupportOverridesProfileHints()
    {
        var runtime = new ISupportState().Apply(Parse(":srv 005 nick CHANTYPES=$ LINELEN=700 :supported"));
        var profile = new ServerProfileSet(Network: new NetworkProfile("Example", Hints: new ServerFeatureHints(new HashSet<char>(['!']), LineLength: 400)));

        var features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, runtime, profile, ServerIdentity.Unknown);

        Assert.Equal(new HashSet<char>(['$']), features.ChannelTypes);
        Assert.Equal(700, features.LineLength);
        Assert.Equal("Example", features.NetworkName);
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
