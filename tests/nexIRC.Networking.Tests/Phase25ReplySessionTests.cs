using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase25ReplySessionTests
{
    [Fact]
    public async Task SendsTypedReplyOnlyWithNegotiatedMessageTagsAndRoutesOwnDirectEcho()
    {
        var endpoint = new IrcEndpoint("reply.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = [IrcCapabilityCatalog.MessageTags, IrcCapabilityCatalog.EchoMessage],
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags echo-message");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags echo-message");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        await session.SendReplyAsync("#room", "résumé", "Parent.ID", session.Snapshot.ConnectionGeneration);
        await WaitForAsync(() => transport.OutboundLines.Any(line => line == "@+reply=Parent.ID PRIVMSG #room :résumé"));

        var semantic = ReadUntilAsync(
            session.ReadSemanticEventsAsync(),
            item => item.Event is IrcQueryMessageEvent query
                && query.Message.ReplyParentMessageId == "Parent.ID");
        transport.EnqueueInboundLine("@+reply=Parent.ID;msgid=Child.ID :alice!u@h PRIVMSG bob :echoed reply");
        var received = await semantic;

        var queryEvent = Assert.IsType<IrcQueryMessageEvent>(received.Event);
        Assert.Equal("bob", queryEvent.Nickname);
        Assert.Equal("Parent.ID", queryEvent.Message.ReplyParentMessageId);
        Assert.Equal("Child.ID", queryEvent.Message.ServerMessageId);

        await session.DisconnectAsync("test complete");
        await run;
    }

    [Fact]
    public async Task ReplySendRefusesUnnegotiatedMessageTags()
    {
        var endpoint = new IrcEndpoint("legacy.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = [],
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.SendReplyAsync("#room", "body", "Parent.ID", session.Snapshot.ConnectionGeneration));
        Assert.Contains("message-tags", exception.Message, StringComparison.OrdinalIgnoreCase);

        await session.DisconnectAsync("test complete");
        await run;
    }

    private static async Task<SessionSemanticEvent> ReadUntilAsync(
        IAsyncEnumerable<SessionSemanticEvent> events,
        Func<SessionSemanticEvent, bool> predicate)
    {
        await foreach (var item in events)
        {
            if (predicate(item))
            {
                return item;
            }
        }

        throw new InvalidOperationException("The expected semantic event was not observed.");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic reply session condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
