using System.Diagnostics;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
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

    [Fact]
    public async Task AccountBackedQueryDraftsSurviveNickChangeAndNickReuseStaysDistinct()
    {
        await using var clientA = await DraftSessionFixture.CreateAsync("alice-a");
        await using var clientB = await DraftSessionFixture.CreateAsync("alice-b");
        var queryA = clientA.Manager.EnsureQuery(clientA.Network.Id, "Bob");
        var queryB = clientB.Manager.EnsureQuery(clientB.Network.Id, "Bob");

        Assert.Null(NetworkSessionManager.SynchronizedDraftConversationKey(queryA, clientA.Network.Snapshot));
        Assert.False(await clientA.Manager.TryUpdateSynchronizedDraftAsync(queryA, 0,
            "00000000000000000000000000000001", "private local query draft"));
        Assert.DoesNotContain(clientA.Transport.OutboundLines, static line => line.StartsWith("NEXIRC STATE SET DRAFT ", StringComparison.Ordinal));

        clientA.Transport.EnqueueInboundLine("@account=bob-account :Bob!u@host PRIVMSG alice :account evidence");
        clientB.Transport.EnqueueInboundLine("@account=bob-account :Bob!u@host PRIVMSG alice :account evidence");
        await WaitForAsync(() => queryA.IdentityEvidence.HasAccount("bob-account")
            && queryB.IdentityEvidence.HasAccount("bob-account"));
        const string key = "query-account:bob-account";
        Assert.Equal(key, NetworkSessionManager.SynchronizedDraftConversationKey(queryA, clientA.Network.Snapshot));
        Assert.Equal(key, NetworkSessionManager.SynchronizedDraftConversationKey(queryB, clientB.Network.Snapshot));

        const string firstText = "A's account-owned query draft";
        const string firstMutation = "11111111111111111111111111111111";
        Assert.True(await clientA.Manager.TryUpdateSynchronizedDraftAsync(queryA, 0, firstMutation, firstText));
        var firstOutbound = await clientA.WaitForOutboundAsync("NEXIRC STATE SET DRAFT ");
        Assert.Contains($"{NexIrcReadStateProtocol.EncodeField(key)} 0 {firstMutation} {NexIrcDraftStateProtocol.EncodePayload(firstText)}",
            firstOutbound, StringComparison.Ordinal);
        clientB.Transport.EnqueueInboundLine($":srv NEXIRC STATE UPDATE DRAFT {NexIrcReadStateProtocol.EncodeField(key)} 1 {firstMutation} {NexIrcDraftStateProtocol.EncodePayload(firstText)}");
        await WaitForAsync(() => clientB.Manager.TryGetSynchronizedDraftState(queryB, out var draft)
            && draft?.Text == firstText && draft.Revision == 1);

        clientA.Transport.EnqueueInboundLine(":Bob!u@host NICK Robert");
        clientB.Transport.EnqueueInboundLine(":Bob!u@host NICK Robert");
        await WaitForAsync(() => queryA.Nickname == "Robert" && queryB.Nickname == "Robert");
        Assert.Equal(key, NetworkSessionManager.SynchronizedDraftConversationKey(queryA, clientA.Network.Snapshot));
        Assert.Equal(key, NetworkSessionManager.SynchronizedDraftConversationKey(queryB, clientB.Network.Snapshot));

        const string secondText = "B edited Bob's draft after the nick change";
        const string secondMutation = "22222222222222222222222222222222";
        Assert.True(await clientB.Manager.TryUpdateSynchronizedDraftAsync(queryB, 1, secondMutation, secondText));
        var secondOutbound = await clientB.WaitForOutboundAsync("NEXIRC STATE SET DRAFT ");
        Assert.Contains($"{NexIrcReadStateProtocol.EncodeField(key)} 1 {secondMutation} {NexIrcDraftStateProtocol.EncodePayload(secondText)}",
            secondOutbound, StringComparison.Ordinal);
        clientA.Transport.EnqueueInboundLine($":srv NEXIRC STATE UPDATE DRAFT {NexIrcReadStateProtocol.EncodeField(key)} 2 {secondMutation} {NexIrcDraftStateProtocol.EncodePayload(secondText)}");
        await WaitForAsync(() => clientA.Manager.TryGetSynchronizedDraftState(queryA, out var draft)
            && draft?.Text == secondText && draft.Revision == 2);

        clientA.Transport.EnqueueInboundLine("@account=charlie-account :Bob!new@host PRIVMSG alice :nick reused by Charlie");
        await WaitForAsync(() => clientA.Network.Queries.Any(query => query.IdentityEvidence.HasAccount("charlie-account")));
        var charlieUsingOldNick = clientA.Network.Queries.Single(query => query.IdentityEvidence.HasAccount("charlie-account"));
        Assert.NotSame(queryA, charlieUsingOldNick);
        Assert.Equal("query-account:charlie-account",
            NetworkSessionManager.SynchronizedDraftConversationKey(charlieUsingOldNick, clientA.Network.Snapshot));
        Assert.NotEqual(key, NetworkSessionManager.SynchronizedDraftConversationKey(charlieUsingOldNick, clientA.Network.Snapshot));
        Assert.True(clientA.Manager.TryGetSynchronizedDraftState(charlieUsingOldNick, out var charlieDraft));
        Assert.Null(charlieDraft);
        Assert.True(clientA.Manager.TryGetSynchronizedDraftState(queryA, out var bobDraft));
        Assert.Equal(secondText, bobDraft?.Text);

        var nickOnly = clientA.Manager.EnsureQuery(clientA.Network.Id, "NickOnly");
        Assert.Null(NetworkSessionManager.SynchronizedDraftConversationKey(nickOnly, clientA.Network.Snapshot));
        var draftMutationCount = clientA.Transport.OutboundLines.Count(
            static line => line.StartsWith("NEXIRC STATE SET DRAFT ", StringComparison.Ordinal));
        Assert.False(await clientA.Manager.TryUpdateSynchronizedDraftAsync(nickOnly, 0,
            "33333333333333333333333333333333", "local-only fallback"));
        Assert.Equal(draftMutationCount, clientA.Transport.OutboundLines.Count(
            static line => line.StartsWith("NEXIRC STATE SET DRAFT ", StringComparison.Ordinal)));
    }

    private sealed class DraftSessionFixture : IAsyncDisposable
    {
        private readonly NetworkSessionManager _manager;

        private DraftSessionFixture(NetworkSessionManager manager, FakeIrcTransport transport, NetworkWorkspace network)
        {
            _manager = manager;
            Manager = manager;
            Transport = transport;
            Network = network;
        }

        public NetworkSessionManager Manager { get; }
        public FakeIrcTransport Transport { get; }
        public NetworkWorkspace Network { get; }

        public static async Task<DraftSessionFixture> CreateAsync(string label)
        {
            var endpoint = new IrcEndpoint("phase53-query-draft.example", 6697, true);
            var transport = new FakeIrcTransport(endpoint);
            var factory = new FakeIrcTransportFactory();
            factory.Add(transport);
            var manager = new NetworkSessionManager(factory);
            var network = manager.Add(new NetworkConnectionOptions
            {
                DisplayName = label,
                Endpoint = endpoint,
                Nickname = "alice",
                Username = "alice",
                RealName = label,
                Reconnect = new ReconnectPolicy(Enabled: false),
                RequestedCapabilities = ["message-tags", "nexirc/state"],
                SaslPolicy = SaslAuthenticationPolicy.Required,
                SaslCredentialProvider = new FixedSaslCredentialProvider()
            });

            await manager.ConnectAsync(network.Id);
            await WaitForAsync(() => transport.ConnectCount == 1);
            transport.EnqueueInboundLine(":srv CAP * LS :message-tags sasl=PLAIN nexirc/state=1");
            await WaitForAsync(() => transport.OutboundLines.Any(static line => line.StartsWith("CAP REQ :", StringComparison.Ordinal)));
            transport.EnqueueInboundLine(":srv CAP * ACK :message-tags sasl=PLAIN nexirc/state=1");
            await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE PLAIN"));
            transport.EnqueueInboundLine(":srv AUTHENTICATE +");
            await WaitForAsync(() => transport.OutboundLines.Any(static line => line.StartsWith("AUTHENTICATE ", StringComparison.Ordinal)
                && !line.Equals("AUTHENTICATE PLAIN", StringComparison.Ordinal)));
            transport.EnqueueInboundLine(":srv 903 alice :SASL successful");
            await WaitForAsync(() => transport.OutboundLines.Contains("CAP END") && transport.OutboundLines.Contains("NICK alice"));
            transport.EnqueueInboundLine(":srv 001 alice :Welcome");
            await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
            var snapshotProbe = manager.EnsureQuery(network.Id, "SnapshotProbe");
            transport.EnqueueInboundLine("@account=snapshot-probe :SnapshotProbe!u@host PRIVMSG alice :account evidence");
            await WaitForAsync(() => snapshotProbe.IdentityEvidence.HasAccount("snapshot-probe"));
            transport.EnqueueInboundLine(":srv NEXIRC STATE BEGIN 1 0");
            transport.EnqueueInboundLine(":srv NEXIRC STATE END 1");
            await WaitForAsync(() => transport.OutboundLines.Contains("NEXIRC STATE GET DRAFT"));
            transport.EnqueueInboundLine(":srv NEXIRC STATE DRAFT BEGIN 0");
            transport.EnqueueInboundLine(":srv NEXIRC STATE DRAFT END");
            await WaitForAsync(() => manager.TryGetSynchronizedDraftState(snapshotProbe, out _));
            return new DraftSessionFixture(manager, transport, network);
        }

        public async Task<string> WaitForOutboundAsync(string prefix)
        {
            string? line = null;
            await WaitForAsync(() => (line = Transport.OutboundLines.FirstOrDefault(item =>
                item.StartsWith(prefix, StringComparison.Ordinal))) is not null);
            return line!;
        }

        public ValueTask DisposeAsync() => _manager.DisposeAsync();
    }

    private sealed class FixedSaslCredentialProvider : ISaslCredentialProvider
    {
        public ValueTask<SaslCredential?> GetCredentialsAsync(IrcEndpoint endpoint, string mechanism,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SaslCredential?>(new SaslCredential("alice", "phase53-test-secret"));
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
