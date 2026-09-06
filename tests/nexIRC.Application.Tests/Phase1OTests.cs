using System.Collections.Concurrent;
using System.Diagnostics;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1OTests
{
    [Fact]
    public async Task DirectNickFollowsQueryWithoutChangingActivityOrHistoryIdentity()
    {
        var fixture = await CreateFixtureAsync();
        await using var manager = fixture.Manager;
        var query = manager.EnsureQuery(fixture.Network.Id, "Alex");
        manager.ActivateView(fixture.Network.StatusView.Id);
        fixture.Transport.EnqueueInboundLine(":Alex!u@host PRIVMSG alice :before the nick change");
        await WaitForAsync(() => query.UnreadCount == 1 && query.EntryCount == 1);
        var historyKey = query.HistoryConversationKey;
        var unread = query.UnreadCount;
        var important = query.ImportantCount;
        var notifications = new ConcurrentQueue<IrcNotification>();
        using var subscription = manager.Notifications.Subscribe(notifications.Enqueue);

        fixture.Transport.EnqueueInboundLine(":Alex!u@host NICK Alex2");
        await WaitForAsync(() => query.Nickname == "Alex2" && query.EntryCount == 2);
        await manager.FlushStateDispatchAsync();
        await fixture.Logs.FlushAsync();

        Assert.Equal("Alex2", query.Title);
        Assert.Same(query, fixture.Network.Queries.Single(item => item.Nickname == "Alex2"));
        Assert.Equal(unread, query.UnreadCount);
        Assert.Equal(important, query.ImportantCount);
        Assert.Equal(historyKey, query.HistoryConversationKey);
        Assert.Contains(query.EntriesSnapshot, entry => entry.Kind == TranscriptEntryKind.Nick && entry.Text.Contains("Alex2", StringComparison.Ordinal));
        Assert.DoesNotContain(notifications, item => item.ViewId == query.Id && item.SemanticSource == nameof(IrcNicknameChangedEvent));
        await WaitForAsync(() => fixture.Logs.Records.Count(record => record.ConversationKind == LogConversationKind.PrivateConversation) >= 2);

        var page = await fixture.Logs.ReadPageWindowAsync(new HistoryPageRequest
        {
            ScopeId = fixture.Network.Id,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = "Alex2",
            ConversationKey = historyKey,
            PageSize = 10
        });
        Assert.Equal(2, page.Records.Count);
        Assert.Contains(page.Records, record => record.ConversationName == "Alex" && record.Text.Contains("before", StringComparison.Ordinal));
        Assert.Contains(page.Records, record => record.ConversationName == "Alex2" && record.MessageKind == LogMessageKind.Nick);

        var search = await fixture.Logs.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = fixture.Network.Id,
            NetworkId = fixture.Network.Id,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = "Alex2",
            ConversationKey = historyKey,
            Text = "before"
        });
        Assert.Single(search.Results);
    }

    [Fact]
    public async Task NickTransitionUpdatesScopedNavigationReferencesAndUsesRfcCaseMapping()
    {
        var fixture = await CreateFixtureAsync();
        await using var manager = fixture.Manager;
        var query = manager.EnsureQuery(fixture.Network.Id, "Alex{");
        manager.ActivateView(query.Id);
        Assert.True(manager.AddFavorite(fixture.Network.Id, DestinationKind.Query, "Alex{"));
        manager.RecordRecent(fixture.Network, DestinationKind.Query, "Alex{");

        fixture.Transport.EnqueueInboundLine(":Alex[!u@host NICK Alex2");
        await WaitForAsync(() => query.Nickname == "Alex2");

        Assert.Equal("Alex2", fixture.Configuration.Favorites.Single(item => item.Kind == DestinationKind.Query).Name);
        Assert.Equal("Alex2", fixture.Configuration.RecentDestinations.Single(item => item.Kind == DestinationKind.Query).Name);
        Assert.Contains(manager.NavigationHistory, item => item.Kind == WorkspaceViewKind.Query && item.Name == "Alex2");
    }

    [Fact]
    public async Task NickCollisionDoesNotMergeExistingQueriesOrTheirDraftState()
    {
        var fixture = await CreateFixtureAsync();
        await using var manager = fixture.Manager;
        var alex = manager.EnsureQuery(fixture.Network.Id, "Alex");
        var alex2 = manager.EnsureQuery(fixture.Network.Id, "Alex2");
        alex.MarkActivity(WorkspaceActivity.Important);
        manager.ActivateView(fixture.Network.StatusView.Id);

        fixture.Transport.EnqueueInboundLine(":Alex!u@host NICK Alex2");
        await manager.FlushStateDispatchAsync();

        Assert.Equal("Alex", alex.Nickname);
        Assert.Equal("Alex2", alex2.Nickname);
        Assert.NotSame(alex, alex2);
        Assert.Equal(1, alex.ImportantCount);
        Assert.DoesNotContain(alex2.EntriesSnapshot, entry => entry.Kind == TranscriptEntryKind.Nick);
    }

    [Fact]
    public async Task NickReuseCreatesANewQueryForTheNewParticipant()
    {
        var fixture = await CreateFixtureAsync();
        await using var manager = fixture.Manager;
        var oldAlex = manager.EnsureQuery(fixture.Network.Id, "Alex");
        manager.ActivateView(fixture.Network.StatusView.Id);
        fixture.Transport.EnqueueInboundLine(":Alex!u@host NICK Alex2");
        await WaitForAsync(() => oldAlex.Nickname == "Alex2");

        var newAlex = manager.EnsureQuery(fixture.Network.Id, "Alex");
        Assert.NotSame(oldAlex, newAlex);
        fixture.Transport.EnqueueInboundLine(":Alex!new@host PRIVMSG alice :new participant");
        await WaitForAsync(() => newAlex.EntryCount == 1);
        Assert.Contains(newAlex.EntriesSnapshot, entry => entry.Text == "new participant");
        Assert.DoesNotContain(oldAlex.EntriesSnapshot, entry => entry.Text == "new participant");
    }

    [Fact]
    public async Task NickTransitionIsNetworkLocalAndCloseReopenUsesTheSameLogicalQuery()
    {
        var factory = new FakeIrcTransportFactory();
        var alphaTransport = new FakeIrcTransport(new IrcEndpoint("alpha.example", 6667, false));
        var betaTransport = new FakeIrcTransport(new IrcEndpoint("beta.example", 6667, false));
        factory.Add(alphaTransport);
        factory.Add(betaTransport);
        var configuration = TestConfiguration();
        await configuration.LoadAsync();
        await using var manager = new NetworkSessionManager(factory, configuration: configuration, logStore: new InMemoryConversationLogStore());
        var alpha = manager.Add(Options("Alpha", alphaTransport.Endpoint, "alice"));
        var beta = manager.Add(Options("Beta", betaTransport.Endpoint, "alice"));
        await manager.ConnectAsync(alpha.Id);
        await manager.ConnectAsync(beta.Id);
        await WaitForAsync(() => alphaTransport.ConnectCount == 1 && betaTransport.ConnectCount == 1);
        Register(alphaTransport, "alice");
        Register(betaTransport, "alice");
        var alphaQuery = manager.EnsureQuery(alpha.Id, "Alex");
        var betaQuery = manager.EnsureQuery(beta.Id, "Alex");
        manager.ActivateView(alpha.StatusView.Id);
        alphaTransport.EnqueueInboundLine(":Alex!u@alpha NICK Alex2");
        await WaitForAsync(() => alphaQuery.Nickname == "Alex2");

        Assert.Equal("Alex", betaQuery.Nickname);
        Assert.Same(alphaQuery, manager.EnsureQuery(alpha.Id, "Alex2"));
        Assert.True(manager.CloseView(alphaQuery.Id));
        var reopened = manager.OpenHistoricalConversation(alpha.Id, DestinationKind.Query, "Alex2");
        Assert.Same(alphaQuery, reopened);
        Assert.True(alphaQuery.IsViewOpen);
        Assert.Equal("Alex", manager.EnsureQuery(beta.Id, "Alex").Nickname);
    }

    [Fact]
    public async Task ReconnectEndsDirectNickProofForNewInboundTraffic()
    {
        var fixture = await CreateFixtureAsync();
        await using var manager = fixture.Manager;
        var oldQuery = manager.EnsureQuery(fixture.Network.Id, "Alex");
        manager.ActivateView(fixture.Network.StatusView.Id);
        fixture.Transport.EnqueueInboundLine(":Alex!u@host NICK Alex2");
        await WaitForAsync(() => oldQuery.Nickname == "Alex2");

        var replacement = new FakeIrcTransport(fixture.Transport.Endpoint);
        fixture.Factory.Add(replacement);
        await manager.ReconnectAsync(fixture.Network.Id);
        await WaitForAsync(() => replacement.ConnectCount == 1);
        Register(replacement, "alice");
        replacement.EnqueueInboundLine(":Alex2!new@host PRIVMSG alice :after reconnect");
        await WaitForAsync(() => fixture.Network.Queries.Count == 2 && fixture.Network.Queries.Any(query => query.EntryCount == 1));

        Assert.Contains(fixture.Network.Queries, query => ReferenceEquals(query, oldQuery) && query.Nickname == "Alex2");
        Assert.Contains(fixture.Network.Queries, query => !ReferenceEquals(query, oldQuery) && query.Nickname == "Alex2" && query.EntryCount == 1);
    }

    [Fact]
    public async Task DispatcherTelemetryIsBoundedAndReportsCategoriesAndPercentiles()
    {
        var dispatcher = new SerializedWorkspaceDispatcher(new ImmediateWorkspaceDispatcher());
        var work = Enumerable.Range(0, SerializedWorkspaceDispatcher.RecentSampleWindowSize + 37)
            .Select(index => dispatcher.InvokeAsync(
                () => { if (index % 17 == 0) Thread.SpinWait(100); },
                index % 2 == 0 ? WorkspaceDispatchActionCategory.IncomingMessage : WorkspaceDispatchActionCategory.UserSelection).AsTask())
            .ToArray();
        await Task.WhenAll(work);
        var diagnostics = dispatcher.Diagnostics;
        await dispatcher.CompleteAsync();

        Assert.Equal(work.Length, diagnostics.QueuedActions);
        Assert.Equal(work.Length, diagnostics.ProcessedActions);
        Assert.Equal(0, diagnostics.CurrentQueueDepth);
        Assert.True(diagnostics.MaximumQueueDepth >= 1);
        Assert.True(diagnostics.RecentSamples is not null && diagnostics.RecentSamples.Count <= SerializedWorkspaceDispatcher.RecentSampleWindowSize);
        Assert.Contains(diagnostics.RecentSamples!, sample => sample.Category == WorkspaceDispatchActionCategory.IncomingMessage);
        Assert.Contains(diagnostics.RecentSamples!, sample => sample.Category == WorkspaceDispatchActionCategory.UserSelection);
        Assert.True(diagnostics.P95QueueWaitMilliseconds >= diagnostics.P50QueueWaitMilliseconds);
        Assert.True(diagnostics.P99QueueWaitMilliseconds >= diagnostics.P95QueueWaitMilliseconds);
        Assert.True(diagnostics.MaximumQueueWaitMilliseconds >= diagnostics.P99QueueWaitMilliseconds);
    }

    [Fact]
    public async Task MixedProtocolActivityRetainsOrderingAndCoarseCategories()
    {
        var fixture = await CreateFixtureAsync();
        await using var manager = fixture.Manager;
        var channel = manager.EnsureChannel(fixture.Network.Id, "#mixed");
        manager.ActivateView(fixture.Network.StatusView.Id);

        fixture.Transport.EnqueueInboundLine(":BurstUser!u@host JOIN #mixed");
        fixture.Transport.EnqueueInboundLine(":BurstUser!u@host PRIVMSG #mixed :mixed message");
        fixture.Transport.EnqueueInboundLine(":BurstUser!u@host PART #mixed :mixed part");
        fixture.Transport.EnqueueInboundLine(":BurstUser!u@host NICK BurstUser2");
        fixture.Transport.EnqueueInboundLine(":srv MODE #mixed +m");
        fixture.Transport.EnqueueInboundLine(":srv TOPIC #mixed :mixed topic");
        fixture.Transport.EnqueueInboundLine(":srv NOTICE alice :mixed notice");

        await WaitForAsync(() => channel.EntryCount >= 6 && fixture.Network.StatusView.EntryCount > 0);
        await manager.FlushStateDispatchAsync();

        var entries = channel.EntriesSnapshot;
        Assert.Equal("mixed topic", channel.Topic);
        Assert.Equal("mixed message", entries[1].Text);
        Assert.Contains(entries, entry => entry.Kind == TranscriptEntryKind.Part && entry.Sender == "BurstUser");
        Assert.Contains(entries, entry => entry.Kind == TranscriptEntryKind.Nick && entry.Text.Contains("BurstUser2", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Kind == TranscriptEntryKind.Mode && entry.Text.Contains("+m", StringComparison.Ordinal));
        Assert.Contains(manager.Diagnostics.StateDispatch.RecentSamples!, sample => sample.Category == WorkspaceDispatchActionCategory.IncomingMessage);
        Assert.Contains(manager.Diagnostics.StateDispatch.RecentSamples!, sample => sample.Category == WorkspaceDispatchActionCategory.Membership);
        Assert.Contains(manager.Diagnostics.StateDispatch.RecentSamples!, sample => sample.Category == WorkspaceDispatchActionCategory.ModeOrTopic);
        Assert.Contains(manager.Diagnostics.StateDispatch.RecentSamples!, sample => sample.Category == WorkspaceDispatchActionCategory.Lifecycle);
    }

    private static async Task<(FakeIrcTransportFactory Factory, FakeIrcTransport Transport, NetworkWorkspace Network, NetworkSessionManager Manager, ConfigurationService Configuration, InMemoryConversationLogStore Logs)> CreateFixtureAsync()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("phase1o.example", 6667, false));
        factory.Add(transport);
        var configuration = TestConfiguration();
        await configuration.LoadAsync();
        var logs = new InMemoryConversationLogStore();
        var manager = new NetworkSessionManager(factory, configuration: configuration, logStore: logs);
        var network = manager.Add(Options("Phase 1O", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "alice");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        return (factory, transport, network, manager, configuration, logs);
    }

    private static ConfigurationService TestConfiguration() => new(new InMemoryConfigurationStore(new NexIrcConfiguration
    {
        Preferences = new ApplicationPreferences
        {
            ConversationLoggingEnabled = true,
            PrivateMessageLoggingEnabled = true,
            StatusLoggingEnabled = true
        }
    }));

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1O test",
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMilliseconds / 1000d * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The Phase 1O deterministic condition did not complete.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
