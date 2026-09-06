using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1MChannelTests
{
    [Fact]
    public async Task AuthorityIsNetworkQualifiedAndTargetRelative()
    {
        var factory = new FakeIrcTransportFactory();
        var alphaTransport = new FakeIrcTransport(new IrcEndpoint("alpha.test", 6667, false));
        var betaTransport = new FakeIrcTransport(new IrcEndpoint("beta.test", 6667, false));
        factory.Add(alphaTransport);
        factory.Add(betaTransport);
        await using var manager = new NetworkSessionManager(factory);
        var alpha = manager.Add(Options("AlphaNet", alphaTransport.Endpoint, "alpha", "#general"));
        var beta = manager.Add(Options("BetaNet", betaTransport.Endpoint, "beta", "#general"));
        await manager.ConnectAsync(alpha.Id);
        await manager.ConnectAsync(beta.Id);
        await WaitForAsync(() => alphaTransport.ConnectCount == 1 && betaTransport.ConnectCount == 1);

        Register(alphaTransport, "alpha", "(qaohv)~&@%+", "beI,k,l,imnpst");
        Register(betaTransport, "beta", "(ov)@+", "be,k,s,im");
        Seed(alphaTransport, "alpha", "(qaohv)~&@%+", "@alpha +Alex");
        Seed(betaTransport, "beta", "(ov)@+", "+beta Alex");
        await WaitForAsync(() => alpha.Snapshot.Channels.Any(item => item.Name == "#general" && item.Members.Keys.Contains("Alex"))
            && beta.Snapshot.Channels.Any(item => item.Name == "#general" && item.Members.Keys.Contains("Alex")));

        var alphaChannel = alpha.Channels.Single();
        var betaChannel = beta.Channels.Single();
        var alphaAlex = alphaChannel.Members.Single(member => member.Nickname == "Alex");
        var betaAlex = betaChannel.Members.Single(member => member.Nickname == "Alex");
        var alphaAuthority = ChannelAuthority.Evaluate(alpha, alphaChannel, alphaAlex);
        var betaAuthority = ChannelAuthority.Evaluate(beta, betaChannel, betaAlex);

        Assert.Equal(alpha.Snapshot.ConnectionGeneration, alphaAuthority.ConnectionGeneration);
        Assert.Equal(ChannelAuthorityCertainty.Allowed, alphaAuthority.Kick.Certainty);
        Assert.Equal(ChannelAuthorityCertainty.Denied, betaAuthority.Kick.Certainty);
        Assert.Contains('q', alpha.Snapshot.Features.Prefix!.Modes);
        Assert.DoesNotContain('q', beta.Snapshot.Features.Prefix!.Modes);
        Assert.NotEqual(alpha.Snapshot.Features.ChannelModes!.RawValue, beta.Snapshot.Features.ChannelModes!.RawValue);
        Assert.True(ParticipantActionCatalog.Build(new ParticipantActionContext(alpha, alphaChannel, alphaAlex), false)
            .Single(group => group.Header == "Moderation").Items.All(item => item.IsEnabled));
        Assert.True(ParticipantActionCatalog.Build(new ParticipantActionContext(beta, betaChannel, betaAlex), false)
            .Single(group => group.Header == "Moderation").Items.All(item => !item.IsEnabled));
    }

    [Fact]
    public async Task ChannelPropertiesReconcileModesTopicsAndMetadataWithoutOptimism()
    {
        var endpoint = new IrcEndpoint("channel-properties.test", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("AlphaNet", endpoint, "me", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me", "(qaohv)~&@%+", "beI,k,l,imnpst");
        transport.EnqueueInboundLine(":me!u@h JOIN #room");
        transport.EnqueueInboundLine(":srv 332 me #room :Initial topic");
        transport.EnqueueInboundLine(":srv 333 me #room setter 1700000000");
        transport.EnqueueInboundLine(":srv 324 me #room +ntlk 50 private-key");
        transport.EnqueueInboundLine(":srv 353 me = #room :@me +Alex");
        transport.EnqueueInboundLine(":srv 366 me #room :End");
        await WaitForAsync(() => network.Channels.Single().Synchronization == ChannelSynchronizationState.Synchronized);

        var channel = network.Channels.Single();
        Assert.Equal("Initial topic", channel.Topic);
        Assert.Equal("setter", channel.TopicSetter);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), channel.TopicSetAt);
        Assert.Contains('n', channel.Modes);
        Assert.Contains('t', channel.Modes);
        Assert.Contains('k', channel.Modes);
        Assert.Equal("set (hidden)", channel.ModeProjections.Single(mode => mode.Mode == 'k').ParameterText);
        Assert.Equal("50", channel.ModeProjections.Single(mode => mode.Mode == 'l').ParameterText);

        var properties = new ChannelPropertiesViewModel(manager, network, channel);
        var mode = await properties.SetFlagModeAsync('i', true);
        Assert.True(mode.Succeeded);
        Assert.DoesNotContain('i', channel.Modes);
        await WaitForAsync(() => transport.OutboundLines.Contains("MODE #room +i"));
        transport.EnqueueInboundLine(":srv MODE #room +i");
        await WaitForAsync(() => channel.Modes.Contains('i'));
        Assert.True(manager.TryGetOperation(mode.Operation!.Id, out var confirmedMode));
        Assert.Equal(IrcOperationState.Confirmed, confirmedMode!.State);

        var invalidLimit = await properties.SetParameterizedModeAsync('l', true, "not-a-number");
        Assert.False(invalidLimit.Succeeded);

        properties.TopicDraft = "Changed topic";
        var topic = await properties.SaveTopicAsync();
        Assert.True(topic.Succeeded);
        Assert.Equal("Initial topic", channel.Topic);
        transport.EnqueueInboundLine(":me!u@h TOPIC #room :Changed topic");
        await WaitForAsync(() => channel.Topic == "Changed topic");
        Assert.True(manager.TryGetOperation(topic.Operation!.Id, out var confirmedTopic));
        Assert.Equal(IrcOperationState.Confirmed, confirmedTopic!.State);

        properties.TopicDraft = "bad\r\nvalue";
        var rejected = await properties.SaveTopicAsync();
        Assert.False(rejected.Succeeded);
        transport.EnqueueInboundLine(":srv 331 me #room :No topic");
        await WaitForAsync(() => channel.Topic is null);
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, string channel) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1M test",
        DesiredChannels = new HashSet<string>([channel], StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname, string prefix, string chanModes)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine($":srv 005 {nickname} PREFIX={prefix} CHANMODES={chanModes} :features");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static void Seed(FakeIrcTransport transport, string nickname, string prefix, string names)
    {
        transport.EnqueueInboundLine($":{nickname}!u@h JOIN #general");
        transport.EnqueueInboundLine($":srv 353 {nickname} = #general :{names}");
        transport.EnqueueInboundLine($":srv 366 {nickname} #general :End");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1M test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
