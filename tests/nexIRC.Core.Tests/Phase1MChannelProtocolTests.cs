using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase1MChannelProtocolTests
{
    [Fact]
    public void TypedChannelCommandsValidateTopicModeParametersAndLength()
    {
        var builder = new IrcCommandBuilder();

        Assert.Equal("TOPIC #room :", IrcChannelCommandBuilder.BuildTopic(builder, "#room", string.Empty).Line);
        Assert.Equal("MODE #room +i", IrcChannelCommandBuilder.BuildFlagMode(builder, "#room", 'i', true).Line);
        Assert.Equal("MODE #room +l 50", IrcChannelCommandBuilder.BuildParameterizedMode(builder, "#room", 'l', true, "50").Line);
        Assert.Throws<ArgumentException>(() => IrcChannelCommandBuilder.BuildTopic(builder, "#room", "bad\r\nvalue"));
        Assert.Throws<ArgumentException>(() => IrcChannelCommandBuilder.BuildParameterizedMode(builder, "#room", 'k', true, "bad key"));
        Assert.Throws<InvalidOperationException>(() => IrcChannelCommandBuilder.BuildTopic(new IrcCommandBuilder(16), "#room", "too long"));
    }

    [Fact]
    public void PrefixAndChanModesRejectMalformedAdvertisementsWithoutThrowing()
    {
        var state = new ISupportState();
        var snapshot = state.Apply(Parse(":srv 005 me PREFIX=(ov)@ CHANMODES=b,k,l,imnpst :bad"));
        Assert.Null(snapshot.Prefix);
        Assert.NotNull(snapshot.ChannelModes);

        snapshot = state.Apply(Parse(":srv 005 me PREFIX=(ov)@+ CHANMODES=b,k,l,imnpst,extra :good prefix"));
        Assert.NotNull(snapshot.Prefix);
        Assert.Null(snapshot.ChannelModes);
    }

    [Fact]
    public void ChannelModeParserSupportsUnknownsMixedSignsAndKeyRemovalWithoutLeakingKey()
    {
        var runtime = new ISupportState().Apply(Parse(":srv 005 me PREFIX=(ov)@+ CHANMODES=beI,k,l,imnpst :supported"));
        var features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, runtime, null, ServerIdentity.Unknown);
        var state = new IrcChannelModeState("#room");

        var changes = state.Apply(Parse(":op!u@h MODE #room +im+l-k 50"), features);
        Assert.Equal([('i', true, null), ('m', true, null), ('l', true, "50"), ('k', false, null)], changes.Select(change => (change.Mode, change.IsAdding, change.Parameter)).ToArray());
        Assert.Contains('i', state.Modes);
        Assert.Contains('m', state.Modes);
        Assert.Contains('l', state.Modes);
        Assert.DoesNotContain('k', state.Modes);

        var key = state.Apply(Parse(":op!u@h MODE #room +k private-key"), features).Single();
        Assert.True(key.IsSensitive);
        Assert.Null(key.SafeParameter);
        Assert.DoesNotContain('k', state.Parameters.Keys);
        Assert.Contains('k', state.Modes);

        state.Apply(Parse(":op!u@h MODE #room -k"), features);
        Assert.DoesNotContain('k', state.Modes);
    }

    [Fact]
    public void ResetChannelModesPreservesMemberPrefixProjection()
    {
        var runtime = new ISupportState().Apply(Parse(":srv 005 me PREFIX=(ov)@+ CHANMODES=b,k,l,imnpst :supported"));
        var features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, runtime, null, ServerIdentity.Unknown);
        var state = new IrcChannelModeState("#room");
        state.Apply(Parse(":op!u@h MODE #room +ov me Alex"), features);
        state.Apply(Parse(":op!u@h MODE #room +nt"), features);

        state.ResetChannelModes();
        Assert.Empty(state.Modes);
        Assert.Contains('o', state.GetMemberModes("me"));
        Assert.Contains('v', state.GetMemberModes("Alex"));
    }

    [Fact]
    public void ListModeProjectionRetainsItsBoundedParameterCap()
    {
        var runtime = new ISupportState().Apply(Parse(":srv 005 me PREFIX=(ov)@+ CHANMODES=b,k,l,imnpst :supported"));
        var features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, runtime, null, ServerIdentity.Unknown);
        var state = new IrcChannelModeState("#room");

        for (var index = 0; index < 600; index++)
        {
            state.Apply(Parse($":op!u@h MODE #room +b *!*@host{index}"), features);
        }

        Assert.Equal(512, state.Parameters['b'].Count);
    }

    private static IrcMessage Parse(string line) => IrcMessageParser.Parse(line).Message!;
}
