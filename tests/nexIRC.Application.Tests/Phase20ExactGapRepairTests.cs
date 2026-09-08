using System.Diagnostics;
using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase20ExactGapRepairTests
{
    [Fact]
    public void ExactGapEligibilityRejectsUnsafeIdentityReferenceAndOrderingCases()
    {
        var network = Guid.NewGuid();
        var conversation = "Channel:#room";
        var support = UsableSupport(ChathistoryReferenceType.MessageId, ChathistoryReferenceType.Timestamp);
        var older = Boundary(network, conversation, "b", "2026-09-07T12:00:00.000Z", 1, HistoryGapBoundaryProvenance.PreDisconnectCanonical);
        var newer = Boundary(network, conversation, "f", "2026-09-07T12:05:00.000Z", 1, HistoryGapBoundaryProvenance.PostReconnectCanonical);

        var valid = HistoryGapPolicy.Validate(older, newer, support, network, conversation, 1);
        Assert.True(valid.IsEligible);
        Assert.Equal(HistoryGapBoundaryTrust.ExactServerMessageId, valid.Trust);

        var crossNetwork = HistoryGapPolicy.Validate(older, newer with { NetworkId = Guid.NewGuid() }, support, network, conversation, 1);
        Assert.False(crossNetwork.IsEligible);

        var mixed = HistoryGapPolicy.Validate(older, newer with { Reference = ChathistoryReference.Timestamp("2026-09-07T12:05:00.000Z"), ServerMessageId = "f", TimestampSource = ConversationTimestampSource.ServerTime }, support, network, conversation, 1);
        Assert.False(mixed.IsEligible);

        var reversed = HistoryGapPolicy.Validate(newer, older, support, network, conversation, 1);
        Assert.False(reversed.IsEligible);

        var equalTimestamp = HistoryGapPolicy.Validate(
            older with { Reference = ChathistoryReference.Timestamp("2026-09-07T12:00:00.000Z"), ServerMessageId = null, TimestampSource = ConversationTimestampSource.ServerTime, DurableSequence = 0 },
            newer with { Reference = ChathistoryReference.Timestamp("2026-09-07T12:00:00.000Z"), ServerMessageId = null, TimestampSource = ConversationTimestampSource.ServerTime, DurableSequence = 0 },
            UsableSupport(ChathistoryReferenceType.Timestamp),
            network,
            conversation,
            1);
        Assert.False(equalTimestamp.IsEligible);
    }

    [Fact]
    public void DuplicateOnlyRepairStopsWithinRoundBudget()
    {
        var ledger = new HistoryGapLedger(new HistoryGapRepairBudget(MaximumRoundsPerGap: 1, MaximumEntriesPerGap: 2));
        var network = Guid.NewGuid();
        var conversation = "Channel:#room";
        var older = Boundary(network, conversation, "b", "2026-09-07T12:00:00.000Z", 1, HistoryGapBoundaryProvenance.PreDisconnectCanonical);
        var newer = Boundary(network, conversation, "f", "2026-09-07T12:05:00.000Z", 1, HistoryGapBoundaryProvenance.PostReconnectCanonical);
        Assert.True(ledger.TryDiscover(older, newer, "#room", UsableSupport(ChathistoryReferenceType.MessageId), 1, out var gap, out _));
        Assert.True(ledger.TryQueue(gap, out gap));
        gap = ledger.MarkRequesting(gap);
        Assert.False(ledger.HasRoundBudget(gap));
        gap = ledger.MarkTerminal(gap, HistoryGapRepairState.Exhausted, "zero progress");
        Assert.Equal(HistoryGapRepairState.Exhausted, gap.State);
        Assert.Equal(1, ledger.Diagnostics.Exhausted);
    }

    [Fact]
    public async Task ReconnectRepairsOnlyTheInteriorAndDoesNotRepeatAfterConvergence()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("phase20-gap.example", 6667, false));
        factory.Add(first);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(first.Endpoint));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var channel = manager.EnsureChannel(network.Id, "#room");
        first.EnqueueInboundLine(":nex!u@h JOIN #room");
        await WaitForAsync(() => channel.IsJoined);
        first.EnqueueInboundLine("@msgid=a;time=2026-09-07T12:00:00.000Z :alice!u@h PRIVMSG #room :A");
        first.EnqueueInboundLine("@msgid=b;time=2026-09-07T12:01:00.000Z :alice!u@h PRIVMSG #room :B");
        await WaitForAsync(() => channel.EntriesSnapshot.Count(item => item.ServerMessageId is not null) == 2);

        var replacement = new FakeIrcTransport(first.Endpoint);
        factory.Add(replacement);
        await manager.ReconnectAsync(network.Id);
        await WaitForAsync(() => replacement.ConnectCount == 1);
        Register(replacement, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        replacement.EnqueueInboundLine("@msgid=f;time=2026-09-07T12:05:00.000Z :alice!u@h PRIVMSG #room :F");
        await Task.Delay(750);
        Assert.True(
            replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BETWEEN #room msgid=b msgid=f ", StringComparison.Ordinal)),
            $"outbound={string.Join(" | ", replacement.OutboundLines)}; gaps={string.Join(" | ", manager.GetHistoryGaps(network.Id).Select(gap => $"{gap.State}:{gap.LastReason}"))}");

        replacement.EnqueueInboundLine("@draft/chathistory-end :srv BATCH +gap chathistory #room");
        replacement.EnqueueInboundLine("@batch=gap;msgid=c;time=2026-09-07T12:02:00.000Z :alice!u@h PRIVMSG #room :C");
        replacement.EnqueueInboundLine("@batch=gap;msgid=d;time=2026-09-07T12:03:00.000Z :alice!u@h PRIVMSG #room :D");
        replacement.EnqueueInboundLine("@batch=gap;msgid=e;time=2026-09-07T12:04:00.000Z :alice!u@h PRIVMSG #room :E");
        replacement.EnqueueInboundLine("@batch=gap;msgid=b;time=2026-09-07T12:01:00.000Z :alice!u@h PRIVMSG #room :B");
        replacement.EnqueueInboundLine(":srv BATCH -gap");

        await WaitForAsync(() => channel.EntriesSnapshot.Where(item => item.ServerMessageId is not null).Select(item => item.Text).SequenceEqual(["A", "B", "C", "D", "E", "F"]));
        await WaitForAsync(() => manager.GetHistoryGaps(network.Id).Single().State == HistoryGapRepairState.Repaired);
        Assert.Equal(HistoryGapRepairState.Repaired, manager.GetHistoryGaps(network.Id).Single().State);
        Assert.Equal(1, replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal)));

        var before = replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal));
        await Task.Delay(50);
        Assert.Equal(before, replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AccountBackedQueryNickChangeRepairsTheSameDurableQueryAndRejectsConflictingAccount()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("phase20-query.example", 6667, false));
        factory.Add(first);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(first.Endpoint));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => first.ConnectCount == 1);
        Register(first, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var query = manager.EnsureQuery(network.Id, "Alice");
        first.EnqueueInboundLine("@account=alice123;msgid=b;time=2026-09-07T12:01:00.000Z :Alice!u@h PRIVMSG nex :B");
        await WaitForAsync(() => query.IdentityEvidence.HasAccount("alice123") && query.EntryCount == 1);

        var replacement = new FakeIrcTransport(first.Endpoint);
        factory.Add(replacement);
        await manager.ReconnectAsync(network.Id);
        await WaitForAsync(() => replacement.ConnectCount == 1);
        Register(replacement, "nex");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        replacement.EnqueueInboundLine("@account=alice123;msgid=f;time=2026-09-07T12:05:00.000Z :Alicia!u@h PRIVMSG nex :F");
        await Task.Delay(750);
        Assert.True(
            replacement.OutboundLines.Any(line => line.StartsWith("CHATHISTORY BETWEEN Alicia msgid=b msgid=f ", StringComparison.Ordinal)),
            $"outbound={string.Join(" | ", replacement.OutboundLines)}; queries={string.Join(" | ", network.Queries.Select(item => $"{item.Nickname}:{item.HistoryConversationKey}:{item.EntryCount}"))}; gaps={string.Join(" | ", manager.GetHistoryGaps(network.Id).Select(gap => $"{gap.State}:{gap.LastReason}"))}");
        replacement.EnqueueInboundLine("@draft/chathistory-end :srv BATCH +querygap chathistory Alicia");
        replacement.EnqueueInboundLine("@batch=querygap;msgid=c;time=2026-09-07T12:03:00.000Z :Alicia!u@h PRIVMSG nex :C");
        replacement.EnqueueInboundLine(":srv BATCH -querygap");

        await WaitForAsync(() => query.Nickname == "Alicia" && query.EntriesSnapshot.Where(item => item.ServerMessageId is not null).Select(item => item.Text).SequenceEqual(["B", "C", "F"]));
        await WaitForAsync(() => manager.GetHistoryGaps(network.Id).Single().State == HistoryGapRepairState.Repaired);
        Assert.Single(network.Queries);

        var betweenCount = replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal));
        replacement.EnqueueInboundLine("@account=other;msgid=conflict;time=2026-09-07T12:06:00.000Z :Alice!u@h PRIVMSG nex :conflict");
        await WaitForAsync(() => network.Queries.Count == 2);
        Assert.DoesNotContain(query.EntriesSnapshot, item => item.Text == "conflict");
        Assert.Equal(betweenCount, replacement.OutboundLines.Count(line => line.StartsWith("CHATHISTORY BETWEEN", StringComparison.Ordinal)));
    }

    private static HistoryGapBoundary Boundary(Guid network, string conversation, string id, string timestamp, int generation, HistoryGapBoundaryProvenance provenance) => new()
    {
        NetworkId = network,
        Conversation = conversation,
        Reference = ChathistoryReference.MessageId(id),
        Timestamp = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture),
        TimestampSource = ConversationTimestampSource.ServerTime,
        ServerMessageId = id,
        Provenance = provenance,
        ConnectionGeneration = generation
    };

    private static ChathistorySupport UsableSupport(params ChathistoryReferenceType[] references) => new()
    {
        CapabilityEnabled = true,
        BatchEnabled = true,
        ServerTimeEnabled = true,
        MessageTagsEnabled = true,
        SupportedReferenceTypes = references
    };

    private static NetworkConnectionOptions Options(IrcEndpoint endpoint) => new()
    {
        DisplayName = "Phase 20",
        Endpoint = endpoint,
        Nickname = "nex",
        Username = "nex",
        RealName = "Phase 20 test",
        RequestedCapabilities = [IrcCapabilityCatalog.Chathistory],
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname)
    {
        transport.EnqueueInboundLine(":srv CAP * LS :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv CAP * ACK :batch draft/chathistory message-tags server-time");
        transport.EnqueueInboundLine(":srv 005 nex CHATHISTORY=50 MSGREFTYPES=msgid,timestamp CHANTYPES=# PREFIX=(ov)@+ :supported");
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 5_000)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeoutMilliseconds / 1000d * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 20 condition did not complete.");
            }

            await Task.Delay(5).ConfigureAwait(false);
        }
    }
}
