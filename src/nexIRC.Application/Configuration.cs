using System.Text.Json;
using System.Text.Json.Serialization;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

/// <summary>
/// Bounded limits shared by the configuration validator and its file store.
/// Keeping these limits in the application layer prevents a malformed settings
/// file from turning into an unbounded collection of UI or session objects.
/// </summary>
public static class ConfigurationLimits
{
    public const int MaximumFileBytes = 1_048_576;
    public const int MaximumExportFileBytes = 1_048_576;
    public const int MaximumProfiles = 64;
    public const int MaximumStringLength = 256;
    public const int MaximumRealNameLength = 512;
    public const int MaximumChannelsPerProfile = 128;
    public const int MaximumAlternateNicknames = 8;
    public const int MaximumHighlightWords = 64;
    public const int MaximumChannelLength = 200;
    public const int MaximumAliases = 128;
    public const int MaximumAliasNameLength = 32;
    public const int MaximumAliasExpansionLength = 1024;
    public const int MaximumAliasRecursionDepth = 8;
    public const int MaximumFavoritesPerProfile = 128;
    public const int MaximumFavoriteGroups = 32;
    public const int MaximumFavoriteGroupNameLength = 64;
    public const int MaximumRecentChannelsPerProfile = 50;
    public const int MaximumRecentQueriesPerProfile = 50;
    public const int MaximumSearchResults = 500;
    public const int MaximumSearchQueryLength = 256;
    public const int MaximumHistoryPageSize = 100;
    public const int MaximumHistoryContextEntries = 100;
    public const int MaximumHistoryExportRecords = 10_000;
    public const long MaximumHistoryFileBytes = 256L * 1024 * 1024;
    public const long DefaultHistorySegmentBytes = 64L * 1024 * 1024;
    public const int MaximumHistorySourceFiles = 100_000;
    public const int MaximumHistoryIndexEntries = 1_000_000;
    public const int MinimumHistoryIndexFileBytes = 1 * 1024 * 1024;
    public const int HistorySearchIndexBlockRecords = 4096;
    public const int MaximumHistorySearchIndexBlocks = 4096;
    public const int HistorySearchIndexBloomBytes = 128;
    public const int MaximumConversationNavigationHistory = 64;
    public const int MaximumDraftLength = 4096;
    public const int MaximumLogRecordBytes = 32_768;
    public const int MaximumNotificationCoalescingEntries = 256;
    public const int MaximumIgnoreRules = 256;
    public const int MaximumUnreadCount = 10_000;
}

public static class ConfigurationSchema
{
    public const int CurrentVersion = 1;
}

public sealed record ApplicationPreferences
{
    public bool NotificationsEnabled { get; init; }

    public bool HighlightNotifications { get; init; } = true;

    public bool PrivateMessageNotifications { get; init; } = true;

    public bool ConnectionNotifications { get; init; }

    public bool HighlightNickname { get; init; } = true;

    public bool HighlightCustomWords { get; init; } = true;

    public List<string> CustomHighlightWords { get; init; } = [];

    public bool IsNetworkTreeVisible { get; init; } = true;

    public bool IsMemberListVisible { get; init; } = true;

    public bool IsToolbarVisible { get; init; } = true;

    public bool IsStatusBarVisible { get; init; } = true;

    public Guid? LastSelectedNetworkProfileId { get; init; }

    public bool ConversationLoggingEnabled { get; init; }

    public bool PrivateMessageLoggingEnabled { get; init; }

    public bool StatusLoggingEnabled { get; init; }

    public int LogRetentionDays { get; init; } = 30;

    public ViewStatePreferences ViewState { get; init; } = new();
}

public sealed record ViewStatePreferences
{
    public double WindowWidth { get; init; } = 1180;

    public double WindowHeight { get; init; } = 760;

    public double? WindowLeft { get; init; }

    public double? WindowTop { get; init; }

    public bool IsMaximized { get; init; }

    public double NavigationPaneWidth { get; init; } = 260;

    public double MemberPaneWidth { get; init; } = 220;

    public string? LastLogSearchQuery { get; init; }

    public string? LastLogConversation { get; init; }
}

