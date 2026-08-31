using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1HTests
{
    [Fact]
    public void NavigationHistoryIsBoundedAndSuppressesLogicalDuplicates()
    {
        var network = Guid.NewGuid();
        var history = new ConversationNavigationHistory(3);
        var first = new ConversationIdentity(network, WorkspaceViewKind.Channel, "#Room");
        var duplicate = new ConversationIdentity(network, WorkspaceViewKind.Channel, "#room");
        var second = new ConversationIdentity(network, WorkspaceViewKind.Query, "Mira");
        var third = new ConversationIdentity(Guid.NewGuid(), WorkspaceViewKind.Channel, "#Room");
        var fourth = new ConversationIdentity(network, WorkspaceViewKind.ServerStatus, "status");

        history.Record(first);
        history.Record(duplicate);
        history.Record(second);
        history.Record(third);
        history.Record(fourth);

        Assert.Equal(3, history.Entries.Count);
        Assert.DoesNotContain(history.Entries, item => item.SameAs(first));
        Assert.True(history.TryGoBack(_ => true, out var previous));
        Assert.NotNull(previous);
        Assert.Equal(third.StableKey, previous!.StableKey);
        Assert.True(history.TryGoForward(_ => true, out var next));
        Assert.Equal(fourth.StableKey, next!.StableKey);
    }

    [Fact]
    public async Task ConversationNavigationKeepsDuplicateTargetsIsolatedAndSkipsClosedViews()
    {
        var factory = new FakeIrcTransportFactory();
        var alphaTransport = new FakeIrcTransport(new IrcEndpoint("alpha.example", 6667, false));
        var betaTransport = new FakeIrcTransport(new IrcEndpoint("beta.example", 6667, false));
        factory.Add(alphaTransport);
        factory.Add(betaTransport);
        await using var manager = new NetworkSessionManager(factory);
        var alpha = manager.Add(Options("AlphaNet", alphaTransport.Endpoint, "alice"));
        var beta = manager.Add(Options("BetaNet", betaTransport.Endpoint, "alice"));
        var alphaRoom = manager.EnsureChannel(alpha.Id, "#room");
        var betaRoom = manager.EnsureChannel(beta.Id, "#room");
        var alphaQuery = manager.EnsureQuery(alpha.Id, "Mira");

        manager.ActivateView(alphaRoom.Id);
        manager.ActivateView(betaRoom.Id);
        manager.ActivateView(alphaQuery.Id);
        Assert.True(manager.NavigateBack());
        Assert.Same(betaRoom, manager.ActiveView);
        Assert.True(manager.NavigateForward());
        Assert.Same(alphaQuery, manager.ActiveView);

        alphaRoom.MarkActivity(WorkspaceActivity.Unread);
        alphaQuery.MarkActivity(WorkspaceActivity.Important);
        manager.ActivateView(alpha.StatusView.Id);
        Assert.True(manager.NavigateNextUnread());
        Assert.Same(alphaRoom, manager.ActiveView);
        alphaRoom.MarkActivity(WorkspaceActivity.Important);
        manager.ActivateView(alpha.StatusView.Id);
        Assert.True(manager.NavigateNextHighlight());
        Assert.Same(alphaRoom, manager.ActiveView);

        Assert.True(manager.CloseView(betaRoom.Id));
        Assert.False(manager.NavigateBack() && ReferenceEquals(manager.ActiveView, betaRoom));
        Assert.Same(betaRoom, manager.OpenHistoricalConversation(beta.Id, DestinationKind.Channel, "#room"));
        Assert.True(betaRoom.IsViewOpen);
        Assert.Same(betaRoom, manager.ActiveView);
        Assert.Equal(2, manager.GetConversationNavigator().Count(item => item.Kind == WorkspaceViewKind.Channel));
        Assert.Contains(manager.GetConversationNavigator(), item => item.NetworkDisplayName == "AlphaNet" && item.Name == "#room");
        Assert.Contains(manager.GetConversationNavigator(), item => item.NetworkDisplayName == "BetaNet" && item.Name == "#room");
    }

    [Fact]
    public async Task PartAndCloseIsExplicitAndHistoricalRemovalKeepsJsonlAddressable()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("lifecycle-h.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Lifecycle H", transport.Endpoint, "alice"));
        var historical = manager.OpenHistoricalConversation(network.Id, DestinationKind.Channel, "#old");
        Assert.Equal(ConversationLifecycleState.HistoricalOnly, historical.LifecycleState);
        Assert.True(manager.RemoveHistoricalConversation(historical.Id));
        Assert.DoesNotContain(network.Views, view => view is ChannelView channel && channel.Channel == "#old");

        var joined = manager.EnsureChannel(network.Id, "#joined");
        manager.ActivateView(joined.Id);
        var closeTask = manager.PartAndCloseAsync(joined.Id).AsTask();
        await closeTask;
        Assert.DoesNotContain(transport.OutboundLines, line => line.StartsWith("PART", StringComparison.Ordinal));
        Assert.DoesNotContain(network.Views, view => view.Id == joined.Id);
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };
}
