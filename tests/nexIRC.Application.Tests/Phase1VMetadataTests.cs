using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application.Tests;

public sealed class Phase1VMetadataTests
{
    [Fact]
    public void PresentationUsesServerTimeAndRetainsProcessingBoundary()
    {
        var message = IrcMessageParser.Parse("@time=2026-09-07T12:34:56.789Z :peer!u@h PRIVMSG #room :hello").Message!;
        var receivedAt = new DateTimeOffset(2026, 9, 7, 12, 35, 1, TimeSpan.Zero);
        var entry = IrcEventPresentation.Render(
            new IrcPrivmsgEvent(message, "#room", "hello", false),
            EmptySnapshot(),
            receivedAt);

        Assert.NotNull(entry);
        Assert.Equal(message.ServerTimestamp, entry!.Timestamp);
        Assert.Equal(receivedAt, entry.ReceivedAt);
        Assert.NotEqual(entry.Timestamp, entry.ReceivedAt);
    }

    [Fact]
    public void InvalidServerTimeUsesLocalReceiveTimeWithoutDroppingTheMessage()
    {
        var message = IrcMessageParser.Parse("@time=invalid :peer!u@h NOTICE #room :hello").Message!;
        var receivedAt = new DateTimeOffset(2026, 9, 7, 12, 35, 1, TimeSpan.Zero);
        var entry = IrcEventPresentation.Render(
            new IrcPrivmsgEvent(message, "#room", "hello", true),
            EmptySnapshot(),
            receivedAt);

        Assert.NotNull(entry);
        Assert.Equal(receivedAt, entry!.Timestamp);
        Assert.Equal(receivedAt, entry.ReceivedAt);
    }

    [Fact]
    public async Task HistoryReplayAndSearchPreserveDisplayedServerTime()
    {
        await using var store = new InMemoryConversationLogStore();
        var displayed = new DateTimeOffset(2026, 9, 7, 12, 34, 56, 789, TimeSpan.Zero);
        var received = displayed.AddSeconds(2);
        var record = new ConversationLogRecord
        {
            Timestamp = displayed,
            ReceivedAt = received,
            NetworkId = Guid.NewGuid(),
            ScopeId = Guid.NewGuid(),
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            ConversationKey = "Channel:#room",
            Sender = "peer",
            MessageKind = LogMessageKind.Message,
            Text = "server-timed"
        };
        await store.AppendAsync(record);

        var page = await store.ReadPageWindowAsync(new HistoryPageRequest
        {
            ScopeId = record.ScopeId,
            ConversationKind = record.ConversationKind,
            ConversationName = record.ConversationName,
            ConversationKey = record.ConversationKey,
            PageSize = 10,
            Oldest = true
        });
        var search = await store.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.CurrentConversation,
            HistoryScopeId = record.ScopeId,
            NetworkId = record.NetworkId,
            ConversationKind = record.ConversationKind,
            ConversationName = record.ConversationName,
            ConversationKey = record.ConversationKey,
            Text = "server-timed"
        });

        var replayed = Assert.Single(page.Records);
        var searched = Assert.Single(search.Results).Record;
        Assert.Equal(displayed, replayed.Timestamp);
        Assert.Equal(received, replayed.ReceivedAt);
        Assert.Equal(displayed, searched.Timestamp);
        Assert.Equal(received, searched.ReceivedAt);
    }

    private static ServerSessionSnapshot EmptySnapshot() => new(
        ServerSessionState.Disconnected,
        RegistrationState.NotStarted,
        "nex",
        "user",
        "real",
        new IrcEndpoint("empty.example", 6667, false),
        0,
        CapabilitySnapshot.Empty,
        ISupportSnapshot.Empty,
        ServerIdentity.Unknown,
        ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, null, ServerIdentity.Unknown),
        null,
        Array.Empty<IrcChannelSnapshot>(),
        Array.Empty<IrcQuerySnapshot>(),
        new HashSet<string>());
}
