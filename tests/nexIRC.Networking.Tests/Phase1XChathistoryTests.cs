using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase1XChathistoryTests
{
    [Fact]
    public async Task ValidatedHistoryBatchProducesHistoricalMessagesWithoutPresenceMutation()
    {
        var endpoint = new IrcEndpoint("history.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            RequestedCapabilities = [IrcCapabilityCatalog.Chathistory],
            ChathistoryRequestTimeout = TimeSpan.FromSeconds(1),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var events = new List<SessionSemanticEvent>();
        session.SemanticEventReceived += (_, item) => events.Add(item);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv CAP * ACK :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv 005 nex CHATHISTORY=50 MSGREFTYPES=msgid,timestamp CHANTYPES=# :supported");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);
        Assert.True(session.Snapshot.Features.Chathistory.IsUsable);

        var request = new ChathistoryRequest
        {
            NetworkId = Guid.NewGuid(),
            ConnectionGeneration = session.Snapshot.ConnectionGeneration,
            Conversation = "Channel:#room",
            Target = "#room",
            Operation = ChathistoryOperation.Before,
            Reference = ChathistoryReference.MessageId("oldest"),
            Limit = 10,
            Purpose = ChathistoryRequestPurpose.LoadOlder
        };
        var pending = session.RequestHistoryAsync(request).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BEFORE #room oldest", StringComparison.Ordinal)));

        transport.EnqueueInboundLine(":srv BATCH +h chathistory #room");
        transport.EnqueueInboundLine("@batch=h;msgid=history-1;time=2026-09-07T12:00:00.000Z :alice!u@h PRIVMSG #room :older");
        transport.EnqueueInboundLine(":srv BATCH -h");

        var result = await pending;
        Assert.True(result.Succeeded);
        Assert.Equal(1, result.MessageCount);
        var message = Assert.Single(result.Messages, item => item is IrcPrivmsgEvent);
        Assert.True(message.IsHistorical);
        Assert.Equal("history-1", message.Message.ServerMessageId);
        Assert.Equal("h", message.Message.BatchId);
        Assert.Empty(session.Snapshot.Channels);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task UnrelatedHistoryBatchDoesNotCompleteRequestOrApplyStateEvents()
    {
        var (session, transport, run) = await StartRegisteredAsync();
        var request = NewRequest(session, "#room");
        var pending = session.RequestHistoryAsync(request).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BEFORE", StringComparison.Ordinal)));

        transport.EnqueueInboundLine(":srv BATCH +other chathistory #other");
        transport.EnqueueInboundLine("@batch=other :alice!u@h JOIN #other");
        transport.EnqueueInboundLine(":srv BATCH -other");
        await Task.Delay(50);
        Assert.False(pending.IsCompleted);
        Assert.Empty(session.Snapshot.Channels);

        transport.EnqueueInboundLine(":srv BATCH +h chathistory #room");
        transport.EnqueueInboundLine("@batch=h;msgid=history-2;time=2026-09-07T12:00:00.000Z :alice!u@h PRIVMSG #room :older");
        transport.EnqueueInboundLine(":srv BATCH -h");
        Assert.True((await pending).Succeeded);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task EmptyHistoryBatchCompletesAndMarksConversationExhausted()
    {
        var (session, transport, run) = await StartRegisteredAsync();
        var request = NewRequest(session, "#room");
        var pending = session.RequestHistoryAsync(request).AsTask();
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BEFORE", StringComparison.Ordinal)));
        transport.EnqueueInboundLine(":srv BATCH +h chathistory #room");
        transport.EnqueueInboundLine(":srv BATCH -h");

        var result = await pending;
        Assert.True(result.Succeeded);
        Assert.True(result.IsEmpty);
        Assert.True(session.GetChathistoryState("Channel:#room").BeginningReached);
        Assert.False(session.CanLoadOlderHistory("Channel:#room"));
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task HistoryRequestTimesOutAndDoesNotRemainActive()
    {
        var endpoint = new IrcEndpoint("history-timeout.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            RequestedCapabilities = [IrcCapabilityCatalog.Chathistory],
            ChathistoryRequestTimeout = TimeSpan.FromMilliseconds(50),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv CAP * ACK :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv 005 nex CHATHISTORY=50 MSGREFTYPES=timestamp :supported");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        var result = await session.RequestHistoryAsync(NewRequest(session, "#room"));
        Assert.Equal(ChathistoryRequestCompletion.TimedOut, result.Completion);
        Assert.False(session.GetChathistoryState("#room").RequestActive);
        await session.DisconnectAsync();
        await run;
    }

    private static ChathistoryRequest NewRequest(ServerSession session, string target) => new()
    {
        NetworkId = Guid.NewGuid(),
        ConnectionGeneration = session.Snapshot.ConnectionGeneration,
        Conversation = $"Channel:{target}",
        Target = target,
        Operation = ChathistoryOperation.Before,
        Reference = ChathistoryReference.Timestamp(DateTimeOffset.UtcNow.AddMinutes(-1)),
        Limit = 10,
        Purpose = ChathistoryRequestPurpose.LoadOlder
    };

    private static async Task<(ServerSession Session, FakeIrcTransport Transport, Task Run)> StartRegisteredAsync()
    {
        var endpoint = new IrcEndpoint("history.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            RequestedCapabilities = [IrcCapabilityCatalog.Chathistory],
            ChathistoryRequestTimeout = TimeSpan.FromSeconds(1),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv CAP * ACK :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv 005 nex CHATHISTORY=50 MSGREFTYPES=msgid,timestamp CHANTYPES=# :supported");
        transport.EnqueueInboundLine(":srv 001 nex :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);
        return (session, transport, run);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic CHATHISTORY condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
