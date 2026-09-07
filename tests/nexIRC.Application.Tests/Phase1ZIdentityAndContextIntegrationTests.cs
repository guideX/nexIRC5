using System.Diagnostics;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1ZIdentityAndContextIntegrationTests
{
    [Fact]
    public async Task ExtendedJoinAccountEvidenceCanStrengthenAnExistingLiveQuery()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase1z-extended-join.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(transport.Endpoint));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex", "extended-join");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var query = manager.EnsureQuery(network.Id, "Alice");
        transport.EnqueueInboundLine(":Alice!u@host JOIN #general alice-account :Alice Real Name");
        await WaitForAsync(() => query.IdentityEvidence.HasAccount("alice-account"));

        Assert.Contains(
            query.IdentityEvidence.Observations,
            evidence => evidence.Source == IdentityEvidenceSource.LiveExtendedJoin);
    }

    [Fact]
    public async Task SameAccountAfterReconnectRebindsTheExistingQueryWithoutDuplicate()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("phase1z-account.example", 6667, false));
        factory.Add(first);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(first.Endpoint));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var query = manager.EnsureQuery(network.Id, "Alice");
        first.EnqueueInboundLine("@account=alice-account :Alice!u@host PRIVMSG nex :before reconnect");
        await WaitForAsync(() => query.IdentityEvidence.HasAccount("alice-account"));

        var replacement = new FakeIrcTransport(first.Endpoint);
        factory.Add(replacement);
        await manager.ReconnectAsync(network.Id);
        await WaitForAsync(() => replacement.ConnectCount == 1);
        Register(replacement, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        replacement.EnqueueInboundLine("@account=alice-account :Alicia!u@host PRIVMSG nex :after reconnect");
        await WaitForAsync(() => query.Nickname == "Alicia" && query.EntryCount == 2);

        Assert.Single(network.Queries);
        Assert.Same(query, network.Queries.Single());
        Assert.Equal("alice", query.HistoryConversationKey["PrivateConversation:".Length..]);
        Assert.Contains(query.EntriesSnapshot, item => item.Text == "after reconnect");
    }

    [Fact]
    public async Task ConflictingAccountOnTheOldNicknameCreatesASeparateSafeQuery()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase1z-conflict.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(transport.Endpoint));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var alice = manager.EnsureQuery(network.Id, "Alice");
        transport.EnqueueInboundLine("@account=alice-account :Alice!u@host PRIVMSG nex :known Alice");
        await WaitForAsync(() => alice.IdentityEvidence.HasAccount("alice-account"));
        transport.EnqueueInboundLine("@account=other-account :Alice!other@host PRIVMSG nex :different Alice");
        await WaitForAsync(() => network.Queries.Count == 2);

        var other = network.Queries.Single(item => !ReferenceEquals(item, alice));
        Assert.DoesNotContain(alice.EntriesSnapshot, item => item.Text == "different Alice");
        Assert.Contains(other.EntriesSnapshot, item => item.Text == "different Alice");
        Assert.Contains(alice.IdentityEvidence.Accounts, account => account == "alice-account");
        Assert.Contains(other.IdentityEvidence.Accounts, account => account == "other-account");
    }

    [Fact]
    public async Task LocalMsgidContextUsesCanonicalHistoryWithoutIssuingChathistory()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase1z-context.example", 6667, false));
        factory.Add(transport);
        var configuration = new ConfigurationService(new InMemoryConfigurationStore(new NexIrcConfiguration
        {
            Preferences = new ApplicationPreferences
            {
                ConversationLoggingEnabled = true,
                PrivateMessageLoggingEnabled = true
            }
        }));
        await configuration.LoadAsync();
        await using var logs = new InMemoryConversationLogStore();
        await using var manager = new NetworkSessionManager(factory, configuration: configuration, logStore: logs);
        var network = manager.Add(Options(transport.Endpoint));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        transport.EnqueueInboundLine("@msgid=ctx-1 :Alice!u@host PRIVMSG nex :one");
        transport.EnqueueInboundLine("@msgid=ctx-2 :Alice!u@host PRIVMSG nex :two");
        transport.EnqueueInboundLine("@msgid=ctx-3 :Alice!u@host PRIVMSG nex :three");
        await WaitForAsync(() => network.Queries.Count == 1 && network.Queries[0].EntryCount == 3);
        await logs.FlushAsync();

        var query = network.Queries.Single();
        var anchor = query.EntriesSnapshot.Single(item => item.ServerMessageId == "ctx-2");
        var outboundBefore = transport.OutboundLines.Count;
        var result = await manager.LoadContextAroundAsync(network, query, anchor);

        Assert.True(result.Succeeded);
        Assert.Equal(outboundBefore, transport.OutboundLines.Count);
        Assert.Equal(["one", "two", "three"], query.HistoryContext.Select(item => item.Record.Text));
    }

    private static NetworkConnectionOptions Options(IrcEndpoint endpoint) => new()
    {
        DisplayName = "Phase 1Z",
        Endpoint = endpoint,
        Nickname = "nex",
        Username = "nex",
        RealName = "Phase 1Z test",
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname, params string[] capabilities)
    {
        transport.EnqueueInboundLine($":srv CAP * LS :{string.Join(' ', capabilities)}");
        if (capabilities.Length > 0)
        {
            transport.EnqueueInboundLine($":srv CAP * ACK :{string.Join(' ', capabilities)}");
        }

        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMilliseconds / 1000d * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 1Z integration condition did not complete.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
