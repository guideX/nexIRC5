using System.Globalization;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase34NativeResumeTests
{
    [Fact]
    public async Task InitialConnectionNegotiatesOpaqueSessionAndRemainsNotRequired()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResume, []);
        await using var session = await StartRegisteredAsync(fixture);

        Assert.True(session.NativeResumeSupport.IsUsable);
        Assert.NotNull(session.NativeResumeSession);
        Assert.Equal("resume-0", session.NativeResumeSession!.AuthoritativeBoundary);
        Assert.Equal(ContinuityRecoveryResultKind.NotRequired, session.Continuity.RecoveryResult.Kind);
        Assert.DoesNotContain(session.ContinuityDiagnostics.Entries, item => (item.Detail ?? string.Empty).Contains(fixture.NativeResumeToken, StringComparison.Ordinal));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task NativeResumeEmitsHandshakeReplaysOrderedEventsAndCompletesStrongly()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResume,
        [
            Event("first", "one"),
            Event("second", "two")
        ]);
        await using var session = await StartRegisteredAsync(fixture);
        var raw = new List<string>();
        session.RawLineReceived += (_, item) => raw.Add(item.RawLine);
        var events = new List<SessionSemanticEvent>();
        session.SemanticEventReceived += (_, item) => events.Add(item);

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.True(result.Outcome == NexIrcResumeOutcome.Completed, result.Detail + Environment.NewLine + string.Join(Environment.NewLine, raw));
        Assert.True(result.ExactBoundaryRecovered);
        Assert.Equal(2, result.ReplayedEventCount);
        Assert.Equal("resume-2", result.FinalBoundary);
        Assert.Contains(fixture.NativeResumeRequests, line => line == "NEXIRC RESUME fixture-resume-token resume-0");
        Assert.Contains(fixture.Transport.OutboundLines, line => line.StartsWith("CAP REQ", StringComparison.Ordinal));
        Assert.Equal(2, events.Count(item => item.Event.IsHistorical && item.Event.Source == IrcSemanticEventSource.ServerPlayback));
        Assert.Contains(fixture.Transport.OutboundLines, line => line.StartsWith("CAP REQ", StringComparison.Ordinal) && line.Contains(NexIrcResumeProtocol.CapabilityName, StringComparison.Ordinal));
        Assert.Contains(raw, line => line.Contains("NEXIRC RESUME ACCEPT resume-0", StringComparison.Ordinal));
        Assert.Contains(raw, line => line.Contains("BATCH +resume", StringComparison.Ordinal) && line.Contains(NexIrcResumeProtocol.BatchType, StringComparison.Ordinal));
        Assert.Contains(raw, line => line.Contains("NEXIRC RESUME COMPLETE resume-2", StringComparison.Ordinal));
        Assert.DoesNotContain(raw, line => line.Contains(fixture.NativeResumeToken, StringComparison.Ordinal));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task LiveBoundaryIsCommittedAndHeldLiveTrafficFollowsReplayExactlyOnce()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResume, []);
        await using var session = await StartRegisteredAsync(fixture);
        var events = new List<SessionSemanticEvent>();
        session.SemanticEventReceived += (_, item) => events.Add(item);

        fixture.EnqueueLive(Event("before", "before"));
        await WaitForAsync(() => session.NativeResumeSession?.AuthoritativeBoundary == "resume-1");
        fixture.AddHistory(Event("missed", "missed"));

        fixture.EnqueueLiveDuringNextNativeReplay(Event("during", "during"));
        var pending = session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration).AsTask();
        var result = await pending;
        await WaitForAsync(() => events.Any(item => item.Event.Message.ServerMessageId == "during"));

        Assert.True(result.Outcome == NexIrcResumeOutcome.Completed, result.Detail);
        Assert.True(result.ReplayedEventCount == 1, $"replayed={result.ReplayedEventCount}; duplicate={result.DuplicateEventsSuppressed}; final={result.FinalBoundary}; requests={string.Join("|", fixture.NativeResumeRequests)}");
        Assert.Equal(2, events.Count(item => item.Event.Message.ServerMessageId is "missed" or "during"));
        Assert.Equal("resume-3", session.NativeResumeSession!.AuthoritativeBoundary);

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task DuplicateBoundaryReplayIsSuppressedWithoutAdvancingTwice()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResume, [Event("one", "one")]);
        await using var session = await StartRegisteredAsync(fixture);
        fixture.EnqueueLive(Event("one", "one"));
        await WaitForAsync(() => session.NativeResumeSession?.AuthoritativeBoundary == "resume-1");
        fixture.AddHistory(Event("two", "two"));

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.DuplicateEventsSuppressed);
        Assert.Equal("resume-2", result.FinalBoundary);

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Theory]
    [InlineData(DeterministicReplayProfile.NativeResumeRejects, NexIrcResumeRejectionReason.UnknownToken)]
    [InlineData(DeterministicReplayProfile.NativeResumeExpired, NexIrcResumeRejectionReason.ExpiredToken)]
    [InlineData(DeterministicReplayProfile.NativeResumeAccountMismatch, NexIrcResumeRejectionReason.AccountMismatch)]
    [InlineData(DeterministicReplayProfile.NativeResumeBoundaryTooOld, NexIrcResumeRejectionReason.BoundaryTooOld)]
    [InlineData(DeterministicReplayProfile.NativeResumeInvalidated, NexIrcResumeRejectionReason.SessionInvalidated)]
    [InlineData(DeterministicReplayProfile.NativeResumeServerRestarted, NexIrcResumeRejectionReason.ServerRestarted)]
    public async Task ResumeRejectionIsTypedAndFallbackSafeBeforeReplay(
        DeterministicReplayProfile profile,
        NexIrcResumeRejectionReason reason)
    {
        await using var fixture = CreateFixture(profile, []);
        await using var session = await StartRegisteredAsync(fixture);

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Rejected, result.Outcome);
        Assert.Equal(reason, result.RejectionReason);
        Assert.True(result.FallbackSafe);
        Assert.False(result.ReplayCompleted);

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task MalformedCompletionFailsWithoutClaimingStrongReplay()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResumeMalformedCompletion, [Event("one", "one")]);
        await using var session = await StartRegisteredAsync(fixture);

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Failed, result.Outcome);
        Assert.Equal(NexIrcResumeRejectionReason.Malformed, result.RejectionReason);
        Assert.False(result.ReplayCompleted);
        Assert.False(result.ExactBoundaryRecovered);

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task DisconnectDuringReplayFailsTheCurrentAttemptWithoutFalseCompletion()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResumeDisconnectDuringReplay,
        [Event("one", "one"), Event("two", "two")]);
        await using var session = await StartRegisteredAsync(fixture);

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Failed, result.Outcome);
        Assert.False(result.ReplayCompleted);
        Assert.False(result.ExactBoundaryRecovered);

        await session.Completion;
    }

    [Fact]
    public async Task NativeReplayRelationshipEventsUseTheExistingCanonicalPipeline()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResume,
        [
            new("#room", "alice", "parent-opaque", DateTimeOffset.Parse("2026-09-26T12:00:00Z", CultureInfo.InvariantCulture), Body: "parent"),
            new("#room", "bob", "reply-opaque", DateTimeOffset.Parse("2026-09-26T12:00:01Z", CultureInfo.InvariantCulture), Body: "reply", Tags: new Dictionary<string, string?>
            {
                ["+reply"] = "parent-opaque"
            }),
            new("#room", "carol", "reaction-opaque", DateTimeOffset.Parse("2026-09-26T12:00:02Z", CultureInfo.InvariantCulture), Command: "TAGMSG", Tags: new Dictionary<string, string?>
            {
                ["+reply"] = "parent-opaque",
                [IrcReaction.ReactTag] = "👍"
            }),
            new("#room", "carol", "unreaction-opaque", DateTimeOffset.Parse("2026-09-26T12:00:03Z", CultureInfo.InvariantCulture), Command: "TAGMSG", Tags: new Dictionary<string, string?>
            {
                ["+reply"] = "parent-opaque",
                [IrcReaction.UnreactTag] = "👍"
            })
        ]);
        await using var session = await StartRegisteredAsync(fixture);
        var events = new List<SessionSemanticEvent>();
        session.SemanticEventReceived += (_, item) => events.Add(item);

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Completed, result.Outcome);
        Assert.Contains(events, item => item.Event is IrcPrivmsgEvent message && message.Message.ReplyParentMessageId == "parent-opaque");
        Assert.Contains(events, item => item.Event is IrcReactionEvent reaction && reaction.Reaction.Operation == IrcReactionOperation.React);
        Assert.Contains(events, item => item.Event is IrcReactionEvent reaction && reaction.Reaction.Operation == IrcReactionOperation.Unreact);
        Assert.DoesNotContain(events, item => item.Event is IrcTagmsgEvent);

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task OrdinaryIrcServerReceivesNoNativeResumeCommands()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.FullIrcv3Replay, []);
        await using var session = await StartRegisteredAsync(fixture, expectNative: false);

        Assert.False(session.NativeResumeSupport.IsUsable);
        Assert.Null(session.NativeResumeSession);
        Assert.Empty(fixture.NativeResumeRequests);
        Assert.DoesNotContain(fixture.Transport.OutboundLines, line => line.Contains("NEXIRC", StringComparison.OrdinalIgnoreCase));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task OutOfOrderReplayFailsWithoutFalseCompletion()
    {
        await using var fixture = CreateFixture(DeterministicReplayProfile.NativeResumeOutOfOrder,
        [Event("one", "one"), Event("two", "two")]);
        await using var session = await StartRegisteredAsync(fixture);

        var result = await session.RequestNativeResumeAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Failed, result.Outcome);
        Assert.False(result.ReplayCompleted);
        Assert.False(result.ExactBoundaryRecovered);

        await session.DisconnectAsync();
        await session.Completion;
    }

    private static DeterministicServerHistoryFixture CreateFixture(
        DeterministicReplayProfile profile,
        IReadOnlyList<DeterministicServerHistoryEvent> history) =>
        new(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase34-native.example", 6667, false),
            Nickname = "nex",
            Profile = profile,
            History = history
        });

    private static DeterministicServerHistoryEvent Event(string id, string body) => new(
        "#room",
        "alice",
        id,
        DateTimeOffset.Parse("2026-09-26T12:00:00Z", CultureInfo.InvariantCulture).AddSeconds(id == "one" ? 1 : id == "two" ? 2 : 3),
        Body: body);

    private static async Task<ServerSession> StartRegisteredAsync(
        DeterministicServerHistoryFixture fixture,
        bool expectNative = true)
    {
        var factory = new FakeIrcTransportFactory();
        factory.Add(fixture.Transport);
        var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = fixture.Transport.Endpoint,
            Nickname = "nex",
            Username = "nex",
            RealName = "Phase 34 fixture",
            RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y,
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        _ = session.RunAsync();
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);
        if (expectNative)
        {
            await WaitForAsync(() => session.NativeResumeSession is not null);
        }
        return session;
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic Phase 34 native-resume condition was not reached.");
            }

            await Task.Delay(5);
        }
    }
}