/// <summary>
/// A non-secret saved network definition. Passwords and SASL credentials are
/// intentionally absent; a host-owned secret provider is required at connect
/// time when authentication is enabled.
/// </summary>
public sealed record NetworkProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string DisplayName { get; init; } = "New network";

    public string Host { get; init; } = "irc.example.invalid";

    public int Port { get; init; } = 6697;

    public bool UseTls { get; init; } = true;

    public string Nickname { get; init; } = "nexIRC5";

    public List<string> AlternateNicknames { get; init; } = [];

    public string Username { get; init; } = "nexirc";

    public string RealName { get; init; } = "nexIRC 5";

    public List<string> AutoJoinChannels { get; init; } = [];

    public bool AutoConnect { get; init; }

    public bool ReconnectEnabled { get; init; } = true;

    public int ReconnectMaximumAttempts { get; init; } = 3;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public SaslAuthenticationPolicy SaslPolicy { get; init; } = SaslAuthenticationPolicy.Disabled;

    public string? SaslUsername { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ServerPasswordEnabled { get; init; }

    public NetworkConnectionOptions ToConnectionOptions(IProfileCredentialStore? credentialStore = null)
    {
        var profile = ConfigurationValidator.NormalizeProfile(this)
            ?? throw new ArgumentException("The network profile is missing a valid host or nickname.", nameof(NetworkProfile));
        var fallbacks = profile.AlternateNicknames?.Where(static nickname => !string.IsNullOrWhiteSpace(nickname)).ToArray()
            ?? Array.Empty<string>();
        return new NetworkConnectionOptions
        {
            ProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            Endpoint = new IrcEndpoint(profile.Host, profile.Port, profile.UseTls),
            Nickname = profile.Nickname,
            AlternateNickname = fallbacks.FirstOrDefault(),
            NicknameFallbacks = fallbacks.Skip(1).ToArray(),
            Username = profile.Username,
            RealName = profile.RealName,
            DesiredChannels = (profile.AutoJoinChannels ?? []).ToHashSet(IrcCaseMappingComparer.For(IrcCaseMapping.Rfc1459)),
            AutoConnect = profile.AutoConnect,
            Reconnect = new ReconnectPolicy(profile.ReconnectEnabled, Math.Clamp(profile.ReconnectMaximumAttempts, 1, 10)),
            SaslPolicy = profile.SaslPolicy,
            SaslCredentialProvider = profile.SaslPolicy == SaslAuthenticationPolicy.Disabled || credentialStore is null
                ? null
                : new ProfileSaslCredentialProvider(profile.Id, credentialStore),
            PasswordProvider = profile.ServerPasswordEnabled && credentialStore is not null
                ? new ProfileServerPasswordProvider(profile.Id, credentialStore)
                : null
        };
    }
}

public sealed record NexIrcConfiguration
{
    public int SchemaVersion { get; init; } = ConfigurationSchema.CurrentVersion;

    public ApplicationPreferences Preferences { get; init; } = new();

    public List<NetworkProfile> Profiles { get; init; } = [];

    public List<FavoriteDestination> Favorites { get; init; } = [];

    public List<FavoriteGroup> FavoriteGroups { get; init; } =
    [
        new FavoriteGroup
        {
            Id = NavigationDefaults.DefaultFavoriteGroupId,
            Name = "General",
            SortOrder = 0
        }
    ];

    public List<RecentDestination> RecentDestinations { get; init; } = [];

    public List<AliasDefinition> Aliases { get; init; } = [];

    public List<IgnoreRule> IgnoreRules { get; init; } = [];
}

public sealed record ConfigurationLoadResult(
    NexIrcConfiguration Configuration,
    bool UsedDefaults,
    string? Diagnostic = null,
    string? EvidencePath = null);

public interface IConfigurationStore
{
    ValueTask<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(NexIrcConfiguration configuration, CancellationToken cancellationToken = default);
}

public interface INetworkProfileRepository
{
    IReadOnlyList<NetworkProfile> Profiles { get; }

    bool AddOrUpdate(NetworkProfile profile);

    bool Remove(Guid profileId);

    bool TryGet(Guid profileId, out NetworkProfile? profile);
}

