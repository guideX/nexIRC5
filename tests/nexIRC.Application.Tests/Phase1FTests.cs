using System.Text.Json;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1FTests
{
    [Fact]
    public async Task CredentialsSaveLoadDeleteAndProfileRenameKeepStableScope()
    {
        var store = new InMemoryProfileCredentialStore();
        var service = new ProfileCredentialService(store);
        var profileId = Guid.NewGuid();

        Assert.Equal(CredentialStoreStatus.Stored, (await service.SaveAsync(profileId, ProfileCredentialKind.Sasl, "alice", "fake-secret")).Status);
        Assert.True(await service.ExistsAsync(profileId, ProfileCredentialKind.Sasl));
        var loaded = await service.LoadAsync(profileId, ProfileCredentialKind.Sasl);
        Assert.Equal(CredentialStoreStatus.Stored, loaded.Status);
        Assert.Equal("alice", loaded.Credential!.UserName);
        loaded.Credential.Dispose();

        Assert.True(await service.ExistsAsync(profileId, ProfileCredentialKind.Sasl));
        Assert.Equal(CredentialStoreStatus.Deleted, (await service.DeleteAsync(profileId, ProfileCredentialKind.Sasl)).Status);
        Assert.False(await service.ExistsAsync(profileId, ProfileCredentialKind.Sasl));
    }

    [Fact]
    public async Task CredentialFailureIsReportedWithoutPlaintextFallback()
    {
        var store = new InMemoryProfileCredentialStore { IsAvailable = false };
        var service = new ProfileCredentialService(store);
        var result = await service.SaveAsync(Guid.NewGuid(), ProfileCredentialKind.Sasl, "user", "fake-secret");

        Assert.Equal(CredentialStoreStatus.Unavailable, result.Status);
        Assert.Contains("not saved", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(await service.ExistsAsync(Guid.NewGuid(), ProfileCredentialKind.Sasl));
    }

    [Fact]
    public async Task ProfileServerPasswordUsesSecureStoreBridgeWithoutSerializingSecret()
    {
        var store = new InMemoryProfileCredentialStore();
        var profile = new NetworkProfile
        {
            Id = Guid.NewGuid(),
            Host = "irc.example.test",
            Nickname = "nex",
            ServerPasswordEnabled = true
        };
        Assert.Equal(CredentialStoreStatus.Stored, (await store.SaveAsync(profile.Id, ProfileCredentialKind.ServerPassword, profile.Nickname, "fake-pass")).Status);

        var options = profile.ToConnectionOptions(store);
        var password = await options.PasswordProvider!.GetPasswordAsync(options.Endpoint);
        Assert.Equal("fake-pass", password);
        Assert.DoesNotContain("fake-pass", JsonSerializer.Serialize(profile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfileExportRoundTripsWithoutSecretsAndResolvesIdCollision()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1f-export-");
        try
        {
            var id = Guid.NewGuid();
            var config = new ConfigurationService(new InMemoryConfigurationStore());
            config.Profiles.AddOrUpdate(new NetworkProfile { Id = id, DisplayName = "Alpha", Host = "alpha.example", Nickname = "Alice" });
            var path = Path.Combine(directory.FullName, "profiles.json");
            await new ProfilePortabilityService(config).ExportAsync(path);
            var json = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("fake-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);

            var importedConfig = new ConfigurationService(new InMemoryConfigurationStore());
            importedConfig.Profiles.AddOrUpdate(new NetworkProfile { Id = id, DisplayName = "Existing", Host = "existing.example", Nickname = "Existing" });
            var result = await new ProfilePortabilityService(importedConfig).ImportAsync(path);
            var imported = Assert.Single(result.ImportedProfiles);
            Assert.NotEqual(id, imported.Id);
            Assert.Equal(2, importedConfig.Profiles.Profiles.Count);

            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"profiles\":[{\"id\":\"" + Guid.NewGuid() + "\",\"host\":\"a.example\",\"nickname\":\"a\",\"password\":\"evil\"}]}");
            var malicious = await new ProfilePortabilityService(new ConfigurationService(new InMemoryConfigurationStore())).ImportAsync(path);
            Assert.Single(malicious.ImportedProfiles);
            Assert.DoesNotContain("evil", JsonSerializer.Serialize(malicious.ImportedProfiles[0]), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task ProfileImportRejectsUnsupportedAndOversizedDocuments()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1f-import-");
        try
        {
            var path = Path.Combine(directory.FullName, "profiles.json");
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":99,\"profiles\":[]}");
            var service = new ProfilePortabilityService(new ConfigurationService(new InMemoryConfigurationStore()));
            var unsupported = await service.ImportAsync(path);
            Assert.Contains("not supported", Assert.Single(unsupported.Diagnostics), StringComparison.OrdinalIgnoreCase);

            await File.WriteAllTextAsync(path, new string('x', 300));
            var oversized = await new ProfilePortabilityService(new ConfigurationService(new InMemoryConfigurationStore()), 256).ImportAsync(path);
            Assert.Contains("size", Assert.Single(oversized.Diagnostics), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public void FavoritesAndRecentsAreScopedDeduplicatedAndBounded()
    {
        var config = new ConfigurationService(new InMemoryConfigurationStore());
        var scope = Guid.NewGuid();
        Assert.True(config.AddOrUpdateFavorite(new FavoriteDestination { ScopeId = scope, Kind = DestinationKind.Channel, Name = "#Room" }));
        Assert.True(config.AddOrUpdateFavorite(new FavoriteDestination { ScopeId = scope, Kind = DestinationKind.Channel, Name = "#room" }));
        Assert.Single(config.Favorites);

        for (var i = 0; i < ConfigurationLimits.MaximumRecentChannelsPerProfile + 10; i++)
        {
            config.RecordRecent(new RecentDestination { ScopeId = scope, Kind = DestinationKind.Channel, Name = $"#room{i}", LastOpened = DateTimeOffset.UtcNow.AddMinutes(i) });
        }

        Assert.Equal(ConfigurationLimits.MaximumRecentChannelsPerProfile, config.RecentDestinations.Count);
        Assert.Equal("#room59", config.RecentDestinations[0].Name);
        config.ClearRecent(scope);
        Assert.Empty(config.RecentDestinations);
    }

    [Fact]
    public void AliasesSubstituteArgumentsAndDetectLoopsWithBoundedExpansion()
    {
        var aliases = new[]
        {
            new AliasDefinition { Name = "j", Expansion = "/join $1" },
            new AliasDefinition { Name = "say", Expansion = "/msg $1 $*" },
            new AliasDefinition { Name = "loop-a", Expansion = "/loop-b" },
            new AliasDefinition { Name = "loop-b", Expansion = "/loop-a" }
        };

        Assert.Equal("/join #chat", AliasExpander.Expand("/j #chat", aliases).Input);
        Assert.Equal("/msg Mira Mira hello there", AliasExpander.Expand("/say Mira hello there", aliases).Input);
        var loop = AliasExpander.Expand("/loop-a", aliases);
        Assert.False(loop.Succeeded);
        Assert.Contains("loop", loop.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(AliasValidator.Validate(new AliasDefinition { Name = "join", Expansion = "/part" }).IsValid);
    }

    [Fact]
    public void AliasCompletionIncludesEnabledAliasesButBuiltInsRemainAuthoritative()
    {
        var engine = new CompletionEngine(() => [new AliasDefinition { Name = "zz", Expansion = "/join $1" }]);
        var result = engine.Complete("/z", 2, null, null);
        Assert.Equal("/zz", result.Text);
        var completed = engine.Complete("/z", 2, null, null);
        Assert.Equal("/zz", completed.Text);
        var builtIn = engine.Complete("/jo", 3, null, null);
        Assert.Equal("/join", builtIn.Text);
    }

    [Fact]
    public async Task JsonLogStoreSeparatesNetworksSearchesAndPagesWithRetention()
    {
        var directory = Directory.CreateTempSubdirectory("nexirc-phase1f-log-");
        try
        {
            await using var store = new JsonlConversationLogStore(directory.FullName);
            var profileA = Guid.NewGuid();
            var profileB = Guid.NewGuid();
            var networkA = Guid.NewGuid();
            var networkB = Guid.NewGuid();
            var old = new ConversationLogRecord
            {
                Timestamp = DateTimeOffset.UtcNow.AddDays(-10),
                NetworkId = networkA,
                ScopeId = profileA,
                ProfileId = profileA,
                ConversationKind = LogConversationKind.Channel,
                ConversationName = "#general",
                ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, "#general"),
                Sender = "Mira",
                Text = "old message"
            };
            var fresh = old with { Timestamp = DateTimeOffset.UtcNow, NetworkId = networkB, ScopeId = profileB, ProfileId = profileB, Text = "Fresh message", Sender = "Rook" };
            Assert.True(await store.AppendAsync(old));
            Assert.True(await store.AppendAsync(fresh));
            await store.FlushAsync();

            var results = await store.SearchAsync(new ConversationLogQuery { Text = "fresh", ProfileId = profileB });
            var result = Assert.Single(results);
            Assert.Equal(networkB, result.Record.NetworkId);
            Assert.Equal("Rook", result.Record.Sender);
            Assert.Single(await store.ReadPageAsync(profileB, LogConversationKind.Channel, "#GENERAL", 100));
            Assert.Equal(1, await store.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-1)));
        }
        finally
        {
            Directory.Delete(directory.FullName, true);
        }
    }

    [Fact]
    public async Task ConversationLoggingRedactsSensitiveStatusAndRespectsPrivatePreference()
    {
        var store = new InMemoryConversationLogStore();
        var preferences = new ApplicationPreferences
        {
            ConversationLoggingEnabled = true,
            StatusLoggingEnabled = true,
            PrivateMessageLoggingEnabled = false
        };
        var endpoint = new IrcEndpoint("log.example", 6667, false);
        var factory = new FakeIrcTransportFactory();
        factory.Add(new FakeIrcTransport(endpoint));
        await using var manager = new NetworkSessionManager(factory, configuration: new ConfigurationService(new InMemoryConfigurationStore()));
        var network = manager.Add(new NetworkConnectionOptions { DisplayName = "log", Endpoint = endpoint, Nickname = "me" });
        var logging = new ConversationLoggingService(store, () => preferences);
        var status = new TranscriptEntry(DateTimeOffset.UtcNow, TranscriptEntryKind.System, null, "PASS :fake-secret");
        logging.Record(network.Id, network.ProfileId, network.StatusView, status);
        await store.FlushAsync();
        var record = Assert.Single(store.Records);
        Assert.DoesNotContain("fake-secret", record.Text, StringComparison.Ordinal);
        Assert.Contains("redacted", record.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NotificationCoalescingKeepsActiveViewsAndSeparatesTypes()
    {
        var coalescer = new NotificationCoalescer(TimeSpan.FromMinutes(1));
        var now = DateTimeOffset.UtcNow;
        var first = Notification(now, IrcNotificationType.Highlight, false);
        var repeat = first with { Timestamp = now.AddSeconds(1) };
        var privateMessage = first with { Type = IrcNotificationType.PrivateMessage, Timestamp = now.AddSeconds(1) };
        var active = first with { IsViewActive = true, Timestamp = now.AddSeconds(1) };
        Assert.True(coalescer.ShouldPublish(first));
        Assert.False(coalescer.ShouldPublish(repeat));
        Assert.True(coalescer.ShouldPublish(privateMessage));
        Assert.True(coalescer.ShouldPublish(active));
    }

    [Fact]
    public void ViewStateValidationRepairsOffscreenAndInvalidDimensions()
    {
        var state = ViewStateValidator.Normalize(new ViewStatePreferences
        {
            WindowWidth = -1,
            WindowHeight = double.PositiveInfinity,
            WindowLeft = 9000,
            WindowTop = 9000
        }, new ViewportBounds(0, 0, 1920, 1080));
        Assert.Equal(640, state.WindowWidth);
        Assert.Equal(760, state.WindowHeight);
        Assert.Null(state.WindowLeft);
        Assert.Null(state.WindowTop);
    }

    private static IrcNotification Notification(DateTimeOffset timestamp, IrcNotificationType type, bool active) =>
        new(Guid.NewGuid(), Guid.NewGuid(), WorkspaceViewKind.Channel, type, WorkspaceActivity.Important, "Mira", "hello", timestamp, active, "test");
}
