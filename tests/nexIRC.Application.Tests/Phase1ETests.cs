using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1ETests
{
    [Fact]
    public void DefaultsAreVersionedAndNotificationsAreOptIn()
    {
        var configuration = ConfigurationValidator.Defaults();

        Assert.Equal(ConfigurationSchema.CurrentVersion, configuration.SchemaVersion);
        Assert.Empty(configuration.Profiles);
        Assert.False(configuration.Preferences.NotificationsEnabled);
        Assert.True(configuration.Preferences.HighlightNickname);
    }

    [Fact]
    public async Task JsonConfigurationRoundTripsProfilesPreferencesAndOmitsSecrets()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1e-");
        try
        {
            var path = Path.Combine(directory.FullName, "configuration.json");
            var store = new JsonConfigurationStore(path);
            var id = Guid.NewGuid();
            var configuration = new NexIrcConfiguration
            {
                Preferences = new ApplicationPreferences
                {
                    NotificationsEnabled = true,
                    HighlightNickname = false,
                    CustomHighlightWords = ["nexIRC", "build"]
                },
                Profiles =
                [
                    new NetworkProfile
                    {
                        Id = id,
                        DisplayName = "Alpha",
                        Host = "irc.example",
                        Port = 6697,
                        Nickname = "Alice",
                        AutoJoinChannels = ["#Room", "#room", "#other"],
                        AlternateNicknames = ["Alice_", "Alice__"],
                        AutoConnect = true
                    }
                ]
            };

            await store.SaveAsync(configuration);
            var loaded = await store.LoadAsync();

            Assert.False(loaded.UsedDefaults);
            Assert.Equal(id, Assert.Single(loaded.Configuration.Profiles).Id);
            Assert.Equal(["#Room", "#other"], loaded.Configuration.Profiles[0].AutoJoinChannels);
            Assert.True(loaded.Configuration.Preferences.NotificationsEnabled);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sasl", json, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(directory.FullName, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task MalformedAndUnsupportedConfigurationRecoverWithEvidence()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1e-");
        try
        {
            var path = Path.Combine(directory.FullName, "configuration.json");
            await File.WriteAllTextAsync(path, "{ not valid json");
            var store = new JsonConfigurationStore(path);
            var malformed = await store.LoadAsync();
            Assert.True(malformed.UsedDefaults);
            Assert.NotNull(malformed.EvidencePath);
            Assert.True(File.Exists(malformed.EvidencePath));

            await File.WriteAllTextAsync(path, "{\"schemaVersion\":99,\"profiles\":[]}");
            var unsupported = await store.LoadAsync();
            Assert.True(unsupported.UsedDefaults);
            Assert.Contains("not supported", unsupported.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task OversizedConfigurationIsBoundedAndDoesNotDeserialize()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1e-");
        try
        {
            var path = Path.Combine(directory.FullName, "configuration.json");
            await File.WriteAllTextAsync(path, new string('x', 300));
            var result = await new JsonConfigurationStore(path, maximumFileBytes: 256).LoadAsync();
            Assert.True(result.UsedDefaults);
            Assert.Contains("size limit", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task MissingOptionalProfileFieldsUseSaneDefaults()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1e-");
        try
        {
            var path = Path.Combine(directory.FullName, "configuration.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"profiles\":[{\"host\":\"irc.example\",\"nickname\":\"Nex\"}]}");
            var result = await new JsonConfigurationStore(path).LoadAsync();
            var profile = Assert.Single(result.Configuration.Profiles);

            Assert.Equal(6697, profile.Port);
            Assert.True(profile.UseTls);
            Assert.Equal("nexirc", profile.Username);
            Assert.Equal("nexIRC 5", profile.RealName);
            Assert.NotEqual(Guid.Empty, profile.Id);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task ProfileRepositoryPreservesStableIdsAcrossAddEditDelete()
    {
        var store = new InMemoryConfigurationStore();
        var service = new ConfigurationService(store);
        await service.LoadAsync();
        var id = Guid.NewGuid();
        Assert.True(service.Profiles.AddOrUpdate(new NetworkProfile { Id = id, DisplayName = "Alpha", Host = "a.example", Nickname = "a" }));
        Assert.True(service.Profiles.AddOrUpdate(new NetworkProfile { Id = id, DisplayName = "Renamed", Host = "a.example", Nickname = "a" }));
        Assert.Equal(id, Assert.Single(service.Profiles.Profiles).Id);
        Assert.Equal("Renamed", service.Profiles.Profiles[0].DisplayName);
        Assert.True(service.Profiles.Remove(id));
        Assert.Empty(service.Profiles.Profiles);
    }

    [Fact]
    public void ProfileNormalizationDeduplicatesChannelsAndBoundsAlternateNicknames()
    {
        var profile = ConfigurationValidator.NormalizeProfile(new NetworkProfile
        {
            Id = Guid.NewGuid(),
            Host = "irc.example",
            Nickname = "Nex",
            AutoJoinChannels = ["#Room", "#room", "#Other", "bad channel"],
            AlternateNicknames = ["Nex", "Nex_", "Nex_", "Nex__"]
        });

        Assert.NotNull(profile);
        Assert.Equal(["#Room", "#Other"], profile.AutoJoinChannels);
        Assert.Equal(["Nex_", "Nex__"], profile.AlternateNicknames);
        Assert.Equal(profile.Id, profile.ToConnectionOptions().ProfileId);
    }

    [Fact]
    public async Task OverlappingProfilesRemainDistinctByStableIdentity()
    {
        var service = new ConfigurationService(new InMemoryConfigurationStore());
        await service.LoadAsync();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        Assert.True(service.Profiles.AddOrUpdate(new NetworkProfile { Id = firstId, DisplayName = "First", Host = "irc.example", Nickname = "same", AutoJoinChannels = ["#room"] }));
        Assert.True(service.Profiles.AddOrUpdate(new NetworkProfile { Id = secondId, DisplayName = "Second", Host = "irc.example", Nickname = "same", AutoJoinChannels = ["#room"] }));

        Assert.Equal([firstId, secondId], service.Profiles.Profiles.Select(profile => profile.Id));
        Assert.All(service.Profiles.Profiles, profile => Assert.Equal(["#room"], profile.AutoJoinChannels));
    }

    [Fact]
    public async Task LabeledWhoisOperationsUseSeparateLabelsAndViews()
    {
        var endpoint = new IrcEndpoint("labeled.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint, "me", requestedCapabilities: ["labeled-response"]));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me", "labeled-response");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var first = await manager.RequestWhoisAsync(network.Id, "Same");
        var firstLine = await WaitForOutboundAsync(transport, "WHOIS Same", 1);
        var firstLabel = firstLine.Split(' ', 2)[0]["@label=".Length..];
        var second = await manager.RequestWhoisAsync(network.Id, "Same");
        var secondLine = await WaitForOutboundAsync(transport, "WHOIS Same", 2);
        var secondLabel = secondLine.Split(' ', 2)[0]["@label=".Length..];
        Assert.NotEqual(firstLabel, secondLabel);
        Assert.NotSame(first.View, second.View);

        transport.EnqueueInboundLine($"@label={secondLabel} :srv 311 me Same user host * :Second");
        transport.EnqueueInboundLine($"@label={secondLabel} :srv 318 me Same :End");
        transport.EnqueueInboundLine($"@label={firstLabel} :srv 311 me Same user host * :First");
        transport.EnqueueInboundLine($"@label={firstLabel} :srv 318 me Same :End");
        await WaitForAsync(() => ((WhoisView)first.View).IsCompleted && ((WhoisView)second.View).IsCompleted);

        Assert.Equal("First", ((WhoisView)first.View).Result.RealName);
        Assert.Equal("Second", ((WhoisView)second.View).Result.RealName);
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task UnlabeledWhoisSerializesDifferentTargetsAndCoalescesSameTarget()
    {
        var endpoint = new IrcEndpoint("plain.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint, "me"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var first = await manager.RequestWhoisAsync(network.Id, "First");
        var same = await manager.RequestWhoisAsync(network.Id, "First");
        var second = await manager.RequestWhoisAsync(network.Id, "Second");
        Assert.True(same.WasCoalesced);
        Assert.Same(first.View, same.View);
        Assert.DoesNotContain(transport.OutboundLines, line => line == "WHOIS Second");

        transport.EnqueueInboundLine(":srv 318 me First :End");
        await WaitForAsync(() => transport.OutboundLines.Contains("WHOIS Second"));
        transport.EnqueueInboundLine(":srv 318 me Second :End");
        await WaitForAsync(() => ((WhoisView)second.View).IsCompleted);
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task RepeatedUnlabeledListIsCoalescedAndCompletedListCanBeReplaced()
    {
        var endpoint = new IrcEndpoint("list.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint, "me"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var first = await manager.RequestChannelListAsync(network.Id);
        var repeated = await manager.RequestChannelListAsync(network.Id);
        Assert.True(repeated.WasCoalesced);
        Assert.Same(first.View, repeated.View);
        transport.EnqueueInboundLine(":srv 321 me Channel :Users Name");
        transport.EnqueueInboundLine(":srv 322 me #first 1 :First");
        transport.EnqueueInboundLine(":srv 323 me :End");
        await WaitForAsync(() => ((ChannelListView)first.View).IsCompleted);
        Assert.Single(((ChannelListView)first.View).Result.Rows);

        var replacement = await manager.RequestChannelListAsync(network.Id);
        Assert.Same(first.View, replacement.View);
        Assert.Empty(((ChannelListView)replacement.View).Result.Rows);
        transport.EnqueueInboundLine(":srv 321 me Channel :Users Name");
        transport.EnqueueInboundLine(":srv 323 me :End");
        await WaitForAsync(() => ((ChannelListView)replacement.View).IsCompleted);
        Assert.Empty(((ChannelListView)replacement.View).Result.Rows);
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task LabeledListReplacementIgnoresLateRowsFromRetiredOperation()
    {
        var endpoint = new IrcEndpoint("labeled-list.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint, "me", requestedCapabilities: ["labeled-response"]));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me", "labeled-response");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var first = await manager.RequestChannelListAsync(network.Id);
        var firstLine = await WaitForOutboundAsync(transport, " LIST", 1);
        var firstLabel = firstLine.Split(' ', 2)[0]["@label=".Length..];
        var replacement = await manager.RequestChannelListAsync(network.Id);
        var secondLine = await WaitForOutboundAsync(transport, " LIST", 2);
        var secondLabel = secondLine.Split(' ', 2)[0]["@label=".Length..];
        Assert.NotEqual(firstLabel, secondLabel);
        Assert.True(((ChannelListView)first.View).IsLoading);

        transport.EnqueueInboundLine($"@label={firstLabel} :srv 321 me Channel :Users Name");
        transport.EnqueueInboundLine($"@label={firstLabel} :srv 322 me #stale 1 :Stale");
        transport.EnqueueInboundLine($"@label={firstLabel} :srv 323 me :End");
        await Task.Delay(50);
        Assert.Empty(((ChannelListView)replacement.View).Result.Rows);
        Assert.True(((ChannelListView)replacement.View).IsLoading);

        transport.EnqueueInboundLine($"@label={secondLabel} :srv 321 me Channel :Users Name");
        transport.EnqueueInboundLine($"@label={secondLabel} :srv 322 me #fresh 2 :Fresh");
        transport.EnqueueInboundLine($"@label={secondLabel} :srv 323 me :End");
        await WaitForAsync(() => ((ChannelListView)replacement.View).IsCompleted);
        Assert.Equal(["#fresh"], ((ChannelListView)replacement.View).Result.RowsSnapshot.Select(row => row.Channel));
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task OwnOutgoingEchoIsMarkedButDoesNotCreateActivity()
    {
        var endpoint = new IrcEndpoint("notify.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var notifications = new List<IrcNotification>();
        using var subscription = manager.Notifications.Subscribe(notifications.Add);
        var network = manager.Add(Options(endpoint, "me", requestedCapabilities: Array.Empty<string>()));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "me");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        transport.EnqueueInboundLine(":me!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.Count == 1);
        manager.ActivateView(network.Channels[0].Id);
        transport.EnqueueInboundLine(":me!u@h PRIVMSG #room :server echo");
        await WaitForAsync(() => network.Channels.Count == 1 && network.Channels[0].EntriesSnapshot.Any(entry => entry.Text == "server echo"));
        await WaitForAsync(() => notifications.Any(item => item.IsOwnMessage));

        Assert.Equal(WorkspaceActivity.None, network.Channels[0].Activity);
        Assert.Contains(notifications, item => item.IsOwnMessage && item.NetworkId == network.Id);
        await manager.DisconnectAsync(network.Id);
    }

    [Fact]
    public async Task RichCommandsUseAdaptivePrefixAndKeepQueryActionsNetworkScoped()
    {
        var endpoint = new IrcEndpoint("actions.example", 6667, false);
        var transport = new FakeIrcTransport(endpoint);
        var factory = new FakeIrcTransportFactory();
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options(endpoint, "me"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        transport.EnqueueInboundLine(":srv CAP * LS :");
        transport.EnqueueInboundLine(":srv 005 me PREFIX=(qaohv)~&@%+ CHANTYPES=# :features");
        transport.EnqueueInboundLine(":srv 001 me :Welcome");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);

        var dispatcher = new IrcCommandDispatcher(manager);
        var joined = await dispatcher.DispatchAsync(network, network.StatusView, "/join #ops");
        var channel = Assert.IsType<ChannelView>(joined.View);
        transport.EnqueueInboundLine(":me!u@h JOIN #ops");
        transport.EnqueueInboundLine(":srv 353 me = #ops :~me @Other +Guest");
        transport.EnqueueInboundLine(":srv 366 me #ops :End");
        await WaitForAsync(() => channel.CanModerate && channel.MembersSnapshot.Count == 3);

        await dispatcher.DispatchAsync(network, channel, "/op Other");
        await dispatcher.DispatchAsync(network, channel, "/voice Other");
        await dispatcher.DispatchAsync(network, channel, "/devoice Other");
        await dispatcher.DispatchAsync(network, channel, "/kick Other cleanup");
        await dispatcher.DispatchAsync(network, channel, "/notice Other hello");
        await dispatcher.DispatchAsync(network, channel, "/ctcp Other VERSION");
        await WaitForAsync(() => transport.OutboundLines.Contains("PRIVMSG Other :\u0001VERSION\u0001"));

        Assert.Contains("MODE #ops +o Other", transport.OutboundLines);
        Assert.Contains("MODE #ops +v Other", transport.OutboundLines);
        Assert.Contains("MODE #ops -v Other", transport.OutboundLines);
        Assert.Contains("KICK #ops Other :cleanup", transport.OutboundLines);
        Assert.Contains("NOTICE Other :hello", transport.OutboundLines);
        Assert.Contains("PRIVMSG Other :\u0001VERSION\u0001", transport.OutboundLines);
        Assert.Contains(network.Queries, query => query.Nickname == "Other");
        Assert.Contains(network.Queries.Single(query => query.Nickname == "Other").EntriesSnapshot, entry => entry.Kind == TranscriptEntryKind.OutgoingCtcp);
    }

    [Fact]
    public void CtcpActionPresentationIsDistinctFromOrdinaryText()
    {
        var message = IrcMessageParser.Parse(":Other!u@h PRIVMSG #ops :\u0001ACTION waves\u0001").Message!;
        var semantic = new IrcCtcpEvent(message, "#ops", "ACTION", "waves", false);
        var snapshot = new ServerSessionSnapshot(
            ServerSessionState.Disconnected,
            RegistrationState.NotStarted,
            "me",
            "user",
            "real",
            new IrcEndpoint("presentation.example", 6667, false),
            0,
            CapabilitySnapshot.Empty,
            ISupportSnapshot.Empty,
            ServerIdentity.Unknown,
            ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, null, ServerIdentity.Unknown),
            null,
            Array.Empty<IrcChannelSnapshot>(),
            Array.Empty<IrcQuerySnapshot>(),
            new HashSet<string>());

        var entry = IrcEventPresentation.Render(semantic, snapshot);
        Assert.NotNull(entry);
        Assert.Equal(TranscriptEntryKind.Action, entry.Kind);
        Assert.Equal("* Other waves", entry.DisplayLine);
    }

    private static NetworkConnectionOptions Options(IrcEndpoint endpoint, string nickname, IReadOnlyList<string>? requestedCapabilities = null) => new()
    {
        DisplayName = endpoint.Host,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1E test",
        RequestedCapabilities = requestedCapabilities ?? Array.Empty<string>(),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string nickname, string? capabilities = null)
    {
        transport.EnqueueInboundLine($":srv CAP * LS :{capabilities ?? string.Empty}");
        if (capabilities is not null)
        {
            transport.EnqueueInboundLine($":srv CAP * ACK :{capabilities}");
        }

        transport.EnqueueInboundLine($":srv 001 {nickname} :Welcome");
    }

    private static async Task<string> WaitForOutboundAsync(FakeIrcTransport transport, string suffix, int occurrence)
    {
        await WaitForAsync(() => transport.OutboundLines.Count(line => line.EndsWith(suffix, StringComparison.Ordinal)) >= occurrence);
        return transport.OutboundLines.Last(line => line.EndsWith(suffix, StringComparison.Ordinal));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1E application condition was not reached.");
            }

            await Task.Delay(10);
        }
    }
}