/// <summary>
/// Owns the validated in-memory configuration and delegates persistence to a
/// replaceable store. WPF observes this service but does not know its file
/// format or recovery policy.
/// </summary>
public sealed class ConfigurationService
{
    private readonly object _gate = new();
    private readonly IConfigurationStore _store;
    private NexIrcConfiguration _configuration = ConfigurationValidator.Defaults();

    public ConfigurationService(IConfigurationStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Profiles = new NetworkProfileRepository(this);
    }

    public IConfigurationStore Store => _store;

    public NexIrcConfiguration Current
    {
        get
        {
            lock (_gate)
            {
                return ConfigurationValidator.Normalize(_configuration);
            }
        }
    }

    public ApplicationPreferences Preferences => Current.Preferences;

    public INetworkProfileRepository Profiles { get; }

    public ConfigurationLoadResult LastLoadResult { get; private set; } = new(ConfigurationValidator.Defaults(), true, "Configuration has not been loaded yet.");

    public async ValueTask<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        var result = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _configuration = ConfigurationValidator.Normalize(result.Configuration);
            LastLoadResult = result with { Configuration = _configuration };
        }

        return LastLoadResult;
    }

    public ValueTask SaveAsync(CancellationToken cancellationToken = default)
    {
        NexIrcConfiguration snapshot;
        lock (_gate)
        {
            _configuration = ConfigurationValidator.Normalize(_configuration);
            snapshot = _configuration;
        }

        return _store.SaveAsync(snapshot, cancellationToken);
    }

    public void SetPreferences(ApplicationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            _configuration = ConfigurationValidator.Normalize(_configuration with { Preferences = preferences });
        }
    }

    public IReadOnlyList<FavoriteDestination> Favorites => Current.Favorites;

    public IReadOnlyList<FavoriteGroup> FavoriteGroups => Current.FavoriteGroups;

    public IReadOnlyList<RecentDestination> RecentDestinations => Current.RecentDestinations;

    public IReadOnlyList<AliasDefinition> Aliases => Current.Aliases;

    public IReadOnlyList<IgnoreRule> IgnoreRules => Current.IgnoreRules;

    public bool AddIgnore(IgnoreRule rule)
    {
        var normalized = IgnoreRuleValidator.Normalize(rule);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            var rules = _configuration.IgnoreRules.ToList();
            if (rules.Any(existing => SameIgnore(existing, normalized)))
            {
                return true;
            }

            if (rules.Count >= ConfigurationLimits.MaximumIgnoreRules)
            {
                return false;
            }

            rules.Add(normalized);
            _configuration = _configuration with { IgnoreRules = rules };
            return true;
        }
    }

    public bool RemoveIgnore(IgnoreRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        lock (_gate)
        {
            var rules = _configuration.IgnoreRules.ToList();
            var index = rules.FindIndex(existing => SameIgnore(existing, rule));
            if (index < 0)
            {
                return false;
            }

            rules.RemoveAt(index);
            _configuration = _configuration with { IgnoreRules = rules };
            return true;
        }
    }

    public bool IsIgnored(IgnoreIdentity identity, IrcCaseMapping mapping = IrcCaseMapping.Rfc1459) =>
        IgnoreRules.Any(rule => IgnoreMatcher.Matches(rule, identity, mapping));

    private static bool SameIgnore(IgnoreRule left, IgnoreRule right) =>
        left.NetworkProfileId == right.NetworkProfileId
        && string.Equals(left.NetworkName, right.NetworkName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Nickname, right.Nickname, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Hostmask, right.Hostmask, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Account, right.Account, StringComparison.OrdinalIgnoreCase);

    public bool AddOrUpdateFavorite(FavoriteDestination favorite)
    {
        var normalized = NavigationValidator.NormalizeFavorite(favorite);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_configuration.FavoriteGroups.Any(group => group.Id == normalized.GroupId))
            {
                normalized = normalized with { GroupId = NavigationDefaults.DefaultFavoriteGroupId };
            }

            var favorites = _configuration.Favorites.ToList();
            var existing = favorites.FindIndex(item => item.Id == normalized.Id);
            if (existing >= 0)
            {
                favorites[existing] = normalized;
            }
            else
            {
                if (favorites.Count(item => item.ScopeId == normalized.ScopeId) >= ConfigurationLimits.MaximumFavoritesPerProfile)
                {
                    return false;
                }

                if (favorites.Any(item => NavigationValidator.SameDestination(item, normalized)))
                {
                    return true;
                }

                var nextOrder = favorites
                    .Where(item => item.ScopeId == normalized.ScopeId && item.GroupId == normalized.GroupId)
                    .Select(item => item.SortOrder)
                    .DefaultIfEmpty(-1)
                    .Max() + 1;
                favorites.Add(normalized with { SortOrder = nextOrder });
            }

            _configuration = _configuration with { Favorites = favorites };
            return true;
        }
    }

    public bool RemoveFavorite(Guid favoriteId)
    {
        lock (_gate)
        {
            var favorites = _configuration.Favorites.Where(item => item.Id != favoriteId).ToList();
            if (favorites.Count == _configuration.Favorites.Count)
            {
                return false;
            }

            _configuration = _configuration with { Favorites = favorites };
            return true;
        }
    }

    public bool MoveFavorite(Guid favoriteId, Guid groupId)
    {
        lock (_gate)
        {
            if (!_configuration.FavoriteGroups.Any(group => group.Id == groupId))
            {
                return false;
            }

            var favorites = _configuration.Favorites.ToList();
            var index = favorites.FindIndex(item => item.Id == favoriteId);
            if (index < 0)
            {
                return false;
            }

            var favorite = favorites[index];
            var nextOrder = favorites
                .Where(item => item.ScopeId == favorite.ScopeId && item.GroupId == groupId)
                .Select(item => item.SortOrder)
                .DefaultIfEmpty(-1)
                .Max() + 1;
            favorites[index] = favorite with { GroupId = groupId, SortOrder = nextOrder };
            _configuration = _configuration with { Favorites = favorites };
            return true;
        }
    }

    public bool ReorderFavorite(Guid favoriteId, int delta)
    {
        if (delta == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var favorites = _configuration.Favorites.ToList();
            var index = favorites.FindIndex(item => item.Id == favoriteId);
            if (index < 0)
            {
                return false;
            }

            var current = favorites[index];
            var sameGroup = favorites
                .Where(item => item.ScopeId == current.ScopeId && item.GroupId == current.GroupId)
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToList();
            var currentIndex = sameGroup.FindIndex(item => item.Id == favoriteId);
            var targetIndex = Math.Clamp(currentIndex + Math.Sign(delta), 0, sameGroup.Count - 1);
            if (currentIndex == targetIndex)
            {
                return false;
            }

            (sameGroup[currentIndex], sameGroup[targetIndex]) = (sameGroup[targetIndex], sameGroup[currentIndex]);
            for (var order = 0; order < sameGroup.Count; order++)
            {
                var itemIndex = favorites.FindIndex(item => item.Id == sameGroup[order].Id);
                favorites[itemIndex] = sameGroup[order] with { SortOrder = order };
            }

            _configuration = _configuration with { Favorites = favorites };
            return true;
        }
    }

    public bool AddFavoriteGroup(string name)
    {
        var normalized = NavigationValidator.NormalizeFavoriteGroups([new FavoriteGroup { Name = name, SortOrder = int.MaxValue }]).LastOrDefault();
        if (normalized is null || string.Equals(normalized.Name, "General", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        lock (_gate)
        {
            if (_configuration.FavoriteGroups.Count >= ConfigurationLimits.MaximumFavoriteGroups
                || _configuration.FavoriteGroups.Any(group => string.Equals(group.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var groups = _configuration.FavoriteGroups.ToList();
            groups.Add(normalized with { Id = Guid.NewGuid(), SortOrder = groups.Count });
            _configuration = _configuration with { FavoriteGroups = NavigationValidator.NormalizeFavoriteGroups(groups) };
            return true;
        }
    }

    public bool RemoveFavoriteGroup(Guid groupId)
    {
        if (groupId == NavigationDefaults.DefaultFavoriteGroupId)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_configuration.FavoriteGroups.Any(group => group.Id == groupId))
            {
                return false;
            }

            var groups = _configuration.FavoriteGroups.Where(group => group.Id != groupId).ToList();
            var favorites = _configuration.Favorites
                .Select(favorite => favorite.GroupId == groupId
                    ? favorite with { GroupId = NavigationDefaults.DefaultFavoriteGroupId }
                    : favorite)
                .ToList();
            _configuration = _configuration with
            {
                FavoriteGroups = NavigationValidator.NormalizeFavoriteGroups(groups),
                Favorites = favorites
            };
            return true;
        }
    }

    public void RecordRecent(RecentDestination destination)
    {
        var normalized = NavigationValidator.NormalizeRecent(destination);
        if (normalized is null)
        {
            return;
        }

        lock (_gate)
        {
            var recents = _configuration.RecentDestinations
                .Where(item => !NavigationValidator.SameDestination(item, normalized))
                .Append(normalized)
                .OrderByDescending(item => item.LastOpened)
                .ToList();
            recents = NavigationValidator.BoundRecents(recents);
            _configuration = _configuration with { RecentDestinations = recents };
        }
    }

    public bool RenameDestination(
        Guid scopeId,
        DestinationKind kind,
        string oldName,
        string newName,
        IrcCaseMapping mapping = IrcCaseMapping.Rfc1459)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        lock (_gate)
        {
            var hasTarget = _configuration.Favorites.Any(item => item.ScopeId == scopeId
                    && item.Kind == kind
                    && IrcCaseMappingComparer.Equals(item.Name, newName, mapping))
                || _configuration.RecentDestinations.Any(item => item.ScopeId == scopeId
                    && item.Kind == kind
                    && IrcCaseMappingComparer.Equals(item.Name, newName, mapping));
            if (hasTarget)
            {
                return false;
            }

            var favorites = _configuration.Favorites
                .Select(item => item.ScopeId == scopeId
                    && item.Kind == kind
                    && IrcCaseMappingComparer.Equals(item.Name, oldName, mapping)
                        ? item with { Name = newName }
                        : item)
                .ToList();
            var recents = _configuration.RecentDestinations
                .Select(item => item.ScopeId == scopeId
                    && item.Kind == kind
                    && IrcCaseMappingComparer.Equals(item.Name, oldName, mapping)
                        ? item with { Name = newName }
                        : item)
                .ToList();
            if (favorites.SequenceEqual(_configuration.Favorites) && recents.SequenceEqual(_configuration.RecentDestinations))
            {
                return false;
            }

            _configuration = _configuration with { Favorites = favorites, RecentDestinations = recents };
            return true;
        }
    }

    public void ClearRecent(Guid? scopeId = null)
        => ClearRecent(scopeId, null);

    public void ClearRecent(Guid? scopeId, DestinationKind? kind)
    {
        lock (_gate)
        {
            _configuration = _configuration with
            {
                RecentDestinations = _configuration.RecentDestinations.Where(item =>
                    !(scopeId is null || item.ScopeId == scopeId)
                    || kind is not null && item.Kind != kind).ToList()
            };
        }
    }

    public bool RemoveRecent(RecentDestination destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            var recents = _configuration.RecentDestinations.Where(item => !NavigationValidator.SameDestination(item, destination)).ToList();
            if (recents.Count == _configuration.RecentDestinations.Count)
            {
                return false;
            }

            _configuration = _configuration with { RecentDestinations = recents };
            return true;
        }
    }

    public bool AddOrUpdateAlias(AliasDefinition alias)
    {
        var normalized = AliasValidator.Normalize(alias);
        if (normalized is null || IrcCommandDispatcher.IsBuiltInCommand(normalized.Name))
        {
            return false;
        }

        lock (_gate)
        {
            var aliases = _configuration.Aliases.ToList();
            var existing = aliases.FindIndex(item => string.Equals(item.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
            {
                aliases[existing] = normalized with { Id = aliases[existing].Id };
            }
            else
            {
                if (aliases.Count >= ConfigurationLimits.MaximumAliases)
                {
                    return false;
                }

                aliases.Add(normalized);
            }

            _configuration = _configuration with { Aliases = aliases };
            return true;
        }
    }

    public bool RemoveAlias(Guid aliasId)
    {
        lock (_gate)
        {
            var aliases = _configuration.Aliases.Where(item => item.Id != aliasId).ToList();
            if (aliases.Count == _configuration.Aliases.Count)
            {
                return false;
            }

            _configuration = _configuration with { Aliases = aliases };
            return true;
        }
    }

    private bool AddOrUpdateProfileCore(NetworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var normalized = ConfigurationValidator.NormalizeProfile(profile);
        if (normalized is null)
        {
            throw new ArgumentException("The network profile is missing a valid host or nickname.", nameof(profile));
        }

        lock (_gate)
        {
            var profiles = _configuration.Profiles.ToList();
            var index = profiles.FindIndex(item => item.Id == normalized.Id);
            if (index >= 0)
            {
                profiles[index] = normalized;
            }
            else
            {
                if (profiles.Count >= ConfigurationLimits.MaximumProfiles)
                {
                    return false;
                }

                profiles.Add(normalized);
            }

            _configuration = _configuration with { Profiles = profiles };
            return true;
        }
    }

    private bool RemoveProfileCore(Guid profileId)
    {
        lock (_gate)
        {
            var profiles = _configuration.Profiles.Where(profile => profile.Id != profileId).ToList();
            if (profiles.Count == _configuration.Profiles.Count)
            {
                return false;
            }

            var selected = _configuration.Preferences.LastSelectedNetworkProfileId == profileId
                ? _configuration.Preferences with { LastSelectedNetworkProfileId = null }
                : _configuration.Preferences;
            _configuration = _configuration with { Profiles = profiles, Preferences = selected };
            return true;
        }
    }

    private NetworkProfile[] ProfilesSnapshot()
    {
        lock (_gate)
        {
            return _configuration.Profiles.ToArray();
        }
    }

    private sealed class NetworkProfileRepository(ConfigurationService owner) : INetworkProfileRepository
    {
        private readonly ConfigurationService _owner = owner;

        public IReadOnlyList<NetworkProfile> Profiles => _owner.ProfilesSnapshot();

        public bool AddOrUpdate(NetworkProfile profile) => _owner.AddOrUpdateProfileCore(profile);

        public bool Remove(Guid profileId) => _owner.RemoveProfileCore(profileId);

        public bool TryGet(Guid profileId, out NetworkProfile? profile)
        {
            profile = Profiles.FirstOrDefault(item => item.Id == profileId);
            return profile is not null;
        }
    }
}

public static class ConfigurationPaths
{
    public static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "nexIRC",
        "configuration.json");
}

public sealed class InMemoryConfigurationStore : IConfigurationStore
{
    private NexIrcConfiguration _configuration;

    public InMemoryConfigurationStore(NexIrcConfiguration? initial = null)
    {
        _configuration = ConfigurationValidator.Normalize(initial ?? ConfigurationValidator.Defaults());
    }

    public int SaveCount { get; private set; }

    public ValueTask<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ConfigurationLoadResult(_configuration, false));
    }

    public ValueTask SaveAsync(NexIrcConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _configuration = ConfigurationValidator.Normalize(configuration);
        SaveCount++;
        return ValueTask.CompletedTask;
    }
}

