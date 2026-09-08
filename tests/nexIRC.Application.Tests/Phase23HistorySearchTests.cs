using System.Diagnostics;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase23HistorySearchTests
{
    [Fact]
    public void StrictNavigationInputsTrimMsgidsAndRequireExplicitUtc()
    {
        Assert.True(HistorySearchInput.TryNormalizeServerMessageId("  0012  ", out var messageId, out _));
        Assert.Equal("0012", messageId);
        Assert.False(HistorySearchInput.TryNormalizeServerMessageId("", out _, out _));
        Assert.False(HistorySearchInput.TryNormalizeServerMessageId(new string('x', ConfigurationLimits.MaximumHistoryMessageIdLength + 1), out _, out _));

        Assert.True(HistorySearchInput.TryParseUtcDateTime("2026-09-07T12:30:00.125Z", out var timestamp, out _));
        Assert.Equal(TimeSpan.Zero, timestamp.Offset);
        Assert.Equal("2026-09-07T12:30:00.1250000+00:00", timestamp.ToString("O"));
        Assert.False(HistorySearchInput.TryParseUtcDateTime("09/07/2026 12:30", out _, out _));
    }

    [Fact]
    public void StaleSearchCompletionCannotOwnTheResultSurface()
    {
        var generations = new HistorySearchGeneration();
        var first = generations.Begin();
        var second = generations.Begin();

        Assert.False(generations.IsCurrent(first));
        Assert.True(generations.IsCurrent(second));

        generations.Invalidate();
        Assert.False(generations.IsCurrent(second));
    }

    [Fact]
    public async Task CancelledSearchStopsBeforeReadingCanonicalHistory()
    {
        await using var store = new InMemoryConversationLogStore();
        await store.AppendAsync(Record(Guid.NewGuid(), Guid.NewGuid(), "#room", "cancel marker", "Alice"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.SearchDetailedAsync(new ConversationLogQuery { Text = "cancel marker" }, cancellation.Token));
    }

    [Fact]
    public async Task InvalidCurrentConversationScopeDoesNotFallBackToAllHistory()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase23-scope-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var network = Guid.NewGuid();
            await store.AppendAsync(Record(network, network, "#room", "scope marker", "Alice"));
            var page = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                Text = "scope marker"
            });

            Assert.Empty(page.Results);
            Assert.Equal(0, page.Statistics.RecordsExamined);
            Assert.Contains("requires", page.Statistics.ValidationError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SearchMatchesConversationLabelsAndIncrementallyExtendsAWarmIndex()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase23-index-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var network = Guid.NewGuid();
            for (var index = 0; index < 4_200; index++)
            {
                await store.AppendAsync(Record(
                    network,
                    network,
                    "#kernel",
                    $"filler-{index} {new string('x', 300)}",
                    index % 2 == 0 ? "Alice" : "Bob"));
            }

            await store.FlushAsync();
            var initial = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                NetworkId = network,
                HistoryScopeId = network,
                ConversationName = "#kernel",
                Text = "never-present-term"
            });
            Assert.Empty(initial.Results);

            var path = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            Assert.True(File.Exists($"{path}.hsidx"));

            await store.AppendAsync(Record(network, network, "#kernel", "phase23-appended-needle", "Alice"));
            await store.FlushAsync();
            var appended = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                NetworkId = network,
                HistoryScopeId = network,
                ConversationName = "#kernel",
                Text = "phase23-appended-needle"
            });

            var result = Assert.Single(appended.Results);
            Assert.Equal("#kernel", result.ConversationName);
            Assert.Equal("Alice", result.Sender);
            Assert.Null(result.ServerMessageId);
            Assert.NotNull(result.CanonicalAnchor);
            Assert.InRange(result.Preview.Length, 0, ConfigurationLimits.MaximumSearchSnippetLength);
            Assert.Equal(0, appended.Statistics.IndexFilesBuilt);
            Assert.True(appended.Statistics.IndexFilesUsed >= 1);
            Assert.True(appended.Statistics.RecordsSkippedByIndex > 0);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CrossNetworkMsgidsAndRepeatedNoMsgidRowsRemainDistinct()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase23-identity-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var scope = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            await store.AppendAsync(Record(first, scope, "#same", "shared result", "Alice", "same-msgid"));
            await store.AppendAsync(Record(second, scope, "#same", "shared result", "Alice", "same-msgid"));
            await store.AppendAsync(Record(first, scope, "#same", "repeated no id", "Alice"));
            await store.AppendAsync(Record(first, scope, "#same", "repeated no id", "Alice"));
            await store.FlushAsync();

            var shared = await store.SearchAsync(new ConversationLogQuery { Text = "shared result" });
            Assert.Equal(2, shared.Count);
            Assert.Equal(new[] { first, second }.OrderBy(item => item), shared.Select(item => item.NetworkId).OrderBy(item => item));

            var repeated = await store.SearchAsync(new ConversationLogQuery { Text = "repeated no id" });
            Assert.Equal(2, repeated.Count);
            Assert.NotEqual(repeated[0].DurableSequence, repeated[1].DurableSequence);
            Assert.All(repeated, item => Assert.Null(item.ServerMessageId));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task SearchResultUsesCanonicalAnchorForTrimmedTranscriptNavigation()
    {
        var transport = new FakeIrcTransport(new IrcEndpoint("phase23-navigation.example", 6667, false));
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var logs = new InMemoryConversationLogStore();
        await using var manager = new NetworkSessionManager(factory, logStore: logs);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Phase 23",
            Endpoint = transport.Endpoint,
            Nickname = "nex",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new ReconnectPolicy(Enabled: false)
        });
        var channel = manager.EnsureChannel(network.Id, "#room");
        for (var index = 0; index < 1_000; index++)
        {
            await logs.AppendAsync(Record(network.Id, network.ProfileId ?? network.Id, "#room", $"message-{index}", "Alice", durableSequence: index + 1));
        }

        var search = await logs.SearchAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = network.ProfileId ?? network.Id,
            NetworkId = network.Id,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            Text = "message-123"
        });
        var result = Assert.Single(search);
        var navigation = await manager.NavigateToHistorySearchResultAsync(network, channel, result);

        Assert.Equal(HistoryNavigationOutcome.ExactLocalMatch, navigation.Outcome);
        Assert.InRange(channel.EntryCount, 1, WorkspaceView.MaximumEntries);
        Assert.Equal("message-123", channel.NavigationAnchor?.Text);
        Assert.Empty(transport.OutboundLines);
        Assert.Contains(channel.EntriesSnapshot, entry => entry.Text == "message-123" && entry.IsNavigationAnchor);
    }

    private static ConversationLogRecord Record(
        Guid network,
        Guid scope,
        string conversation,
        string text,
        string sender,
        string? serverMessageId = null,
        long durableSequence = 0) =>
        new()
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddTicks(durableSequence > 0 ? durableSequence : 1),
            NetworkId = network,
            ScopeId = scope,
            ProfileId = scope,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = conversation,
            ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, conversation),
            Sender = sender,
            MessageKind = LogMessageKind.Message,
            Direction = LogDirection.Incoming,
            Text = text,
            ServerMessageId = serverMessageId,
            DurableSequence = durableSequence
        };
}
