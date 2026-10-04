using System.Diagnostics;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase52SynchronizedDraftIdentityTests
{
    [Fact]
    public async Task ChannelKeysFoldAndQueriesRequireStableUnambiguousPeerAccounts()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase52-draft-identity.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Phase 52 identity",
            Endpoint = transport.Endpoint,
            Nickname = "alice",
            Username = "alice",
            RealName = "Phase 52 identity test",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        });

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 005 alice CASEMAPPING=rfc1459 :features");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var channel = manager.EnsureChannel(network.Id, "#GuideXOS[");
        Assert.Equal("channel:#guidexos{", NetworkSessionManager.SynchronizedDraftConversationKey(channel, network.Snapshot));

        var query = manager.EnsureQuery(network.Id, "Peer");
        Assert.Null(NetworkSessionManager.SynchronizedDraftConversationKey(query, network.Snapshot));
        transport.EnqueueInboundLine("@account=peer-account :Peer!u@host PRIVMSG alice :verified account tagged event");
        await WaitForAsync(() => query.IdentityEvidence.HasAccount("peer-account"));
        var originalKey = NetworkSessionManager.SynchronizedDraftConversationKey(query, network.Snapshot);
        Assert.Equal("query-account:peer-account", originalKey);

        transport.EnqueueInboundLine(":Peer!u@host NICK Peer2");
        await WaitForAsync(() => query.Nickname == "Peer2");
        Assert.Equal(originalKey, NetworkSessionManager.SynchronizedDraftConversationKey(query, network.Snapshot));

        transport.EnqueueInboundLine("@account=other-account :Peer2!u@host PRIVMSG alice :conflicting identity evidence");
        await WaitForAsync(() => query.IdentityEvidence.HasConflictingAccounts);
        Assert.Null(NetworkSessionManager.SynchronizedDraftConversationKey(query, network.Snapshot));

        var otherPeerQuery = manager.EnsureQuery(network.Id, "Charlie");
        Assert.NotSame(query, otherPeerQuery);
        Assert.Null(NetworkSessionManager.SynchronizedDraftConversationKey(otherPeerQuery, network.Snapshot));
        transport.EnqueueInboundLine("@account=charlie-account :Charlie!new@host PRIVMSG alice :different peer account");
        await WaitForAsync(() => otherPeerQuery.IdentityEvidence.HasAccount("charlie-account"));
        Assert.Equal("query-account:charlie-account",
            NetworkSessionManager.SynchronizedDraftConversationKey(otherPeerQuery, network.Snapshot));
        Assert.NotEqual(originalKey, NetworkSessionManager.SynchronizedDraftConversationKey(otherPeerQuery, network.Snapshot));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMilliseconds / 1000d * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("The Phase 52 draft identity condition did not complete.");
            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