public sealed class JsonConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly int _maximumFileBytes;

    public JsonConfigurationStore(string path, int maximumFileBytes = ConfigurationLimits.MaximumFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maximumFileBytes < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _path = System.IO.Path.GetFullPath(path);
        _maximumFileBytes = maximumFileBytes;
    }

    public string Path => _path;

    public async ValueTask<ConfigurationLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return new ConfigurationLoadResult(ConfigurationValidator.Defaults(), false, "No saved configuration was found; using defaults.");
        }

        try
        {
            var info = new FileInfo(_path);
            if (info.Length > _maximumFileBytes)
            {
                return Recover("The configuration file exceeds the supported size limit.");
            }

            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            await using var bounded = await ReadBoundedAsync(stream, cancellationToken).ConfigureAwait(false);
            var configuration = await JsonSerializer.DeserializeAsync<NexIrcConfiguration>(bounded, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (configuration is null)
            {
                return Recover("The configuration file was empty.");
            }

            if (configuration.SchemaVersion != ConfigurationSchema.CurrentVersion)
            {
                return Recover($"Configuration schema version {configuration.SchemaVersion} is not supported; using defaults.");
            }

            return new ConfigurationLoadResult(ConfigurationValidator.Normalize(configuration), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Recover($"The configuration file could not be loaded safely: {exception.Message}");
        }
    }

    public async ValueTask SaveAsync(NexIrcConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalized = ConfigurationValidator.Normalize(configuration);
        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("The configuration path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // A failed cleanup cannot make a successfully saved settings
                // file invalid and must not hide the original save exception.
            }
        }
    }

    private ConfigurationLoadResult Recover(string diagnostic)
    {
        string? evidencePath = null;
        try
        {
            var candidate = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}.json";
            File.Move(_path, candidate, overwrite: false);
            evidencePath = candidate;
        }
        catch
        {
            // Recovery remains safe even when the directory is read-only or a
            // concurrent process has already moved the evidence.
        }

        return new ConfigurationLoadResult(ConfigurationValidator.Defaults(), true, diagnostic, evidencePath);
    }

    private async ValueTask<MemoryStream> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0L;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                destination.Position = 0;
                return destination;
            }

            total += read;
            if (total > _maximumFileBytes)
            {
                destination.Dispose();
                throw new ConfigurationTooLargeException();
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ConfigurationTooLargeException() : IOException("The configuration file exceeds the supported size limit.");
}

