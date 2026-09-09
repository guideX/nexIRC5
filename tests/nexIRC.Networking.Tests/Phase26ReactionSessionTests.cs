using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase26ReactionSessionTests
{
    [Fact]
    public async Task SendsExactReactionTagmsgAndRoutesIncomingReactionWithoutTextRows()
    {
        var endpoint = new IrcEndpoint("reaction.example", 6667, false);
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

        await session.SendReactionAsync("#room", "heart;blue", "Parent.ID", expectedConnectionGeneration: session.Snapshot.ConnectionGeneration);
        await WaitForAsync(() => transport.OutboundLines.Any(line => line == "@+reply=Parent.ID;+draft/react=heart\\:blue TAGMSG #room"));

        var semantic = ReadUntilAsync(session.ReadSemanticEventsAsync(), item => item.Event is IrcReactionEvent);
        transport.EnqueueInboundLine("@+reply=Parent.ID;+draft/react=heart;msgid=Event.ID :bob!u@h TAGMSG #room");
        var received = await semantic;
        var reaction = Assert.IsType<IrcReactionEvent>(received.Event);
        Assert.Equal("#room", reaction.Target);
        Assert.Equal("Parent.ID", reaction.ParentMessageId);
        Assert.Equal("heart", reaction.Value);
        Assert.Equal(IrcReactionOperation.React, reaction.Operation);

        await session.DisconnectAsync("test complete");
        await run;
    }

    [Fact]
    public async Task MalformedReactionRemainsAnUnrecognizedTagmsgAndDeniedTagsBlockSending()
    {
        var endpoint = new IrcEndpoint("reaction-deny.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "alice",
            RequestedCapabilities = [IrcCapabilityCatalog.MessageTags],
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags");
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags");
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        var malformed = ReadUntilAsync(session.ReadSemanticEventsAsync(), item => item.Event is IrcTagmsgEvent);
        transport.EnqueueInboundLine("@+draft/react=heart TAGMSG #room");
        Assert.IsType<IrcTagmsgEvent>((await malformed).Event);

        transport.EnqueueInboundLine(":srv 005 alice CLIENTTAGDENY=*,-+reply :supported");
        await WaitForAsync(() => session.Snapshot.Features.RuntimeISupport.HasClientTagDeny);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await session.SendReactionAsync("#room", "heart", "Parent.ID", expectedConnectionGeneration: session.Snapshot.ConnectionGeneration));
        Assert.Contains("CLIENTTAGDENY", exception.Message, StringComparison.OrdinalIgnoreCase);

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

        throw new InvalidOperationException("The expected reaction semantic event was not observed.");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic reaction session condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
