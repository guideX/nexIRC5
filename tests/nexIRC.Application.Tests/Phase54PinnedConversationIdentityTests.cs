using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase54PinnedConversationIdentityTests
{
    [Fact]
    public async Task PinKeysScopeChannelsByNetworkAndPrivateQueriesByStableAccountEvidence()
    {
        var factory = new FakeIrcTransportFactory();
        var alphaTransport = new FakeIrcTransport(new IrcEndpoint("alpha.example", 6697, true));
        var betaTransport = new FakeIrcTransport(new IrcEndpoint("beta.example", 6697, true));
        factory.Add(alphaTransport);
        factory.Add(betaTransport);
        await using var manager = new NetworkSessionManager(factory);
        var alpha = manager.Add(Options("AlphaNet", alphaTransport.Endpoint));
        var beta = manager.Add(Options("BetaNet", betaTransport.Endpoint));

        var alphaChannel = manager.EnsureChannel(alpha.Id, "#Dev[");
        var betaChannel = manager.EnsureChannel(beta.Id, "#dev[");
        var alphaChannelKey = NetworkSessionManager.SynchronizedPinConversationKey(alphaChannel, alpha.Snapshot);
        var betaChannelKey = NetworkSessionManager.SynchronizedPinConversationKey(betaChannel, beta.Snapshot);
        Assert.NotNull(alphaChannelKey);
        Assert.NotEqual(alphaChannelKey, betaChannelKey);

        var bob = manager.EnsureQuery(alpha.Id, "Bob");
        Assert.Null(NetworkSessionManager.SynchronizedPinConversationKey(bob, alpha.Snapshot));
        bob.ObserveIdentityEvidence(new ConversationIdentityEvidence(alpha.Id, "Bob", "bob-account", null, null,
            alpha.Snapshot.ConnectionGeneration, DateTimeOffset.UtcNow, IdentityEvidenceSource.LiveAccountTag));
        var bobAccountKey = NetworkSessionManager.SynchronizedPinConversationKey(bob, alpha.Snapshot);
        Assert.NotNull(bobAccountKey);

        Assert.True(bob.RebindNicknameFromStrongAccount("Robert", alpha.Snapshot.Features.CaseMapping,
            alpha.Snapshot.ConnectionGeneration));
        Assert.Equal("Robert", bob.Nickname);
        Assert.Equal(bobAccountKey, NetworkSessionManager.SynchronizedPinConversationKey(bob, alpha.Snapshot));

        var recycledBob = manager.EnsureQuery(alpha.Id, "Bob");
        Assert.NotSame(bob, recycledBob);
        Assert.Null(NetworkSessionManager.SynchronizedPinConversationKey(recycledBob, alpha.Snapshot));
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = "local-user",
        Username = "local-user",
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };
}
