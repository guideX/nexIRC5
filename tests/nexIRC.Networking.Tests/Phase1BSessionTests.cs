using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Networking.Tests;

public sealed class Phase1BSessionTests
{
    [Fact]
    public async Task RepeatedJoinDoesNotMultiplyPendingJoinsAndPreRegistrationJoinWaitsForWelcome()
    {
        var endpoint = new IrcEndpoint("join-lifecycle.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        await session.JoinChannelAsync("#before-registration");
        Assert.DoesNotContain(transport.OutboundLines, line => line == "JOIN #before-registration");
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        await WaitForAsync(() => transport.OutboundLines.Count(line => line == "JOIN #before-registration") == 1);
        await session.JoinChannelAsync("#before-registration");
        Assert.Equal(1, transport.OutboundLines.Count(line => line == "JOIN #before-registration"));
        transport.EnqueueInboundLine(":me!u@h JOIN #before-registration");
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task LabeledListAndWhoisNumericsRetainRequestLabels()
    {
        var endpoint = new IrcEndpoint("labels.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var events = new List<IrcSemanticEvent>();
        session.SemanticEventReceived += (_, item) => events.Add(item.Event);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        transport.EnqueueInboundLine("@label=who-1 :srv 311 me Mira user host * :Real");
        transport.EnqueueInboundLine("@label=who-1 :srv 318 me Mira :End");
        transport.EnqueueInboundLine("@label=list-1 :srv 321 me Channel :Users Name");
        transport.EnqueueInboundLine("@label=list-1 :srv 322 me #room 2 :Topic");
        transport.EnqueueInboundLine("@label=list-1 :srv 323 me :End");
        await WaitForAsync(() => events.OfType<IrcListEndEvent>().Any() && events.OfType<IrcWhoisEvent>().Any(item => item.Numeric == 318));

        Assert.Equal("who-1", events.OfType<IrcWhoisEvent>().Single(item => item.Numeric == 311).RequestLabel);
        Assert.Equal("who-1", events.OfType<IrcWhoisEvent>().Single(item => item.Numeric == 318).RequestLabel);
        Assert.Equal("list-1", events.OfType<IrcListItemEvent>().Single().RequestLabel);
        Assert.Equal("list-1", events.OfType<IrcListEndEvent>().Single().RequestLabel);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task DesiredChannelsReplayAndResynchronizeAfterReconnect()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            DesiredChannels = new HashSet<string>(["#room"]),
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10))
        }, factory);

        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        first.EnqueueInboundLine(":srv CAP * LS :");
        first.EnqueueInboundLine(":srv 001 nex :Welcome");
        first.EnqueueInboundLine(":nex!u@h JOIN #room");
        first.EnqueueInboundLine(":srv 353 nex = #room :@nex bob");
        first.EnqueueInboundLine(":srv 366 nex #room :End of NAMES");
        await WaitForAsync(() => first.OutboundLines.Contains("WHO #room") && session.Snapshot.Channels.Single().Synchronization == ChannelSynchronizationState.Synchronized);

        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        second.EnqueueInboundLine(":srv CAP * LS :");
        second.EnqueueInboundLine(":srv 001 nex :Welcome");
        second.EnqueueInboundLine(":nex!u@h JOIN #room");
        second.EnqueueInboundLine(":srv 353 nex = #room :@nex carol");
        second.EnqueueInboundLine(":srv 366 nex #room :End of NAMES");
        await WaitForAsync(() => session.Snapshot.ConnectionGeneration == 2 && session.Snapshot.Channels.Single().Synchronization == ChannelSynchronizationState.Synchronized);