public static class ConfigurationValidator
{
    public static NexIrcConfiguration Defaults() => new();

    public static NexIrcConfiguration Normalize(NexIrcConfiguration? configuration)
    {
        if (configuration is null || configuration.SchemaVersion != ConfigurationSchema.CurrentVersion)
        {
            return Defaults();
        }

        var profiles = new List<NetworkProfile>();
        var profileIds = new HashSet<Guid>();
        foreach (var profile in (configuration.Profiles ?? []).Take(ConfigurationLimits.MaximumProfiles))
        {
            var normalized = NormalizeProfile(profile);
            if (normalized is not null && profileIds.Add(normalized.Id))
            {
                profiles.Add(normalized);
            }
        }

        var preferences = configuration.Preferences is { } loadedPreferences ? loadedPreferences : new ApplicationPreferences();
        var words = NormalizeWords(preferences.CustomHighlightWords);
        Guid? selected = preferences.LastSelectedNetworkProfileId is Guid selectedId && profileIds.Contains(selectedId)
            ? selectedId
            : null;
        var viewState = ViewStateValidator.Normalize(preferences.ViewState);
        var favoriteGroups = NavigationValidator.NormalizeFavoriteGroups(configuration.FavoriteGroups);
        var favorites = NavigationValidator.NormalizeFavorites(configuration.Favorites, favoriteGroups.Select(group => group.Id).ToHashSet());
        var recents = NavigationValidator.BoundRecents((configuration.RecentDestinations ?? [])
            .Select(NavigationValidator.NormalizeRecent)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .OrderByDescending(static item => item.LastOpened));
        var aliases = (configuration.Aliases ?? [])
            .Select(AliasValidator.Normalize)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .Where(alias => !IrcCommandDispatcher.IsBuiltInCommand(alias.Name))
            .GroupBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(ConfigurationLimits.MaximumAliases)
            .ToList();
        var ignores = (configuration.IgnoreRules ?? [])
            .Select(IgnoreRuleValidator.Normalize)
            .Where(static value => value is not null)
            .Select(static value => value!)
            .GroupBy(rule => $"{rule.NetworkProfileId}|{rule.NetworkName}|{rule.Nickname}|{rule.Hostmask}|{rule.Account}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(ConfigurationLimits.MaximumIgnoreRules)
            .ToList();
        return new NexIrcConfiguration
        {
            SchemaVersion = ConfigurationSchema.CurrentVersion,
            Preferences = preferences with
            {
                CustomHighlightWords = words,
                LastSelectedNetworkProfileId = selected,
                LogRetentionDays = Math.Clamp(preferences.LogRetentionDays, 1, 3650),
                ViewState = viewState
            },
            Profiles = profiles,
            Favorites = favorites,
            FavoriteGroups = favoriteGroups,
            RecentDestinations = recents,
            Aliases = aliases,
            IgnoreRules = ignores
        };
    }

    public static NetworkProfile? NormalizeProfile(NetworkProfile? profile)
    {
        if (profile is null)
        {
            return null;
        }

        var host = NormalizeValue(profile.Host, string.Empty, ConfigurationLimits.MaximumStringLength);
        var nickname = NormalizeValue(profile.Nickname, string.Empty, ConfigurationLimits.MaximumStringLength);
        if (host.Length == 0 || nickname.Length == 0 || host.Any(char.IsWhiteSpace) || host.Any(IsLineBreak))
        {
            return null;
        }

        var id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id;
        var alternateNicknames = NormalizeValues(profile.AlternateNicknames, ConfigurationLimits.MaximumAlternateNicknames, ConfigurationLimits.MaximumStringLength, nickname);
        var channels = NormalizeChannels(profile.AutoJoinChannels);
        return profile with
        {
            Id = id,
            DisplayName = NormalizeValue(profile.DisplayName, host, ConfigurationLimits.MaximumStringLength),
            Host = host,
            Port = profile.Port is >= 1 and <= 65535 ? profile.Port : 6697,
            Nickname = nickname,
            AlternateNicknames = alternateNicknames,
            Username = NormalizeValue(profile.Username, "nexirc", ConfigurationLimits.MaximumStringLength),
            RealName = NormalizeValue(profile.RealName, "nexIRC 5", ConfigurationLimits.MaximumRealNameLength),
            AutoJoinChannels = channels,
            ReconnectMaximumAttempts = Math.Clamp(profile.ReconnectMaximumAttempts, 1, 10),
            SaslPolicy = Enum.IsDefined(profile.SaslPolicy) ? profile.SaslPolicy : SaslAuthenticationPolicy.Disabled,
            SaslUsername = NormalizeOptionalValue(profile.SaslUsername, ConfigurationLimits.MaximumStringLength)
        };
    }

    private static List<string> NormalizeChannels(IEnumerable<string>? channels)
    {
        var result = new List<string>();
        var comparer = IrcCaseMappingComparer.For(IrcCaseMapping.Rfc1459);
        foreach (var value in (channels ?? []).Take(ConfigurationLimits.MaximumChannelsPerProfile))
        {
            var channel = value?.Trim() ?? string.Empty;
            if (channel.Length == 0 || channel.Length > ConfigurationLimits.MaximumChannelLength || channel.Any(char.IsWhiteSpace) || channel.Any(character => character is ',' or '\r' or '\n') || result.Contains(channel, comparer))
            {
                continue;
            }

            result.Add(channel);
        }

        return result;
    }

    private static List<string> NormalizeWords(IEnumerable<string>? words) => NormalizeValues(words, ConfigurationLimits.MaximumHighlightWords, ConfigurationLimits.MaximumStringLength, null);

    private static List<string> NormalizeValues(IEnumerable<string>? values, int maximum, int maximumLength, string? excluded)
    {
        var result = new List<string>();
        foreach (var value in (values ?? []).Take(maximum))
        {
            var normalized = value?.Trim() ?? string.Empty;
            if (normalized.Length == 0 || normalized.Length > maximumLength || normalized.Any(IsLineBreak) || (excluded is not null && IrcCaseMappingComparer.Equals(normalized, excluded, IrcCaseMapping.Rfc1459)) || result.Any(existing => IrcCaseMappingComparer.Equals(existing, normalized, IrcCaseMapping.Rfc1459)))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static string NormalizeValue(string? value, string fallback, int maximumLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0 && normalized.Length <= maximumLength && !normalized.Any(IsLineBreak) ? normalized : fallback;
    }

    private static string? NormalizeOptionalValue(string? value, int maximumLength)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximumLength || normalized.Any(IsLineBreak)
            ? null
            : normalized;
    }

    private static bool IsLineBreak(char character) => character is '\r' or '\n';
}
