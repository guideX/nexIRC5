using System.Text.Json;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1GTests
{
    [Fact]
    public void ContextualAliasesExpandSafelyAndPreserveLiteralDollars()
    {
        var aliases = new[]
        {
            new AliasDefinition { Name = "where", Expansion = "/notice $target $network/$profile/$me/$server/$$/$selected $*" }
        };
        var context = new AliasContext("AlphaNet", "Alpha profile", "#room", "nexAlpha", "irc.alpha.invalid", null);

        var expanded = AliasExpander.Expand("/where one two", aliases, context);

        Assert.True(expanded.Succeeded);
        Assert.Equal("/notice #room AlphaNet/Alpha profile/nexAlpha/irc.alpha.invalid/$/ one two", expanded.Input);
        var missingContext = AliasExpander.Expand("/where", aliases).Input;
        Assert.StartsWith("/notice", missingContext, StringComparison.Ordinal);
        Assert.Contains("$/", missingContext, StringComparison.Ordinal);
    }

    [Fact]
    public void FavoriteGroupsPersistOrderMoveFavoritesAndAvoidCrossNetworkCollisions()
    {
        var config = new ConfigurationService(new InMemoryConfigurationStore());
        var alpha = Guid.NewGuid();
        var beta = Guid.NewGuid();
        Assert.True(config.AddFavoriteGroup("Work"));
        var work = Assert.Single(config.FavoriteGroups, group => group.Name == "Work");

        var first = new FavoriteDestination { ScopeId = alpha, Kind = DestinationKind.Channel, Name = "#Room" };
        var second = new FavoriteDestination { ScopeId = alpha, Kind = DestinationKind.Channel, Name = "#Other" };
        var sameNameOnBeta = new FavoriteDestination { ScopeId = beta, Kind = DestinationKind.Channel, Name = "#room" };
        Assert.True(config.AddOrUpdateFavorite(first));
        Assert.True(config.AddOrUpdateFavorite(second with { GroupId = work.Id }));
        Assert.True(config.AddOrUpdateFavorite(sameNameOnBeta));
        Assert.Equal(3, config.Favorites.Count);

        var alphaFirst = config.Favorites.Single(item => item.ScopeId == alpha && item.Name == "#Room");
        Assert.True(config.MoveFavorite(alphaFirst.Id, work.Id));
        Assert.Equal(work.Id, config.Favorites.Single(item => item.Id == alphaFirst.Id).GroupId);
        Assert.True(config.ReorderFavorite(alphaFirst.Id, -1) || config.ReorderFavorite(alphaFirst.Id, 1));
        Assert.True(config.RemoveFavoriteGroup(work.Id));
        Assert.All(config.Favorites, item => Assert.Equal(NavigationDefaults.DefaultFavoriteGroupId, item.GroupId));
    }

    [Fact]
    public void RecentsCanRemoveOrClearOneKindWithoutTouchingTheOtherKind()
    {
        var config = new ConfigurationService(new InMemoryConfigurationStore());
        var scope = Guid.NewGuid();
        config.RecordRecent(new RecentDestination { ScopeId = scope, Kind = DestinationKind.Channel, Name = "#room", LastOpened = DateTimeOffset.UtcNow.AddMinutes(-1) });
        config.RecordRecent(new RecentDestination { ScopeId = scope, Kind = DestinationKind.Query, Name = "Mira", LastOpened = DateTimeOffset.UtcNow });
        Assert.Equal(2, config.RecentDestinations.Count);

        config.ClearRecent(scope, DestinationKind.Channel);
        Assert.Single(config.RecentDestinations);
        Assert.Equal(DestinationKind.Query, config.RecentDestinations[0].Kind);
        Assert.True(config.RemoveRecent(config.RecentDestinations[0]));
        Assert.Empty(config.RecentDestinations);
    }

    [Fact]
    public async Task Phase1FOldConfigurationWithoutGroupsLoadsIntoGeneralGroup()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1g-config-");
        try
        {
            var scope = Guid.NewGuid();
            var path = Path.Combine(directory.FullName, "configuration.json");
            await File.WriteAllTextAsync(path, $$"""{"schemaVersion":1,"profiles":[],"favorites":[{"scopeId":"{{scope}}","kind":0,"name":"#room"}],"recentDestinations":[],"aliases":[]}""");
            var result = await new JsonConfigurationStore(path).LoadAsync();

            Assert.False(result.UsedDefaults);
            Assert.Single(result.Configuration.FavoriteGroups);
            Assert.Equal(NavigationDefaults.DefaultFavoriteGroupId, Assert.Single(result.Configuration.Favorites).GroupId);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task ClearCommandOnlyClearsTheActiveDisplay()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("clear.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Clear", transport.Endpoint, "alice"));
        var channel = manager.EnsureChannel(network.Id, "#room");
        channel.Entries.Add(new TranscriptEntry(DateTimeOffset.UtcNow, TranscriptEntryKind.Message, "Mira", "visible"));
        var other = manager.EnsureQuery(network.Id, "Mira");
        other.Entries.Add(new TranscriptEntry(DateTimeOffset.UtcNow, TranscriptEntryKind.Message, "Mira", "keep"));

        var result = await new IrcCommandDispatcher(manager).DispatchAsync(network, channel, "/clear");

        Assert.True(result.Succeeded);
        Assert.Empty(channel.EntriesSnapshot);
        Assert.Contains(other.EntriesSnapshot, entry => entry.Text == "keep");
    }

    [Fact]
    public async Task InMemoryHistorySupportsBoundedOldestNewestOlderNewerAndAroundPages()
    {
        var store = new InMemoryConversationLogStore();
        var scope = Guid.NewGuid();
        var baseTime = DateTimeOffset.UtcNow.Date;
        for (var index = 1; index <= 5; index++)
        {
            await store.AppendAsync(Record(scope, baseTime.AddHours(index), $"message-{index}"));
        }

        var newest = await store.ReadPageWindowAsync(Request(scope, pageSize: 2));
        Assert.Equal(["message-5", "message-4"], newest.Records.Select(record => record.Text));
        Assert.True(newest.HasOlder);
        Assert.False(newest.HasNewer);

        var older = await store.ReadPageWindowAsync(Request(scope, pageSize: 2) with { Before = newest.OldestTimestamp });
        Assert.Equal(["message-3", "message-2"], older.Records.Select(record => record.Text));
        var newer = await store.ReadPageWindowAsync(Request(scope, pageSize: 2) with { After = older.NewestTimestamp });
        Assert.Equal(["message-5", "message-4"], newer.Records.Select(record => record.Text));

        var oldest = await store.ReadPageWindowAsync(Request(scope, pageSize: 2) with { Oldest = true });
        Assert.Equal(["message-2", "message-1"], oldest.Records.Select(record => record.Text));
        var around = await store.ReadPageWindowAsync(Request(scope, pageSize: 3) with { Around = baseTime.AddHours(3), AroundWindow = TimeSpan.FromHours(1) });
        Assert.Equal(3, around.Records.Count);
        Assert.Contains(around.Records, record => record.Text == "message-3");
    }

    [Fact]
    public async Task JsonHistorySkipsMalformedRecordsAndExportsBoundedPlainTextAndJsonl()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1g-history-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var scope = Guid.NewGuid();
            await store.AppendAsync(Record(scope, DateTimeOffset.UtcNow, "safe message"));
            await store.FlushAsync();
            var logPath = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            await File.AppendAllTextAsync(logPath, "not-json\n");

            var page = await store.ReadPageWindowAsync(Request(scope, pageSize: 10));
            Assert.Single(page.Records);

            var plainPath = Path.Combine(directory.FullName, "export.txt");
            var exported = await ConversationLoggingService.ExportAsync(store, new HistoryExportRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#room"
            }, plainPath, HistoryExportFormat.PlainText, 1024);
            Assert.Single(exported.Records);
            Assert.Contains("safe message", await File.ReadAllTextAsync(plainPath));

            var jsonPath = Path.Combine(directory.FullName, "export.jsonl");
            await ConversationLoggingService.ExportAsync(store, new HistoryExportRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#room"
            }, jsonPath, HistoryExportFormat.Jsonl, 1024);
            Assert.Single(await File.ReadAllLinesAsync(jsonPath));
            Assert.DoesNotContain("password", await File.ReadAllTextAsync(jsonPath), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task ClosingJoinedChannelDoesNotPartAndReopenUsesTheSameLogicalView()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("lifecycle.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Lifecycle", transport.Endpoint, "alice"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "alice");
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.Count == 1 && network.Channels[0].IsJoined);
        var channel = network.Channels.Single();

        Assert.True(manager.CloseView(channel.Id));
        Assert.DoesNotContain(network.Views, view => view.Id == channel.Id);
        Assert.DoesNotContain(transport.OutboundLines, line => line.StartsWith("PART #room", StringComparison.Ordinal));

        Assert.True(manager.ReopenView(channel.Id));
        Assert.Same(channel, network.Views.Single(view => view.Id == channel.Id));
        var dispatcher = new IrcCommandDispatcher(manager);
        await dispatcher.DispatchAsync(network, channel, "/part #room testing");
        await WaitForAsync(() => transport.OutboundLines.Contains("PART #room :testing"));
        Assert.Equal(ConversationLifecycleState.Parted, channel.LifecycleState);
        await dispatcher.DispatchAsync(network, channel, "/rejoin #room");
        await WaitForAsync(() => transport.OutboundLines.Contains("JOIN #room"));
    }

    [Fact]
    public async Task ClosedQueryReopensWithoutCreatingADuplicateAndDisconnectedStateIsRetained()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("query.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Query", transport.Endpoint, "alice"));
        var query = manager.EnsureQuery(network.Id, "Mira");
        Assert.True(manager.CloseView(query.Id));
        var reopened = manager.EnsureQuery(network.Id, "mira");
        Assert.Same(query, reopened);
        Assert.Single(network.Queries);
        Assert.Single(network.Views, view => view is QueryView);

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "alice");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        await manager.DisconnectAsync(network.Id);
        await WaitForAsync(() => query.LifecycleState == ConversationLifecycleState.Disconnected);
    }

    private static ConversationLogRecord Record(Guid scope, DateTimeOffset timestamp, string text) => new()
    {
        Timestamp = timestamp,
        NetworkId = scope,
        ScopeId = scope,
        ProfileId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#room"),
        Sender = "Mira",
        MessageKind = LogMessageKind.Message,
        Direction = LogDirection.Incoming,
        Text = text
    };

    private static HistoryPageRequest Request(Guid scope, int pageSize) => new()
    {
        ScopeId = scope,
        ConversationKind = LogConversationKind.Channel,
        ConversationName = "#room",
        PageSize = pageSize
    };

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1G test",
        RequestedCapabilities = Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string server, string nickname)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to nexIRC test network");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("The Phase 1G lifecycle condition was not reached.");
            await Task.Delay(10);
        }
    }
}
