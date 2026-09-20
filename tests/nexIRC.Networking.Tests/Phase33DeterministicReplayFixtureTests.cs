using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase33DeterministicReplayFixtureTests
{
    [Fact]
    public async Task FullProfileReplaysOpaqueCanonicalHistoryWithBatchBoundaries()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.FullIrcv3Replay,
        [new("#room", "alice", "opaque-z9", DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture), Body: "older")]);
        await using var session = await StartRegisteredAsync(fixture, requestHistory: true);

        var request = NewRequest(session, "#room", ChathistoryOperation.Latest, "missing-boundary");
        var result = await session.RequestHistoryAsync(request);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.MessageCount);
        Assert.Equal("opaque-z9", Assert.Single(result.Messages).Message.ServerMessageId);
        Assert.Contains(fixture.HistoryRequests, line => line.StartsWith("CHATHISTORY LATEST #room", StringComparison.Ordinal));
        Assert.Contains(session.Snapshot.Capabilities.Enabled, capability => capability.Equals("batch", StringComparison.OrdinalIgnoreCase));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task NoHistoryProfileAdvertisesNoUsableHistoryAndSendsNoReplay()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NoHistory,
        [new("#room", "alice", "opaque-no-history", DateTimeOffset.UtcNow, Body: "live")]);
        await using var session = await StartRegisteredAsync(fixture, requestHistory: false);

        Assert.False(session.Snapshot.Features.Chathistory.IsUsable);
        Assert.DoesNotContain(fixture.Transport.OutboundLines, line => line.StartsWith("CHATHISTORY ", StringComparison.Ordinal));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task AdvertisedHistoryFailureIsStructuredFailureNotUnsupported()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.AdvertisedHistoryFails,
        [new("#room", "alice", "opaque-failure", DateTimeOffset.UtcNow, Body: "not returned")]);
        await using var session = await StartRegisteredAsync(fixture, requestHistory: true);

        var result = await session.RequestHistoryAsync(NewRequest(session, "#room", ChathistoryOperation.Latest, "opaque-boundary"));

        Assert.True(session.Snapshot.Features.Chathistory.IsUsable);
        Assert.False(result.Succeeded);
        Assert.Equal(ChathistoryRequestCompletion.Failed, result.Completion);
        Assert.Contains(fixture.HistoryRequests, line => line.StartsWith("CHATHISTORY LATEST #room", StringComparison.Ordinal));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task FixtureCanDriveLiveAndReplayOrderingWithoutRealSleeps()
    {
        var timestamp = DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture);
        await using var fixture = CreateFixture(DeterministicReplayProfile.FullIrcv3Replay,
        [new("#room", "alice", "opaque-replay", timestamp, Body: "replayed")]);
        await using var session = await StartRegisteredAsync(fixture, requestHistory: false);
        var events = new List<SessionSemanticEvent>();
        session.SemanticEventReceived += (_, item) => events.Add(item);

        fixture.EnqueueLive(new("#room", "alice", "opaque-live", timestamp.AddSeconds(1), Body: "live"));
        fixture.EnqueueLive(new("#room", "alice", "opaque-replay", timestamp, Body: "same canonical event"));
        await WaitForAsync(() => events.Count >= 2);

        Assert.Contains(events, item => item.Event.Message.ServerMessageId == "opaque-live");
        Assert.Contains(events, item => item.Event.Message.ServerMessageId == "opaque-replay");
        Assert.All(events, item => Assert.False(item.Event.IsHistorical));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task RelationshipTagsReplayAsCanonicalReplyReactionAndUnreactionEvents()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.FullIrcv3Replay,
        [
            new("#room", "alice", "parent-opaque", DateTimeOffset.Parse("2026-09-19T12:00:00Z", CultureInfo.InvariantCulture), Body: "parent"),
            new("#room", "bob", "reply-opaque", DateTimeOffset.Parse("2026-09-19T12:00:01Z", CultureInfo.InvariantCulture), Body: "reply", Tags: new Dictionary<string, string?>
            {
                ["+reply"] = "parent-opaque"
            }),
            new("#room", "carol", "reaction-opaque", DateTimeOffset.Parse("2026-09-19T12:00:02Z", CultureInfo.InvariantCulture), Command: "TAGMSG", Tags: new Dictionary<string, string?>
            {
                ["+reply"] = "parent-opaque",
                [IrcReaction.ReactTag] = "👍"
            }),
            new("#room", "carol", "unreaction-opaque", DateTimeOffset.Parse("2026-09-19T12:00:03Z", CultureInfo.InvariantCulture), Command: "TAGMSG", Tags: new Dictionary<string, string?>
            {
                ["+reply"] = "parent-opaque",
                [IrcReaction.UnreactTag] = "👍"
            })
        ]);
        await using var session = await StartRegisteredAsync(fixture, requestHistory: true);

        var result = await session.RequestHistoryAsync(NewRequest(session, "#room", ChathistoryOperation.Latest, "unknown-opaque"));

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.MessageCount);
        Assert.Contains(result.Messages, item => item is IrcPrivmsgEvent message && message.Message.ReplyParentMessageId == "parent-opaque");
        Assert.Contains(result.Messages, item => item is IrcReactionEvent reaction && reaction.Reaction.Operation == IrcReactionOperation.React);
        Assert.Contains(result.Messages, item => item is IrcReactionEvent reaction && reaction.Reaction.Operation == IrcReactionOperation.Unreact);
        Assert.DoesNotContain(result.Messages, item => item is IrcTagmsgEvent);

        await session.DisconnectAsync();
        await session.Completion;
    }

    private static DeterministicServerHistoryFixture CreateFixture(
        DeterministicReplayProfile profile,
        IReadOnlyList<DeterministicServerHistoryEvent> history) =>
        new(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase33-fixture.example", 6667, false),
            Nickname = "nex",
            Profile = profile,
            History = history
        });

    private static async Task<ServerSession> StartRegisteredAsync(
        DeterministicServerHistoryFixture fixture,
        bool requestHistory)
    {
        var factory = new FakeIrcTransportFactory();
        factory.Add(fixture.Transport);
        var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = fixture.Transport.Endpoint,
            Nickname = "nex",
            Username = "nex",
            RealName = "Phase 33 fixture",
            RequestedCapabilities = requestHistory ? [IrcCapabilityCatalog.Chathistory] : Array.Empty<string>(),
            ChathistoryRequestTimeout = TimeSpan.FromSeconds(1),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        _ = session.RunAsync();
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);
        return session;
    }

    private static ChathistoryRequest NewRequest(
        ServerSession session,
        string target,
        ChathistoryOperation operation,
        string reference) => new()
        {
            NetworkId = Guid.NewGuid(),
            ConnectionGeneration = session.Snapshot.ConnectionGeneration,
            Conversation = $"Channel:{target}",
            Target = target,
            Operation = operation,
            Reference = ChathistoryReference.MessageId(reference),
            Limit = 20,
            Purpose = ChathistoryRequestPurpose.ReconnectGap
        };

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 33 fixture condition was not reached.");
            }

            await Task.Delay(5);
        }
    }
}
