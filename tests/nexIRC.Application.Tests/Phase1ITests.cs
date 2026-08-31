using System.Text;
using nexIRC.Application;

namespace nexIRC.Application.Tests;

public sealed class Phase1ITests
{
    [Fact]
    public async Task CrossFileSearchSupportsScopesFiltersDuplicateTargetsAndBoundedResults()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1i-search-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var alpha = Guid.Parse("00000000-0000-0000-0000-000000000011");
            var beta = Guid.Parse("00000000-0000-0000-0000-000000000012");
            var profile = Guid.Parse("00000000-0000-0000-0000-000000000013");
            await store.AppendAsync(Record(alpha, profile, "#general", DateTimeOffset.UnixEpoch.AddHours(1), "release alpha", "Mira"));
            await store.AppendAsync(Record(alpha, profile, "#general", DateTimeOffset.UnixEpoch.AddHours(2), "release alpha action", "Rook", LogMessageKind.Action));
            await store.AppendAsync(Record(alpha, profile, "#other", DateTimeOffset.UnixEpoch.AddHours(3), "release elsewhere", "Mira"));
            await store.AppendAsync(Record(beta, beta, "#general", DateTimeOffset.UnixEpoch.AddHours(4), "release beta", "Mira"));
            await store.FlushAsync();

