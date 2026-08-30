using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class ProtocolStateTests
{
    [Fact]
    public async Task FragmentedAndCoalescedInputUpdatesChannelsNamesTopicsAndQueries()
    {
        var endpoint = new IrcEndpoint("state.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "alice",
            Username = "alice",
            RealName = "Test user",
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var sawQuit = false;
        session.SemanticEventReceived += (_, item) => sawQuit |= item.Event is IrcQuitEvent quit && quit.Nickname == "robert";
        var run = session.RunAsync();
        while (transport.ConnectCount == 0)
        {
            await Task.Delay(5);
        }

        transport.EnqueueInboundBytes(Encoding.UTF8.GetBytes(":srv CAP * LS :"));
        transport.EnqueueInboundBytes(Encoding.UTF8.GetBytes("\r\n:srv 005 alice PREFIX=(qaohv)~&@%+ CHANTYPES=# :supported\r\n:srv 001 alice :welcome\r\n"));
        transport.EnqueueInboundBytes(Encoding.UTF8.GetBytes(":alice!u@host JOIN #room\r\n:bob!b@host JOIN #room\r\n:srv 353 alice = #room :@alice +bob\r\n:srv 332 alice #room :Topic text\r\n:bob!b@host PRIVMSG alice :hello\r\n:bob!b@host NICK robert\r\n:robert!b@host QUIT :gone\r\n"));

        await WaitForAsync(() => sawQuit
            && session.Snapshot.Queries.Count == 1
            && session.Snapshot.Channels.Count == 1
            && !session.Snapshot.Channels[0].Members.ContainsKey("robert"));
        var snapshot = session.Snapshot;
        var channel = Assert.Single(snapshot.Channels);

        Assert.True(channel.IsJoined);
        Assert.Equal("Topic text", channel.Topic);
        Assert.Contains("alice", channel.Members.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("robert", channel.Members.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("bob", snapshot.Queries[0].Nickname);
        Assert.Contains("hello", snapshot.Queries[0].Messages);

        await session.DisconnectAsync();
        await run;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The protocol state was not updated in time.");
            }

            await Task.Delay(10);
        }
    }
}