        Assert.Contains("JOIN #room", second.OutboundLines);
        Assert.Contains("NAMES #room", second.OutboundLines);
        Assert.Contains("TOPIC #room", second.OutboundLines);
        Assert.Contains("WHO #room", second.OutboundLines);
        Assert.Contains("carol", session.Snapshot.Channels.Single().Members.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("bob", session.Snapshot.Channels.Single().Members.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.False(session.Snapshot.Channels.Single().IsStale);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task CommonNumericRepliesAreTypedAndRetainMotdSnapshot()
    {
        var endpoint = new IrcEndpoint("test.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "nex",
            Username = "nex",
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var typed = new List<IrcSemanticEvent>();
        session.SemanticEventReceived += (_, item) => typed.Add(item.Event);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);

        transport.EnqueueInboundLine(":srv 375 nex :- server message of the day -");
        transport.EnqueueInboundLine(":srv 372 nex :- hello");
        transport.EnqueueInboundLine(":srv 376 nex :End of MOTD");
        transport.EnqueueInboundLine(":srv 321 nex Channel :Users Name");
        transport.EnqueueInboundLine(":srv 322 nex #room 2 :topic");
        transport.EnqueueInboundLine(":srv 323 nex :End of LIST");
        transport.EnqueueInboundLine(":srv 352 nex #room user host server nick H*@ :Real Name");
        transport.EnqueueInboundLine(":srv 315 nex #room :End of WHO");
        await WaitForAsync(() => typed.OfType<IrcWhoEndEvent>().Any());

        Assert.True(session.Snapshot.Motd.IsComplete);
        Assert.Equal(["- hello"], session.Snapshot.Motd.Lines);
        Assert.Contains(typed, item => item is IrcListStartEvent);
        Assert.Contains(typed, item => item is IrcListItemEvent list && list.Channel == "#room" && list.VisibleUsers == 2);
        Assert.Contains(typed, item => item is IrcWhoEvent who && who.Nickname == "nick");
        Assert.Contains(typed, item => item is IrcWhoEndEvent);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task SaslPlainNegotiatesBeforeRegistrationAndRedactsDiagnostics()
    {
        var endpoint = new IrcEndpoint("sasl.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        var credential = new SaslCredential("alice", "phase1b-secret");
        var provider = new StaticSaslCredentialProvider(credential);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "alice",
            Username = "alice",
            SaslPolicy = SaslAuthenticationPolicy.Required,
            SaslCredentialProvider = provider,
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var diagnostics = new List<OutboundIrcCommandEvent>();
        using var diagnosticCancellation = new CancellationTokenSource();
        var diagnosticReader = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in session.ReadOutboundEventsAsync(diagnosticCancellation.Token))
                {
                    diagnostics.Add(item);
                }
            }
            catch (OperationCanceledException) when (diagnosticCancellation.IsCancellationRequested)
            {
            }
        });

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :sasl=PLAIN");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP REQ :sasl"));
        transport.EnqueueInboundLine(":srv CAP * ACK :sasl");
        await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE PLAIN"));
        Assert.DoesNotContain("NICK alice", transport.OutboundLines);

        transport.EnqueueInboundLine(":srv AUTHENTICATE +");
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0alice\0phase1b-secret"));
        await WaitForAsync(() => transport.OutboundLines.Contains($"AUTHENTICATE {expected}"));
        transport.EnqueueInboundLine(":srv 903 alice :SASL authentication successful");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP END") && transport.OutboundLines.Contains("NICK alice"));
        transport.EnqueueInboundLine(":srv 001 alice :Welcome");
        await WaitForAsync(() => session.Snapshot.Registration == RegistrationState.Registered);

        Assert.Equal(SaslAuthenticationState.Succeeded, session.Snapshot.Authentication.State);
        Assert.True(credential.IsDisposed);
        Assert.DoesNotContain(diagnostics, item => item.RawLine.Contains("phase1b-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.RawLine.Contains(expected, StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => Encoding.UTF8.GetString(item.RawBytes.Span).Contains(expected, StringComparison.Ordinal));

        await session.DisconnectAsync();
        diagnosticCancellation.Cancel();
        await diagnosticReader;
        await run;
    }

    [Fact]
    public async Task SaslPlainUsesFourHundredByteChunksAndTerminalPlus()
    {
        var endpoint = new IrcEndpoint("sasl-chunks.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        var credential = new SaslCredential("u", new string('p', 297));
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "u",
            SaslPolicy = SaslAuthenticationPolicy.Optional,
            SaslCredentialProvider = new StaticSaslCredentialProvider(credential),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :sasl=PLAIN");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP REQ :sasl"));
        transport.EnqueueInboundLine(":srv CAP * ACK :sasl");
        await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE PLAIN"));
        transport.EnqueueInboundLine(":srv AUTHENTICATE +");
        await WaitForAsync(() => transport.OutboundLines.Count(line => line.StartsWith("AUTHENTICATE ", StringComparison.Ordinal) && line.Length > 100) == 1);

        var chunk = transport.OutboundLines.Single(line => line.StartsWith("AUTHENTICATE ", StringComparison.Ordinal) && line.Length > 100);
        Assert.Equal(400, chunk["AUTHENTICATE ".Length..].Length);
        await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE +"));
        transport.EnqueueInboundLine(":srv 903 u :success");
        await WaitForAsync(() => session.Snapshot.Authentication.State == SaslAuthenticationState.Succeeded);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task RequiredSaslUnavailablePreventsRegistrationWhileOptionalSaslContinues()
    {
        var endpoint = new IrcEndpoint("sasl-policy.example", 6667, false);
        var requiredTransport = new FakeIrcTransport(endpoint);
        var requiredFactory = new FakeIrcTransportFactory();
        requiredFactory.Add(requiredTransport);
        await using var required = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "required",
            SaslPolicy = SaslAuthenticationPolicy.Required,
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, requiredFactory);
        var requiredRun = required.RunAsync();
        await WaitForAsync(() => requiredTransport.ConnectCount == 1);
        requiredTransport.EnqueueInboundLine(":srv CAP * LS :server-time");
        await requiredRun;

        Assert.Equal(SaslAuthenticationState.Failed, required.Snapshot.Authentication.State);
        Assert.Equal(RegistrationState.Failed, required.Snapshot.Registration);
        Assert.DoesNotContain(requiredTransport.OutboundLines, line => line.StartsWith("NICK ", StringComparison.Ordinal));

        var optionalTransport = new FakeIrcTransport(endpoint);
        var optionalFactory = new FakeIrcTransportFactory();
        optionalFactory.Add(optionalTransport);
        await using var optional = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "optional",
            SaslPolicy = SaslAuthenticationPolicy.Optional,
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, optionalFactory);
        var optionalRun = optional.RunAsync();
        await WaitForAsync(() => optionalTransport.ConnectCount == 1);
        optionalTransport.EnqueueInboundLine(":srv CAP * LS :server-time");
        await WaitForAsync(() => optionalTransport.OutboundLines.Contains("NICK optional"));
        optionalTransport.EnqueueInboundLine(":srv 001 optional :Welcome");
        await WaitForAsync(() => optional.Snapshot.Registration == RegistrationState.Registered);

        Assert.Equal(SaslAuthenticationState.Skipped, optional.Snapshot.Authentication.State);
        await optional.DisconnectAsync();
        await optionalRun;
    }

    [Fact]
    public async Task RequiredSaslRejectionFailsRegistrationWithoutLeakingCredentials()
    {
        var endpoint = new IrcEndpoint("sasl-reject.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        var credential = new SaslCredential("reject-user", "reject-secret");
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "reject-user",
            SaslPolicy = SaslAuthenticationPolicy.Required,
            SaslCredentialProvider = new StaticSaslCredentialProvider(credential),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var diagnostics = new List<OutboundIrcCommandEvent>();
        using var diagnosticCancellation = new CancellationTokenSource();
        var diagnosticReader = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in session.ReadOutboundEventsAsync(diagnosticCancellation.Token))
                {
                    diagnostics.Add(item);
                }
            }
            catch (OperationCanceledException) when (diagnosticCancellation.IsCancellationRequested)
            {
            }
        });

        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :sasl=PLAIN");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP REQ :sasl"));
        transport.EnqueueInboundLine(":srv CAP * ACK :sasl");
        await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE PLAIN"));
        transport.EnqueueInboundLine(":srv AUTHENTICATE +");
        await WaitForAsync(() => transport.OutboundLines.Any(line => line.StartsWith("AUTHENTICATE ", StringComparison.Ordinal) && line != "AUTHENTICATE PLAIN"));
        transport.EnqueueInboundLine(":srv 904 reject-user :SASL authentication failed");

        await run;

        Assert.Equal(RegistrationState.Failed, session.Snapshot.Registration);
        Assert.Equal(SaslAuthenticationState.Failed, session.Snapshot.Authentication.State);
        Assert.DoesNotContain(transport.OutboundLines, line => line.StartsWith("NICK ", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.RawLine.Contains("reject-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, item => item.RawLine.Contains("reject-user", StringComparison.Ordinal) && item.RawLine.StartsWith("AUTHENTICATE ", StringComparison.Ordinal));
        Assert.True(credential.IsDisposed);

        diagnosticCancellation.Cancel();
        await diagnosticReader;
    }

    [Fact]
    public async Task NickMutationAndMembershipLifecycleUseAllObservedChannels()
    {
        var endpoint = new IrcEndpoint("membership.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            DesiredChannels = new HashSet<string>(["#keep"]),
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 005 me CASEMAPPING=rfc1459 PREFIX=(qaohv)~&@%+ :supported");
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        transport.EnqueueInboundLine(":me!u@h JOIN #keep");
        transport.EnqueueInboundLine(":remote!u@h JOIN #one");
        transport.EnqueueInboundLine(":remote!u@h JOIN #two");
        transport.EnqueueInboundLine(":srv MODE #one +q remote");
        transport.EnqueueInboundLine(":srv MODE #two +q remote");
        transport.EnqueueInboundLine(":remote!u@h NICK :renamed");
        await WaitForAsync(() => session.Snapshot.Channels.Count == 3 && session.Snapshot.Channels.All(channel => channel.Members.Values.All(member => member.Nickname != "remote")));

        var one = session.Snapshot.Channels.Single(channel => channel.Name == "#one");
        var two = session.Snapshot.Channels.Single(channel => channel.Name == "#two");
        Assert.Contains("renamed", one.Members.Keys);
        Assert.Contains('q', one.Members["renamed"].PrefixModes);
        Assert.Contains('q', two.Members["renamed"].PrefixModes);

        transport.EnqueueInboundLine(":me!u@h NICK :newme");
        await WaitForAsync(() => session.Snapshot.Nickname == "newme" && session.Snapshot.Channels.Single(channel => channel.Name == "#keep").Members.ContainsKey("newme"));
        transport.EnqueueInboundLine(":renamed!u@h PART #one :bye");
        transport.EnqueueInboundLine(":renamed!u@h QUIT :gone");
        transport.EnqueueInboundLine(":srv KICK #keep newme :removed");
        await WaitForAsync(() => session.Snapshot.Channels.All(channel => channel.Members.Keys.All(nickname => nickname is not "renamed" and not "newme")));

        var keep = session.Snapshot.Channels.Single(channel => channel.Name == "#keep");
        Assert.False(keep.IsJoined);
        Assert.Contains("#keep", session.Snapshot.DesiredChannels);
        await session.PartChannelAsync("#keep");
        Assert.DoesNotContain("#keep", session.Snapshot.DesiredChannels);
        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task AsciiCasemappingKeepsPunctuationDistinctInObservedMembers()
    {
        var endpoint = new IrcEndpoint("case.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            Reconnect = new ReconnectPolicy(Enabled: false)
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 005 me CASEMAPPING=ascii :supported");
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        transport.EnqueueInboundLine(":srv 353 me = #room :a[ a{");
        transport.EnqueueInboundLine(":srv 366 me #room :End");
        await WaitForAsync(() => session.Snapshot.Channels.Any(channel => channel.Members.Count == 2));

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task ReconnectRebuildsSaslAndObservedStateWithoutOldChannelEvidence()
    {
        var endpoint = new IrcEndpoint("resync.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            SaslPolicy = SaslAuthenticationPolicy.Required,
            SaslCredentialProvider = new FreshSaslCredentialProvider("me", "resync-secret"),
            DesiredChannels = new HashSet<string>(["#alpha", "#beta"]),
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10))
        }, factory);
        var run = session.RunAsync();
        await CompleteSaslRegistrationAsync(first, "me", "resync-secret");
        first.EnqueueInboundLine(":me!u@h JOIN #alpha");
        first.EnqueueInboundLine(":me!u@h JOIN #beta");
        first.EnqueueInboundLine(":OldAlphaUser!a@h JOIN #alpha");
        first.EnqueueInboundLine(":OldBetaUser!b@h JOIN #beta");
        first.EnqueueInboundLine(":srv TOPIC #alpha :old alpha topic");
        first.EnqueueInboundLine(":srv TOPIC #beta :old beta topic");
        first.EnqueueInboundLine(":srv MODE #alpha +nt");
        first.EnqueueInboundLine(":srv MODE #beta +k old-key");
        first.EnqueueInboundLine(":srv 353 me = #alpha :@me OldAlphaUser");
        first.EnqueueInboundLine(":srv 366 me #alpha :End");
        first.EnqueueInboundLine(":srv 353 me = #beta :@me OldBetaUser");
        first.EnqueueInboundLine(":srv 366 me #beta :End");
        first.EnqueueInboundLine(":srv 352 me #alpha a host server OldAlphaUser H :Old alpha");
        first.EnqueueInboundLine(":srv 315 me #alpha :End");
        await WaitForAsync(() => session.Snapshot.Channels.Count == 2 && session.Snapshot.Channels.All(channel => channel.Synchronization == ChannelSynchronizationState.Synchronized));

        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        var invalidated = session.Snapshot;
        Assert.Contains("#alpha", invalidated.DesiredChannels);
        Assert.Contains("#beta", invalidated.DesiredChannels);
        Assert.False(invalidated.Registration == RegistrationState.Registered);
        Assert.Empty(invalidated.Capabilities.Enabled);
        Assert.All(invalidated.Channels, channel =>
        {
            Assert.True(channel.IsStale);
            Assert.Empty(channel.Members);
            Assert.Null(channel.Topic);
            Assert.Empty(channel.Modes);
        });

        await CompleteSaslRegistrationAsync(second, "me", "resync-secret");
        second.EnqueueInboundLine(":me!u@h JOIN #alpha");
        second.EnqueueInboundLine(":me!u@h JOIN #beta");
        second.EnqueueInboundLine(":NewAlphaUser!a@h JOIN #alpha");
        second.EnqueueInboundLine(":NewBetaUser!b@h JOIN #beta");
        second.EnqueueInboundLine(":srv TOPIC #alpha :new alpha topic");
        second.EnqueueInboundLine(":srv TOPIC #beta :new beta topic");
        second.EnqueueInboundLine(":srv MODE #alpha +nt");
        second.EnqueueInboundLine(":srv MODE #beta +k new-key");
        second.EnqueueInboundLine(":srv 353 me = #alpha :@me NewAlphaUser");
        second.EnqueueInboundLine(":srv 366 me #alpha :End");
        second.EnqueueInboundLine(":srv 353 me = #beta :@me NewBetaUser");
        second.EnqueueInboundLine(":srv 366 me #beta :End");
        await WaitForAsync(() => session.Snapshot.ConnectionGeneration == 2 && session.Snapshot.Channels.All(channel => channel.Synchronization == ChannelSynchronizationState.Synchronized));

        var rebuilt = session.Snapshot;
        Assert.Equal(SaslAuthenticationState.Succeeded, rebuilt.Authentication.State);
        Assert.Contains("NewAlphaUser", rebuilt.Channels.Single(channel => channel.Name == "#alpha").Members.Keys);
        Assert.Contains("NewBetaUser", rebuilt.Channels.Single(channel => channel.Name == "#beta").Members.Keys);
        Assert.DoesNotContain("OldAlphaUser", rebuilt.Channels.SelectMany(channel => channel.Members.Keys));
        Assert.DoesNotContain("OldBetaUser", rebuilt.Channels.SelectMany(channel => channel.Members.Keys));
        Assert.Equal("new alpha topic", rebuilt.Channels.Single(channel => channel.Name == "#alpha").Topic);
        Assert.Contains('k', rebuilt.Channels.Single(channel => channel.Name == "#beta").Modes);
        Assert.Contains("JOIN #alpha", second.OutboundLines);
        Assert.Contains("NAMES #alpha", second.OutboundLines);
        Assert.Contains("TOPIC #alpha", second.OutboundLines);
        Assert.Contains("WHO #alpha", second.OutboundLines);

        await session.DisconnectAsync();
        await run;
    }

    [Fact]
    public async Task DelayedCallbacksFromAnOldTransportCannotMutateTheCurrentEpoch()
    {
        var endpoint = new IrcEndpoint("epoch.example", 6667, false);
        var first = new FakeIrcTransport(endpoint);
        var second = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(first);
        factory.Add(second);
        await using var session = new ServerSession(new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = "me",
            Reconnect = new ReconnectPolicy(true, 2, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(10))
        }, factory);
        var run = session.RunAsync();
        await WaitForAsync(() => first.ConnectCount == 1);
        first.EnqueueInboundLine(":srv CAP * LS :");
        first.EnqueueInboundLine(":srv 001 me :Welcome");
        first.EnqueueInboundLine(":me!u@h JOIN #old");
        await WaitForAsync(() => session.Snapshot.Channels.Any(channel => channel.Name == "#old" && channel.IsJoined));
        first.EnqueueRemoteDisconnect();
        await WaitForAsync(() => second.ConnectCount == 1);
        second.EnqueueInboundLine(":srv CAP * LS :");
        second.EnqueueInboundLine(":srv 001 me :Welcome");
        second.EnqueueInboundLine(":me!u@h JOIN #current");
        await WaitForAsync(() => session.Snapshot.ConnectionGeneration == 2 && session.Snapshot.Channels.Any(channel => channel.Name == "#current" && channel.IsJoined));
        var beforeCallbacks = session.Snapshot;

        await first.EmitCallbackAsync(new IrcTransportInboundLineCallback(":old!u@h JOIN #stale"));
        await first.EmitCallbackAsync(new IrcTransportFailureCallback(new ConnectionFailure(ConnectionFailureKind.Network, "old transport failure", IsTransient: false)));
        await first.EmitCallbackAsync(new IrcTransportDisconnectedCallback());

        var afterCallbacks = session.Snapshot;
        Assert.Equal(beforeCallbacks.ConnectionGeneration, afterCallbacks.ConnectionGeneration);
        Assert.Equal(beforeCallbacks.Registration, afterCallbacks.Registration);
        Assert.Equal(beforeCallbacks.Nickname, afterCallbacks.Nickname);
        Assert.DoesNotContain("#stale", afterCallbacks.Channels.Select(channel => channel.Name));
        Assert.Contains("#current", afterCallbacks.Channels.Select(channel => channel.Name));
        Assert.Null(afterCallbacks.LastFailure);

        await session.DisconnectAsync();
        await run;
    }

    private static async Task CompleteSaslRegistrationAsync(FakeIrcTransport transport, string nickname, string password)
    {
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :sasl=PLAIN");
        await WaitForAsync(() => transport.OutboundLines.Contains("CAP REQ :sasl"));
        transport.EnqueueInboundLine(":srv CAP * ACK :sasl");
        await WaitForAsync(() => transport.OutboundLines.Contains("AUTHENTICATE PLAIN"));
        transport.EnqueueInboundLine(":srv AUTHENTICATE +");
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{nickname}\0{password}"));
        await WaitForAsync(() => transport.OutboundLines.Contains($"AUTHENTICATE {payload}"));
        transport.EnqueueInboundLine($":srv 903 {nickname} :success");
        await WaitForAsync(() => transport.OutboundLines.Contains($"NICK {nickname}"));
        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private sealed class StaticSaslCredentialProvider(SaslCredential credential) : ISaslCredentialProvider
    {
        public ValueTask<SaslCredential?> GetCredentialsAsync(IrcEndpoint endpoint, string mechanism, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SaslCredential?>(credential);
        }
    }

    private sealed class FreshSaslCredentialProvider(string userName, string password) : ISaslCredentialProvider
    {
        public ValueTask<SaslCredential?> GetCredentialsAsync(IrcEndpoint endpoint, string mechanism, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<SaslCredential?>(new SaslCredential(userName, password));
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 3000)
    {
        var timeout = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= timeout)
            {
                throw new TimeoutException("The deterministic Phase 1B test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
