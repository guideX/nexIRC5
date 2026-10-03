using System.Text;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase50SynchronizedReadProjectionTests
{
    [Fact]
    public void RemoteMarkerReducesUnreadActivityWithoutRemovingTranscriptEntries()
    {
        var view = new ChannelView(Guid.NewGuid(), Guid.NewGuid(), "#room");
        var first = Entry("m7", 7, highlight: true);
        var second = Entry("m8", 8, highlight: false);
        view.AppendConversationEntry(first);
        view.AppendConversationEntry(second);
        view.MarkActivity(WorkspaceActivity.Important, first);
        view.MarkHighlight(first);
        view.MarkActivity(WorkspaceActivity.Unread, second);

        Assert.Equal(2, view.UnreadCount);
        Assert.Equal(1, view.HighlightCount);
        view.ApplySynchronizedReadMarker(new NexIrcReadMarker("channel:#room", 7, "m7", 1, 1));

        Assert.Equal(1, view.UnreadCount);
        Assert.Equal(0, view.ImportantCount);
        Assert.Equal(0, view.HighlightCount);
        Assert.Equal(2, view.EntryCount);
        Assert.True(view.IsCoveredBySynchronizedReadMarker(first));
        Assert.False(view.IsCoveredBySynchronizedReadMarker(second));
    }

    [Fact]
    public void SnapshotMarkerSuppressesReplayedActivityAndFreshEpochCanAdvanceFromLowerSequence()
    {
        var view = new ChannelView(Guid.NewGuid(), Guid.NewGuid(), "#room");
        var marker = new NexIrcReadMarker("channel:#room", 20, "old20", 3, 1);
        view.ApplySynchronizedReadMarker(marker);
        var replayed = Entry("old20", 20, epoch: 1);
        view.MarkActivity(WorkspaceActivity.Unread, replayed);
        Assert.Equal(0, view.UnreadCount);

        var newSessionMessage = Entry("new1", 1, epoch: 2);
        view.MarkActivity(WorkspaceActivity.Unread, newSessionMessage);
        Assert.Equal(1, view.UnreadCount);
        view.ApplySynchronizedReadMarker(new NexIrcReadMarker("channel:#room", 1, "new1", 4, 2));
        Assert.Equal(0, view.UnreadCount);
    }

    [Fact]
    public void OrdinaryIrcEntriesRemainLocalWhenTheyHaveNoStateEpoch()
    {
        var view = new ChannelView(Guid.NewGuid(), Guid.NewGuid(), "#room");
        view.MarkActivity(WorkspaceActivity.Unread, Entry("local", 0, epoch: 0));
        view.ApplySynchronizedReadMarker(new NexIrcReadMarker("channel:#room", 30, "m30", 1, 1));
        Assert.Equal(1, view.UnreadCount);
    }

    [Fact]
    public void OlderRevisionCannotReplaceANewerMarker()
    {
        var view = new ChannelView(Guid.NewGuid(), Guid.NewGuid(), "#room");
        view.ApplySynchronizedReadMarker(new NexIrcReadMarker("channel:#room", 8, "m8", 5, 1));
        var next = Entry("m9", 9);

        view.ApplySynchronizedReadMarker(new NexIrcReadMarker("channel:#room", 9, "m9", 4, 1));

        Assert.False(view.IsCoveredBySynchronizedReadMarker(next));
    }

    [Fact]
    public async Task ActiveLiveConversationAdvancesReadStateAndRemoteUpdatesDoNotEcho()
    {
        var endpoint = new IrcEndpoint("state.example", 6697, true);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(new NetworkConnectionOptions
        {
            DisplayName = endpoint.Host,
            Endpoint = endpoint,
            Nickname = "me",
            Username = "me",
            RealName = "Phase 50 test",
            Reconnect = new ReconnectPolicy(Enabled: false),
            RequestedCapabilities = ["message-tags", "nexirc/state"],
            SaslPolicy = SaslAuthenticationPolicy.Required,
            SaslCredentialProvider = new FixedSaslCredentialProvider()
        });

        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :message-tags sasl=PLAIN nexirc/state=1");
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("CAP REQ :", StringComparison.Ordinal)));
        transport.EnqueueInboundLine(":srv CAP * ACK :message-tags sasl=PLAIN nexirc/state=1");
        await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE PLAIN"));
        transport.EnqueueInboundLine(":srv AUTHENTICATE +");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0me\0secret"));
        await WaitForAsync(() => transport.OutboundLines.Contains($"AUTHENTICATE {payload}"));
        transport.EnqueueInboundLine(":srv 903 me :SASL successful");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP END") && transport.OutboundLines.Contains("NICK me"));
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        transport.EnqueueInboundLine(":srv NEXIRC STATE BEGIN 1 0");
        transport.EnqueueInboundLine(":srv NEXIRC STATE END 1");

        transport.EnqueueInboundLine(":me!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.Count == 1);
        var view = network.Channels[0];
        manager.ActivateView(view.Id);
        transport.EnqueueInboundLine("@msgid=m1;resume-seq=s1 :peer!u@h PRIVMSG #room :one");
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("NEXIRC STATE SET READ ", StringComparison.Ordinal)));
        var firstMutationCount = transport.OutboundLines.Count(line => line.StartsWith("NEXIRC STATE SET READ ", StringComparison.Ordinal));
        Assert.Equal(1, firstMutationCount);

        transport.EnqueueInboundLine(":srv NEXIRC STATE UPDATE READ Y2hhbm5lbDojcm9vbQ s1 bTE 1 1");
        await Task.Delay(50);
        Assert.Equal(firstMutationCount, transport.OutboundLines.Count(line => line.StartsWith("NEXIRC STATE SET READ ", StringComparison.Ordinal)));

        transport.EnqueueInboundLine("@msgid=m2;resume-seq=s2 :peer!u@h PRIVMSG #room :two");
        transport.EnqueueInboundLine("@msgid=m3;resume-seq=s3 :peer!u@h PRIVMSG #room :three");
        transport.EnqueueInboundLine("@msgid=m4;resume-seq=s4 :peer!u@h PRIVMSG #room :four");
        await WaitForAsync(() => transport.OutboundLines.Count(line => line.StartsWith("NEXIRC STATE SET READ ", StringComparison.Ordinal)) == 2);
        var lastMutation = transport.OutboundLines.Last(line => line.StartsWith("NEXIRC STATE SET READ ", StringComparison.Ordinal));
        Assert.Contains(" s4 ", lastMutation, StringComparison.Ordinal);
        await manager.DisconnectAsync(network.Id);
    }

    private static TranscriptEntry Entry(string messageId, long sequence, bool highlight = false, int epoch = 1) =>
        new(DateTimeOffset.UtcNow, TranscriptEntryKind.Message, "peer", messageId,
            Metadata: highlight ? "highlight" : null)
        {
            ServerMessageId = messageId,
            ServerSequence = sequence,
            ServerSessionEpoch = epoch
        };

    private sealed class FixedSaslCredentialProvider : ISaslCredentialProvider
    {
        public ValueTask<SaslCredential?> GetCredentialsAsync(IrcEndpoint endpoint, string mechanism,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<SaslCredential?>(new SaslCredential("me", "secret"));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The Phase 50 Application condition was not reached.");
            await Task.Delay(10);
        }
    }
}
