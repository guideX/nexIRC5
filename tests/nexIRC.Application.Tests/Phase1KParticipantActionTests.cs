using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1KParticipantActionTests
{
    [Fact]
    public async Task ParticipantActionsRouteToTheirNetworkAndReuseQualifiedQueries()
    {
        var factory = new FakeIrcTransportFactory();
        var transportA = new FakeIrcTransport(new IrcEndpoint("a.example", 6667, false));
        var transportB = new FakeIrcTransport(new IrcEndpoint("b.example", 6667, false));
        factory.Add(transportA);
        factory.Add(transportB);
        var configuration = new ConfigurationService(new InMemoryConfigurationStore());
        await using var manager = new NetworkSessionManager(factory, configuration: configuration);
        var networkA = manager.Add(Options("Alpha", transportA.Endpoint, "operatorA", "#general", profileId: Guid.NewGuid()));
        var networkB = manager.Add(Options("Beta", transportB.Endpoint, "operatorB", "#general", profileId: Guid.NewGuid()));
        await manager.ConnectAsync(networkA.Id);
        await manager.ConnectAsync(networkB.Id);
        await WaitForAsync(() => transportA.ConnectCount == 1 && transportB.ConnectCount == 1);
        Register(transportA, "a", "operatorA", "Alpha");
        Register(transportB, "b", "operatorB", "Beta");
        SeedMemberState(transportA, "operatorA", "Alex", "alpha.example", "@");
        SeedMemberState(transportB, "operatorB", "Alex", "beta.example", "@");
        await WaitForAsync(() => networkA.Channels.Single().IsJoined && networkB.Channels.Single().Members.Any(member => member.Nickname == "Alex"));

        var service = new ParticipantActionService(manager);
        var contextA = new ParticipantActionContext(networkA, networkA.Channels.Single(), networkA.Channels.Single().Members.Single(member => member.Nickname == "Alex"), networkA.Channels);
        var contextB = new ParticipantActionContext(networkB, networkB.Channels.Single(), networkB.Channels.Single().Members.Single(member => member.Nickname == "Alex"), networkB.Channels);
        var outboundBeforeQuery = transportA.OutboundLines.Count;
        var queryA = service.OpenQuery(contextA);
        var queryAAgain = service.OpenQuery(contextA);
        var queryB = service.OpenQuery(contextB);

        Assert.Same(queryA, queryAAgain);
        Assert.NotSame(queryA, queryB);
        Assert.Equal(networkA.Id, queryA.NetworkId);
        Assert.Equal(networkB.Id, queryB.NetworkId);
        Assert.Equal(outboundBeforeQuery, transportA.OutboundLines.Count);

        var beforeInvalidKickBan = transportA.OutboundLines.Count;
        var invalidKickBan = await service.KickAndBanAsync(contextA, "bad mask", "cleanup");
        Assert.False(invalidKickBan.Succeeded);
        Assert.Equal(beforeInvalidKickBan, transportA.OutboundLines.Count);

        Assert.True((await service.SetPrivilegeAsync(contextA, 'o', true)).Succeeded);
        Assert.True((await service.SendNoticeAsync(contextA, "hello")).Succeeded);
        Assert.True((await service.SendCtcpAsync(contextA, "PING")).Succeeded);
        Assert.True((await service.KickAndBanAsync(contextA, "*!alex@alpha.example", "cleanup")).Succeeded);
        await WaitForAsync(() => transportA.OutboundLines.Contains("MODE #general +o Alex")
            && transportA.OutboundLines.Contains("NOTICE Alex :hello")
            && transportA.OutboundLines.Contains("PRIVMSG Alex :\u0001PING\u0001")
            && transportA.OutboundLines.Contains("MODE #general +b *!alex@alpha.example")
            && transportA.OutboundLines.Contains("KICK #general Alex :cleanup"));

        var banIndex = transportA.OutboundLines.ToList().IndexOf("MODE #general +b *!alex@alpha.example");
        var kickIndex = transportA.OutboundLines.ToList().IndexOf("KICK #general Alex :cleanup");
        Assert.True(banIndex >= 0 && kickIndex > banIndex);
        Assert.DoesNotContain("MODE #general +o Alex", transportB.OutboundLines);
    }

    [Fact]
    public async Task CatalogUsesArbitraryPrefixModesAndConservativeEnablement()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("custom.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Custom", transport.Endpoint, "operator", "#general"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "operator", "Custom", "(xohv)%@+~");
        transport.EnqueueInboundLine(":operator!u@local JOIN #general");
        transport.EnqueueInboundLine(":srv 353 operator = #general :%operator Alex");
        transport.EnqueueInboundLine(":srv 366 operator #general :End");
        await WaitForAsync(() => network.Channels.Single().Members.Any(member => member.Nickname == "Alex"));

        var channel = network.Channels.Single();
        var member = channel.Members.Single(member => member.Nickname == "Alex");
        var context = new ParticipantActionContext(network, channel, member, network.Channels);
        var groups = ParticipantActionCatalog.Build(context, false);
        var privileges = Assert.Single(groups, group => group.Header == "Channel Privileges");
        Assert.Contains(privileges.Items, item => item.ModeLetter == 'x' && item.Header.Contains("Mode x", StringComparison.Ordinal));
        Assert.Contains(privileges.Items, item => item.ModeLetter == 'o' && item.Header.StartsWith("Give", StringComparison.Ordinal));

        await manager.DisconnectAsync(network.Id);
        await WaitForAsync(() => network.State is NetworkDisplayState.Disconnected or NetworkDisplayState.Failed);
        var disconnectedItems = ParticipantActionCatalog.Build(context, false).SelectMany(group => group.Items).ToArray();
        Assert.False(disconnectedItems.Single(item => item.Action == ParticipantActionKind.Whois).IsEnabled);
        Assert.False(disconnectedItems.Single(item => item.Action == ParticipantActionKind.Notice).IsEnabled);
    }

    [Fact]
    public void MentionNeverSendsAndPreservesDraft()
    {
        Assert.Equal("Alex: ", ParticipantMention.Insert(string.Empty, "Alex", true));
        Assert.Equal("draft Alex", ParticipantMention.Insert("draft ", "Alex", true));
        Assert.Equal("Alex", ParticipantMention.Insert(string.Empty, "Alex", false));
    }

    [Fact]
    public async Task IgnoreSuppressesPresentationButNotMembershipState()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("ignore.example", 6667, false));
        factory.Add(transport);
        var configuration = new ConfigurationService(new InMemoryConfigurationStore());
        await using var manager = new NetworkSessionManager(factory, configuration: configuration);
        var network = manager.Add(Options("Ignore", transport.Endpoint, "operator", "#general", profileId: Guid.NewGuid()));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "operator");
        SeedMemberState(transport, "operator", "Alex", "host.example", "@");
        await WaitForAsync(() => network.Channels.Single().Members.Any(member => member.Nickname == "Alex"));
        var channel = network.Channels.Single();
        var member = channel.Members.Single(member => member.Nickname == "Alex");
        var context = new ParticipantActionContext(network, channel, member, network.Channels);
        Assert.True((await new ParticipantActionService(manager).SetIgnoredAsync(context, true)).Succeeded);
        transport.EnqueueInboundLine(":Alex!u@host.example PRIVMSG #general :hidden");
        transport.EnqueueInboundLine(":Alex!u@host.example NICK AlexRenamed");
        await WaitForAsync(() => channel.MembersSnapshot.Any(item => item.Nickname == "AlexRenamed"));
        Assert.DoesNotContain(channel.EntriesSnapshot, entry => entry.Text.Contains("hidden", StringComparison.Ordinal));
        Assert.True(configuration.IsIgnored(new IgnoreIdentity("Alex", "u", "host.example", NetworkProfileId: network.ProfileId, NetworkName: network.NetworkName), network.Snapshot.Features.CaseMapping));
    }

    [Fact]
    public async Task AccountTagIgnoreSuppressesMessageAndProjectsKnownAccount()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("account.example", 6667, false));
        factory.Add(transport);
        var configuration = new ConfigurationService(new InMemoryConfigurationStore());
        await using var manager = new NetworkSessionManager(factory, configuration: configuration);
        var network = manager.Add(Options("Account", transport.Endpoint, "operator", "#general", profileId: Guid.NewGuid()));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "operator");
        SeedMemberState(transport, "operator", "Alex", "host.example", "@");
        await WaitForAsync(() => network.Channels.Single().Members.Any(member => member.Nickname == "Alex"));

        configuration.AddIgnore(new IgnoreRule { Account = "alexacct", NetworkProfileId = network.ProfileId });
        transport.EnqueueInboundLine("@account=alexacct :Alex!u@host.example PRIVMSG #general :account-hidden");
        var channel = network.Channels.Single();
        await WaitForAsync(() => channel.MembersSnapshot.Single(member => member.Nickname == "Alex").Account == "alexacct");

        Assert.DoesNotContain(channel.EntriesSnapshot, entry => entry.Text.Contains("account-hidden", StringComparison.Ordinal));
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, string channel, Guid? profileId = null) => new()
    {
        ProfileId = profileId,
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1K test",
        DesiredChannels = new HashSet<string>([channel], StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string server, string nickname, string? network = null, string prefix = "(qaohv)~&@%+")
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        transport.EnqueueInboundLine($":{server} 005 {nickname} PREFIX={prefix}{(network is null ? string.Empty : $" NETWORK={network}")} CHANMODES=b,k,l,imnpst :features");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :welcome");
    }

    private static void SeedMemberState(FakeIrcTransport transport, string localNickname, string target, string host, string prefix)
    {
        transport.EnqueueInboundLine($":{localNickname}!u@local JOIN #general");
        transport.EnqueueInboundLine($":srv 353 {localNickname} = #general :{prefix}{localNickname} {target}");
        transport.EnqueueInboundLine(":srv 366 " + localNickname + " #general :End");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1K test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
