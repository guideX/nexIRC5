using System.Globalization;
using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase37ProtectedResumeTests
{
    [Fact]
    public async Task ExplicitNewAttachmentUsesProtectedGrantAndRetainsIndependentAttachmentCredential()
    {
        var store = new InMemoryResumeStateStore();
        var protector = new TestResumeSecretProtector();
        var networkId = Guid.Parse("e8b72083-5899-48c3-8a9a-714c398cc6fd");
        await using var fixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase40-attachment.example", 6697, true),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeAttachmentsEnabled = true,
            NativeSessionCredential = "phase40-session-grant",
            NativeAttachmentSessionCredential = "phase40-secondary-grant",
            NativeAttachmentToken = "phase40-secondary-token"
        });
        await using var session = CreateSession(fixture, networkId, store, protector);
        var raw = new List<string>();
        var outbound = new List<string>();
        session.RawLineReceived += (_, item) => raw.Add(item.RawLine);
        session.OutboundCommandSent += (_, item) => outbound.Add(item.RawLine);

        _ = session.RunAsync();
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered
            && session.NativeResumeSession?.SessionCredential is not null
            && session.NativeResumeSession.AttachmentId is not null);
        var primaryAttachmentId = session.NativeResumeSession!.AttachmentId;
        var initial = await store.LoadAsync(ResumeStateIdentity.For(fixture.Transport.Endpoint, networkId));
        Assert.True(initial.IsUsable);
        Assert.Equal("phase40-session-grant", protector.Unprotect(initial.State!.ProtectedSessionCredential!, initial.State.NetworkIdentity));

        var result = await session.RequestNativeAttachmentAsync(session.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Completed, result.Outcome);
        Assert.True(result.AttachmentCreated);
        Assert.Equal("phase40-secondary-token", session.NativeResumeSession!.Token);
        Assert.Equal(fixture.NativeAttachmentId, session.NativeResumeSession.AttachmentId);
        Assert.NotEqual(primaryAttachmentId, session.NativeResumeSession.AttachmentId);
        Assert.Equal("phase40-secondary-grant", session.NativeResumeSession.SessionCredential);
        Assert.Contains(fixture.NativeAttachmentRequests, line => line == "NEXIRC ATTACH phase40-session-grant resume-0");
        Assert.DoesNotContain(raw, line => line.Contains("phase40-session-grant", StringComparison.Ordinal)
            || line.Contains("phase40-secondary-token", StringComparison.Ordinal));
        Assert.DoesNotContain(outbound, line => line.Contains("phase40-session-grant", StringComparison.Ordinal));

        var persisted = await store.LoadAsync(ResumeStateIdentity.For(fixture.Transport.Endpoint, networkId));
        Assert.True(persisted.IsUsable);
        Assert.Equal(fixture.NativeAttachmentId, persisted.State!.AttachmentId);
        Assert.Equal("phase40-secondary-token", protector.Unprotect(persisted.State.ProtectedCurrentToken, persisted.State.NetworkIdentity));
        Assert.Equal("phase40-secondary-grant", protector.Unprotect(persisted.State.ProtectedSessionCredential!, persisted.State.NetworkIdentity));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task AttachmentGrantAndIndependentCredentialAreProtectedAndAttachedThroughReplay()
    {
        var store = new InMemoryResumeStateStore();
        var protector = new TestResumeSecretProtector();
        var networkId = Guid.Parse("4a3d9d6b-fbcf-4acf-a557-6307d44be00b");
        await using var fixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase40-attach.example", 6697, true),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeAttachmentsEnabled = true,
            NativeSessionCredential = "phase40-session-grant",
            NativeAttachmentSessionCredential = "phase40-secondary-grant",
            NativeAttachmentToken = "phase40-secondary-token"
        });
        await using var session = CreateSession(fixture, networkId, store, protector);
        var publicRaw = new List<string>();
        var publicOutbound = new List<string>();
        session.RawLineReceived += (_, item) => publicRaw.Add(item.RawLine);
        session.OutboundCommandSent += (_, item) => publicOutbound.Add(item.RawLine);

        _ = session.RunAsync();
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered
            && session.NativeResumeSession?.SessionCredential is not null
            && session.NativeResumeSession.AttachmentId is not null);

        var primaryId = session.NativeResumeSession!.AttachmentId;
        var initialState = await store.LoadAsync(ResumeStateIdentity.For(fixture.Transport.Endpoint, networkId));
        Assert.True(initialState.IsUsable);
        Assert.Equal("phase40-session-grant", protector.Unprotect(initialState.State!.ProtectedSessionCredential!, initialState.State.NetworkIdentity));
        Assert.DoesNotContain("phase40-session-grant", Convert.ToBase64String(initialState.State.ProtectedSessionCredential!), StringComparison.Ordinal);

        var result = await session.RequestNativeAttachmentAsync(session.Snapshot.ConnectionGeneration);
        var current = session.NativeResumeSession!;

        Assert.Equal(NexIrcResumeOutcome.Completed, result.Outcome);
        Assert.True(result.AttachmentCreated);
        Assert.NotEqual(primaryId, current.AttachmentId);
        Assert.Equal(fixture.NativeAttachmentId, current.AttachmentId);
        Assert.Equal("phase40-secondary-token", current.Token);
        Assert.Equal("phase40-secondary-grant", current.SessionCredential);
        Assert.Contains(fixture.NativeAttachmentRequests, line => line == "NEXIRC ATTACH phase40-session-grant resume-0");
        Assert.DoesNotContain(publicRaw, line => line.Contains("phase40-session-grant", StringComparison.Ordinal)
            || line.Contains("phase40-secondary-token", StringComparison.Ordinal));
        Assert.DoesNotContain(publicOutbound, line => line.Contains("phase40-session-grant", StringComparison.Ordinal));

        var persisted = await store.LoadAsync(ResumeStateIdentity.For(fixture.Transport.Endpoint, networkId));
        Assert.True(persisted.IsUsable);
        Assert.Equal(current.AttachmentId, persisted.State!.AttachmentId);
        Assert.Equal("phase40-secondary-token", protector.Unprotect(persisted.State.ProtectedCurrentToken, persisted.State.NetworkIdentity));
        Assert.Equal("phase40-secondary-grant", protector.Unprotect(persisted.State.ProtectedSessionCredential!, persisted.State.NetworkIdentity));

        await session.DisconnectAsync();
        await session.Completion;
    }

    [Fact]
    public async Task FreshSessionInstanceLoadsProtectedStateAndResumesExactGap()
    {
        var store = new InMemoryResumeStateStore();
        var protector = new TestResumeSecretProtector();
        var networkId = Guid.Parse("7b5f7fb0-1a56-4f89-b4c5-f09c0b6d3b37");
        var initialEvent = Event("one", "one");

        await using (var firstFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase37-protected.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "phase37-token-A"
        }))
        await using (var first = CreateSession(firstFixture, networkId, store, protector))
        {
            _ = first.RunAsync();
            await WaitForAsync(() => first.Snapshot.Registration == RegistrationState.Registered && first.NativeResumeSession is not null);
            firstFixture.EnqueueLive(initialEvent);
            await WaitForAsync(() => first.NativeResumeSession?.AuthoritativeBoundary == "resume-1");
            var persisted = await store.LoadAsync(ResumeStateIdentity.For(firstFixture.Transport.Endpoint, networkId));
            Assert.True(persisted.IsUsable);
            Assert.DoesNotContain("phase37-token-A", Convert.ToBase64String(persisted.State!.ProtectedCurrentToken), StringComparison.Ordinal);
            await first.DisconnectAsync();
            await first.Completion;
        }

        await using var secondFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase37-protected.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "phase37-token-A"
        });
        await using var second = CreateSession(secondFixture, networkId, store, protector);
        var redactedOutbound = new List<string>();
        second.OutboundCommandSent += (_, item) => redactedOutbound.Add(item.RawLine);
        _ = second.RunAsync();
        await WaitForAsync(() => second.Snapshot.Registration == RegistrationState.Registered && second.NativeResumeSession is not null);
        secondFixture.AddHistory(initialEvent, Event("two", "two"));

        var result = await second.RequestNativeResumeAsync(second.Snapshot.ConnectionGeneration);

        Assert.True(result.Outcome == NexIrcResumeOutcome.Completed, $"{result.Outcome}: {result.Detail}; request={string.Join("|", secondFixture.NativeResumeRequests)}");
        Assert.Equal(1, result.ReplayedEventCount);
        Assert.Equal("resume-2", result.FinalBoundary);
        Assert.Contains(secondFixture.NativeResumeRequests, request => request.Contains("resume-1", StringComparison.Ordinal));
        Assert.DoesNotContain(redactedOutbound, request => request.Contains("phase37-token-A", StringComparison.Ordinal));
        Assert.Equal("resume-2", second.NativeResumeSession!.AuthoritativeBoundary);
    }

    [Fact]
    public async Task PermanentResumeRejectionAdoptsFreshSessionAndReplacesProtectedState()
    {
        var store = new InMemoryResumeStateStore();
        var protector = new TestResumeSecretProtector();
        var networkId = Guid.Parse("2d0b28a6-1dc2-4b2a-92d2-6dc17fc5ca35");

        await using (var firstFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase37-rejection.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "old-token"
        }))
        await using (var first = CreateSession(firstFixture, networkId, store, protector))
        {
            _ = first.RunAsync();
            await WaitForAsync(() => first.Snapshot.Registration == RegistrationState.Registered && first.NativeResumeSession is not null);
            await first.DisconnectAsync();
            await first.Completion;
        }

        await using var secondFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase37-rejection.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResumeRejects,
            NativeResumeToken = "fresh-token"
        });
        await using var second = CreateSession(secondFixture, networkId, store, protector);
        _ = second.RunAsync();
        await WaitForAsync(() => second.Snapshot.Registration == RegistrationState.Registered && second.NativeResumeSession is not null);

        var result = await second.RequestNativeResumeAsync(second.Snapshot.ConnectionGeneration);

        Assert.Equal(NexIrcResumeOutcome.Rejected, result.Outcome);
        Assert.Equal(NexIrcResumeRejectionReason.UnknownToken, result.RejectionReason);
        Assert.Equal("fresh-token", second.NativeResumeSession!.Token);
        var persisted = await store.LoadAsync(ResumeStateIdentity.For(secondFixture.Transport.Endpoint, networkId));
        Assert.True(persisted.IsUsable);
        Assert.Equal("fresh-token", protector.Unprotect(persisted.State!.ProtectedCurrentToken, persisted.State.NetworkIdentity));
    }

    [Fact]
    public async Task WrongAccountAndProtectionFailureDoNotOfferTheStoredCredential()
    {
        var store = new InMemoryResumeStateStore();
        var protector = new TestResumeSecretProtector();
        var networkId = Guid.Parse("9fdc30b6-9cbb-4a9f-9a54-6d6a38c23735");
        var endpoint = new IrcEndpoint("phase37-binding.example", 6667, false);

        await using (var firstFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = endpoint,
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "bound-token"
        }))
        await using (var first = CreateSession(firstFixture, networkId, store, protector))
        {
            _ = first.RunAsync();
            await WaitForAsync(() => first.Snapshot.Registration == RegistrationState.Registered && first.NativeResumeSession is not null);
            await first.DisconnectAsync();
            await first.Completion;
        }

        await using (var wrongNetworkFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("different-network.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "network-fresh-token"
        }))
        await using (var wrongNetwork = CreateSession(wrongNetworkFixture, networkId, store, protector))
        {
            _ = wrongNetwork.RunAsync();
            await WaitForAsync(() => wrongNetwork.Snapshot.Registration == RegistrationState.Registered && wrongNetwork.NativeResumeSession is not null);
            Assert.Equal("network-fresh-token", wrongNetwork.NativeResumeSession!.Token);
            Assert.Empty(wrongNetworkFixture.NativeResumeRequests);
        }

        await using (var wrongAccountFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = endpoint,
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "account-fresh-token"
        }))
        await using (var wrongAccount = CreateSession(wrongAccountFixture, networkId, store, protector, "different-account"))
        {
            _ = wrongAccount.RunAsync();
            await WaitForAsync(() => wrongAccount.Snapshot.Registration == RegistrationState.Registered && wrongAccount.NativeResumeSession is not null);
            Assert.Equal("account-fresh-token", wrongAccount.NativeResumeSession!.Token);
            Assert.Empty(wrongAccountFixture.NativeResumeRequests);
        }

        var identity = ResumeStateIdentity.For(endpoint, networkId);
        var current = await store.LoadAsync(identity);
        await store.SaveAsync(current.State! with { ProtectedCurrentToken = [0x01, 0x02, 0x03] });

        await using var corruptFixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = endpoint,
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "protection-fresh-token"
        });
        await using var corrupt = CreateSession(corruptFixture, networkId, store, protector);
        _ = corrupt.RunAsync();
        await WaitForAsync(() => corrupt.Snapshot.Registration == RegistrationState.Registered && corrupt.NativeResumeSession is not null);

        Assert.Equal("protection-fresh-token", corrupt.NativeResumeSession!.Token);
        Assert.Empty(corruptFixture.NativeResumeRequests);
    }

    [Fact]
    public async Task PersistenceFailureKeepsOldCredentialAndSuppressesRotationAck()
    {
        var store = new InMemoryResumeStateStore { IsAvailable = false };
        var protector = new TestResumeSecretProtector();
        var networkId = Guid.Parse("1ea90c0e-9dc9-4fd0-8888-e6e89fd58a3e");
        await using var fixture = new DeterministicServerHistoryFixture(new DeterministicHistoryFixtureOptions
        {
            Endpoint = new IrcEndpoint("phase37-write-failure.example", 6667, false),
            Profile = DeterministicReplayProfile.NativeResume,
            NativeResumeToken = "write-failure-old-token"
        });
        await using var session = CreateSession(fixture, networkId, store, protector);
        _ = session.RunAsync();
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered && session.NativeResumeSession is not null);

        fixture.Transport.EnqueueInboundLine(":deterministic.fixture NEXIRC SESSION ROTATE replacement-token resume-0 2");
        await WaitForAsync(() => session.ContinuityDiagnostics.Entries.Any(item => (item.Detail ?? string.Empty).Contains("Token rotation was not acknowledged", StringComparison.Ordinal)));

        Assert.DoesNotContain(fixture.Transport.OutboundLines, line => line == "NEXIRC SESSION ACK 2");
        Assert.Equal("write-failure-old-token", session.NativeResumeSession!.Token);
    }

    [Fact]
    public void RotationStateKeepsProtectedOldAndPendingCredentialsDistinct()
    {
        var first = new NexIrcResumeSession("old-token", "s1", 1);
        var rotated = first.Rotate("pending-token", "s1", 2);

        Assert.True(rotated.HasPendingRotation);
        Assert.Equal("old-token", rotated.DurableCurrentToken);
        Assert.Equal("pending-token", rotated.Token);
        Assert.Equal(1, rotated.DurableCurrentGeneration);
        Assert.Equal(2, rotated.EstablishedGeneration);
        Assert.Equal("pending-token", rotated.MarkRotationAcknowledged().Token);
        Assert.False(rotated.MarkRotationAcknowledged().HasPendingRotation);
    }

    [Fact]
    public void ParsedAndPrefixedAuthenticationDiagnosticsAreRedacted()
    {
        Assert.Equal("AUTHENTICATE <redacted>", IrcSensitiveData.RedactLine(":server AUTHENTICATE sasl-secret"));
        var parsed = IrcMessageParser.Parse(":server NEXIRC SESSION phase37-token-A resume-1").Message!;
        var redacted = IrcSensitiveData.RedactParsedMessage(parsed);

        Assert.DoesNotContain("phase37-token-A", redacted.RawLine, StringComparison.Ordinal);
        Assert.DoesNotContain("phase37-token-A", string.Join(' ', redacted.Parameters), StringComparison.Ordinal);
    }

    private static ServerSession CreateSession(
        DeterministicServerHistoryFixture fixture,
        Guid networkId,
        IResumeStateStore store,
        IResumeSecretProtector protector,
        string username = "nex") =>
        new(new ServerSessionOptions
        {
            NetworkId = networkId,
            ResumeStateStore = store,
            ResumeSecretProtector = protector,
            Endpoint = fixture.Transport.Endpoint,
            Nickname = "nex",
            Username = username,
            RealName = "Phase 37 protected resume",
            RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y,
            Reconnect = new ReconnectPolicy(Enabled: false)
        },
        new FakeIrcTransportFactoryWith(fixture.Transport));

    private static DeterministicServerHistoryEvent Event(string id, string body) => new(
        "#room",
        "alice",
        id,
        DateTimeOffset.Parse("2026-09-26T12:00:00Z", CultureInfo.InvariantCulture),
        Body: body);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 37 protected-resume condition was not reached.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class TestResumeSecretProtector : IResumeSecretProtector
    {
        public byte[] Protect(string secret, string protectionContext) =>
            Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) + ":" + protectionContext.Length.ToString(CultureInfo.InvariantCulture));

        public string Unprotect(ReadOnlySpan<byte> protectedSecret, string protectionContext)
        {
            var encoded = Encoding.UTF8.GetString(protectedSecret).Split(':', 2)[0];
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
    }

    private sealed class FakeIrcTransportFactoryWith(FakeIrcTransport transport) : IIrcTransportFactory
    {
        public ValueTask<IIrcTransport> CreateAsync(IrcEndpoint endpoint, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IIrcTransport>(transport);
        }
    }
}