            var all = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Text = "release",
                MaximumResults = 2
            });
            Assert.Equal(2, all.Results.Count);
            Assert.Equal(4, all.Statistics.MatchingRecords);
            Assert.True(all.Statistics.ResultsTruncated);
            Assert.Equal(4, all.Statistics.RecordsExamined);
            Assert.Equal(3, all.Statistics.FilesExamined);
            Assert.All(all.Results, result =>
            {
                Assert.False(string.IsNullOrWhiteSpace(result.ConversationName));
                Assert.NotNull(result.Location);
                Assert.NotNull(result.Location.SourceOffset);
                Assert.True(result.Location.SourceLength > 0);
            });
            Assert.Equal(beta, all.Results[0].NetworkId);
            Assert.Equal("#general", all.Results[0].ConversationName);

            var currentConversation = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentConversation,
                HistoryScopeId = profile,
                NetworkId = alpha,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#GENERAL",
                Text = "release"
            });
            Assert.Equal(2, currentConversation.Results.Count);
            Assert.Equal(1, currentConversation.Statistics.FilesExamined);
            Assert.All(currentConversation.Results, result => Assert.Equal(alpha, result.NetworkId));

            var currentNetwork = await store.SearchDetailedAsync(new ConversationLogQuery
            {
                Scope = ConversationLogSearchScope.CurrentNetwork,
                HistoryScopeId = profile,
                NetworkId = alpha,
                Text = "release",
                Sender = "mira",
                From = DateTimeOffset.UnixEpoch.AddHours(1),
                To = DateTimeOffset.UnixEpoch.AddHours(2),
                MessageKind = LogMessageKind.Message
            });
            var networkResult = Assert.Single(currentNetwork.Results);
            Assert.Equal("#general", networkResult.ConversationName);
            Assert.Equal("Mira", networkResult.Sender);
            Assert.Equal(LogMessageKind.Message, networkResult.MessageKind);

            var betaSameName = await store.SearchAsync(new ConversationLogQuery
            {
                ConversationName = "#GENERAL",
                NetworkId = beta,
                Text = "release"
            });
            var betaResult = Assert.Single(betaSameName);
            Assert.Equal(beta, betaResult.NetworkId);
            Assert.Equal(beta, betaResult.ScopeId);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task SearchSkipsMalformedAndTruncatedRecordsAndRebuildsAfterSidecarChanges()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1i-files-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var scope = Guid.NewGuid();
            for (var index = 0; index < 4_000; index++)
            {
                await store.AppendAsync(Record(scope, scope, "#room", DateTimeOffset.UtcNow.AddSeconds(index), index == 0 ? "visible message" : $"filler-{index}", "Mira"));
            }
            await store.FlushAsync();
            var logPath = Directory.EnumerateFiles(directory.FullName, "*.jsonl", SearchOption.AllDirectories).Single();
            await File.AppendAllTextAsync(logPath, "not-json\n{\"timestamp\":\"broken");

            var results = await store.SearchDetailedAsync(new ConversationLogQuery { Text = "visible" });
            Assert.Single(results.Results);
            Assert.Equal(4_000, results.Statistics.RecordsExamined);
            var searchSidecarPath = $"{logPath}.hsidx";
            Assert.True(File.Exists(searchSidecarPath));
            Assert.DoesNotContain("visible", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(searchSidecarPath)), StringComparison.OrdinalIgnoreCase);

            var sidecarPath = $"{logPath}.hidx";
            await store.ReadPageWindowAsync(new HistoryPageRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#room",
                PageSize = 1
            });
            Assert.True(File.Exists(sidecarPath));
            File.Delete(sidecarPath);
            await File.WriteAllTextAsync(sidecarPath, "partial sidecar");
            var page = await store.ReadPageWindowAsync(new HistoryPageRequest
            {
                ScopeId = scope,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#room",
                PageSize = 1
            });
            Assert.Single(page.Records);
            Assert.True(new FileInfo(sidecarPath).Length > "partial sidecar".Length);

            File.Delete(searchSidecarPath);
            var rebuiltMissingSearch = await store.SearchDetailedAsync(new ConversationLogQuery { Text = "visible" });
            Assert.Single(rebuiltMissingSearch.Results);
            Assert.True(File.Exists(searchSidecarPath));

            await File.WriteAllBytesAsync(searchSidecarPath, "partial search index"u8.ToArray());
            var rebuiltSearch = await store.SearchDetailedAsync(new ConversationLogQuery { Text = "visible" });
            Assert.Single(rebuiltSearch.Results);
            Assert.True(new FileInfo(searchSidecarPath).Length > "partial search index".Length);

            var validSearchIndex = await File.ReadAllBytesAsync(searchSidecarPath);
            var corruptedSearchIndex = validSearchIndex.ToArray();
            corruptedSearchIndex[50] ^= 0x01;
            await File.WriteAllBytesAsync(searchSidecarPath, corruptedSearchIndex);
            var rebuiltCorruptSearch = await store.SearchDetailedAsync(new ConversationLogQuery { Text = "visible" });
            Assert.Single(rebuiltCorruptSearch.Results);
            Assert.Equal(validSearchIndex, await File.ReadAllBytesAsync(searchSidecarPath));
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task SearchCancellationIsObservedBeforeScanningHistory()
    {
        var store = new InMemoryConversationLogStore();
        var scope = Guid.NewGuid();
        for (var index = 0; index < 100; index++)
        {
            await store.AppendAsync(Record(scope, scope, "#room", DateTimeOffset.UnixEpoch.AddMinutes(index), $"message-{index}", "Mira"));
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await store.SearchDetailedAsync(new ConversationLogQuery { Text = "message" }, cancellation.Token));
    }

    [Fact]
    public async Task SearchDoesNotChangeLifecycleActivityOrSendIrcCommands()
    {
        var factory = new nexIRC.Networking.Testing.FakeIrcTransportFactory();
        var transport = new nexIRC.Networking.Testing.FakeIrcTransport(new nexIRC.Core.Networking.IrcEndpoint("phase1i.invalid", 6667, false));
        factory.Add(transport);
        await using var manager = new nexIRC.Application.NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = "Phase 1I",
            Endpoint = transport.Endpoint,
            Nickname = "alice",
            Username = "alice",
            RequestedCapabilities = Array.Empty<string>(),
            Reconnect = new nexIRC.Core.Session.ReconnectPolicy(Enabled: false)
        });
        var channel = manager.EnsureChannel(network.Id, "#room");
        channel.MarkActivity(WorkspaceActivity.Important);
        var lifecycle = channel.LifecycleState;
        var outboundBefore = transport.OutboundLines.Count;
        var store = new InMemoryConversationLogStore();
        await store.AppendAsync(Record(network.Id, network.Id, "#room", DateTimeOffset.UtcNow, "search only", "Mira"));

        var results = await store.SearchAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = network.Id,
            NetworkId = network.Id,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            Text = "search"
        });

        Assert.Single(results);
        Assert.Equal(lifecycle, channel.LifecycleState);
        Assert.Equal(WorkspaceActivity.Important, channel.Activity);
        Assert.Equal(outboundBefore, transport.OutboundLines.Count);
        Assert.Single(network.Channels);
    }

    private static ConversationLogRecord Record(
        Guid networkId,
        Guid scopeId,
        string name,
        DateTimeOffset timestamp,
        string text,
        string sender,
        LogMessageKind kind = LogMessageKind.Message) => new()
        {
            Timestamp = timestamp,
            NetworkId = networkId,
            ScopeId = scopeId,
            ProfileId = scopeId,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = name,
            ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, name),
            Sender = sender,
            MessageKind = kind,
            Direction = LogDirection.Incoming,
            Text = text
        };
}
