using System.Text;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase1BProtocolTests
{
    [Fact]
    public void ChannelModesUsePrefixAndChanModesGrammar()
    {
        var runtime = new ISupportState().Apply(Parse(":srv 005 me PREFIX=(qaohv)~&@%+ CHANMODES=b,k,l,imnpst :supported"));
        var features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, runtime, null, ServerIdentity.Unknown);
        var state = new IrcChannelModeState("#room");

        var changes = state.Apply(Parse(":op!u@h MODE #room +ntkl secret 10"), features);
        var prefixChanges = state.Apply(Parse(":op!u@h MODE #room +qa alice bob"), features);
        var banChanges = state.Apply(Parse(":op!u@h MODE #room +b *!*@bad"), features);

        Assert.Collection(
            changes,
            change => Assert.Equal(('n', true, null, IrcChannelModeKind.NoParameter), (change.Mode, change.IsAdding, change.Parameter, change.Kind)),
            change => Assert.Equal(('t', true, null, IrcChannelModeKind.NoParameter), (change.Mode, change.IsAdding, change.Parameter, change.Kind)),
            change => Assert.Equal(('k', true, "secret", IrcChannelModeKind.ParameterAlways), (change.Mode, change.IsAdding, change.Parameter, change.Kind)),
            change => Assert.Equal(('l', true, "10", IrcChannelModeKind.ParameterWhenSet), (change.Mode, change.IsAdding, change.Parameter, change.Kind)));
        Assert.Equal([('q', "alice"), ('a', "bob")], prefixChanges.Select(change => (change.Mode, change.Parameter)).ToArray());
        Assert.Contains("*!*@bad", state.Parameters['b']);
        Assert.Contains('q', state.GetMemberModes("alice"));
        Assert.Contains('a', state.GetMemberModes("bob"));
        Assert.Contains('k', state.Modes);
        Assert.Contains('n', state.Modes);
    }

    [Fact]
    public void AdaptiveModeEngineConsumesAllChanModesAndUnknownLettersSafely()
    {
        var runtime = new ISupportState().Apply(Parse(":srv 005 me PREFIX=(qaohv)~&@%+ CHANMODES=beI,k,l,imnpst :supported"));
        var features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, runtime, null, ServerIdentity.Unknown);
        var state = new IrcChannelModeState("#room");

        var changes = state.Apply(Parse(":op!u@h MODE #room +beI *!*@bad invite-mask exempt-mask"), features);
        var flags = state.Apply(Parse(":op!u@h MODE #room +imn-nt"), features);
        var limit = state.Apply(Parse(":op!u@h MODE #room +l 25"), features);
        state.Apply(Parse(":op!u@h MODE #room +ov alice bob"), features);
        var removals = state.Apply(Parse(":op!u@h MODE #room -ov alice bob"), features);
        var unknown = state.Apply(Parse(":op!u@h MODE #room +zov alice bob"), features);

        Assert.Equal([('b', "*!*@bad"), ('e', "invite-mask"), ('I', "exempt-mask")], changes.Select(change => (change.Mode, change.Parameter)).ToArray());
        Assert.Equal([('i', null), ('m', null), ('n', null), ('n', null), ('t', null)], flags.Select(change => (change.Mode, change.Parameter)).ToArray());
        Assert.Equal(('l', "25"), (limit[0].Mode, limit[0].Parameter));
        Assert.Contains('b', state.Parameters.Keys);
        Assert.Contains('e', state.Parameters.Keys);
        Assert.Contains('I', state.Parameters.Keys);
        Assert.Contains('l', state.Modes);
        Assert.Equal([('o', false, "alice"), ('v', false, "bob")], removals.Select(change => (change.Mode, change.IsAdding, change.Parameter)).ToArray());
        Assert.Equal(IrcChannelModeKind.Unknown, unknown[0].Kind);
        Assert.Equal(('o', "alice"), (unknown[1].Mode, unknown[1].Parameter));
        Assert.Contains('o', state.GetMemberModes("alice"));
        Assert.Contains('v', state.GetMemberModes("bob"));
    }

    [Fact]
    public async Task TranscriptRoundTripsRawBytesAndDirection()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nexirc-{Guid.NewGuid():N}.jsonl");
        try
        {
            var inboundBytes = new byte[] { 0xFF, (byte)'\r', (byte)'\n' };
            var entries = new[]
            {
                IrcTranscriptEntry.FromInbound(DateTimeOffset.UnixEpoch, inboundBytes, 3),
                IrcTranscriptEntry.FromOutbound(DateTimeOffset.UnixEpoch.AddSeconds(1), Encoding.UTF8.GetBytes("PONG :x\r\n"), 3)
            };

            await IrcTranscriptFile.WriteAsync(path, entries);
            var roundTripped = await IrcTranscriptFile.ReadAllAsync(path);

            Assert.Equal(2, roundTripped.Count);
            Assert.Equal(IrcTranscriptDirection.Inbound, roundTripped[0].Direction);
            Assert.Equal(inboundBytes, roundTripped[0].RawBytes.ToArray());
            Assert.Equal("PONG :x", roundTripped[1].RawLine);
            Assert.Equal(3, roundTripped[1].ConnectionGeneration);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SaslPlainProducesOnlyTheExplicitMechanismResponseAndCredentialCanBeDisposed()
    {
        using var credential = new SaslCredential("alice", "secret");
        var response = await new SaslPlainMechanism().CreateInitialResponseAsync(credential);

        Assert.Equal(new byte[] { 0, (byte)'a', (byte)'l', (byte)'i', (byte)'c', (byte)'e', 0, (byte)'s', (byte)'e', (byte)'c', (byte)'r', (byte)'e', (byte)'t' }, response.ToArray());
        Assert.False(credential.IsDisposed);
        credential.Dispose();
        Assert.True(credential.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => credential.CopySecret(new char[8]));
    }

    [Fact]
    public void IrcCaseMappingsHaveTheAdvertisedPunctuationDifferences()
    {
        Assert.True(IrcCaseMappingComparer.Equals("Nick^", "nick~", IrcCaseMapping.Rfc1459));
        Assert.False(IrcCaseMappingComparer.Equals("Nick^", "nick~", IrcCaseMapping.StrictRfc1459));
        Assert.True(IrcCaseMappingComparer.Equals("Chan[", "chan{", IrcCaseMapping.StrictRfc1459));
        Assert.False(IrcCaseMappingComparer.Equals("Chan[", "chan{", IrcCaseMapping.Ascii));
        Assert.Equal("n!ck", IrcCaseMappingComparer.Fold("N!CK", IrcCaseMapping.Ascii));
    }

    [Fact]
    public void CapCanHoldEndUntilACompletionGateFinishes()
    {
        var negotiator = new IrcCapabilityNegotiator(["sasl"], completionGateCapabilities: ["sasl"]);
        negotiator.Start();
        var listing = negotiator.Handle(Parse(":srv CAP * LS :sasl=PLAIN"));
        var acknowledgement = negotiator.Handle(Parse(":srv CAP * ACK :sasl"));

        Assert.Equal("CAP REQ :sasl", listing.Commands.Single().Line);
        Assert.Empty(acknowledgement.Commands);
        Assert.Equal(CapNegotiationState.AwaitingCompletion, acknowledgement.Snapshot.NegotiationState);

        var completed = negotiator.Complete();
        Assert.Equal("CAP END", completed.Commands.Single().Line);
        Assert.Equal(CapNegotiationState.Ended, completed.Snapshot.NegotiationState);
    }

    [Fact]
    public void SensitiveOutboundTranscriptRecordsAreRedactedButRemainReplayable()
    {
        var password = "supplied-password";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0" + password));
        var entry = IrcTranscriptEntry.FromOutbound(
            DateTimeOffset.UnixEpoch,
            Encoding.UTF8.GetBytes($"AUTHENTICATE {encoded}\r\n"),
            1);

        Assert.DoesNotContain(password, entry.RawLine, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, entry.RawLine, StringComparison.Ordinal);
        Assert.Equal("AUTHENTICATE <redacted>", entry.RawLine);
        Assert.Equal("AUTHENTICATE <redacted>\r\n", Encoding.UTF8.GetString(entry.RawBytes.Span));
    }

    [Fact]
    public void SensitiveCommandRedactionHandlesWhitespaceAndTabs()
    {
        Assert.Equal("PASS :<redacted>", IrcSensitiveData.RedactLine("  PASS\tprivate-secret\r\n"));
        Assert.Equal("AUTHENTICATE <redacted>", IrcSensitiveData.RedactLine(" AUTHENTICATE\tprivate-secret"));
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
