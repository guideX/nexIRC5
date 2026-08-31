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
    public const int MaximumProfiles = 64;
    public const int MaximumStringLength = 256;
    public const int MaximumRealNameLength = 512;
    public const int MaximumChannelsPerProfile = 128;
    public const int MaximumAlternateNicknames = 8;
    public const int MaximumHighlightWords = 64;
    public const int MaximumChannelLength = 200;
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

    public NetworkConnectionOptions ToConnectionOptions()
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
            Reconnect = new ReconnectPolicy(profile.ReconnectEnabled, Math.Clamp(profile.ReconnectMaximumAttempts, 1, 10))
        };
    }
}

public sealed record NexIrcConfiguration
{
    public int SchemaVersion { get; init; } = ConfigurationSchema.CurrentVersion;

    public ApplicationPreferences Preferences { get; init; } = new();

    public List<NetworkProfile> Profiles { get; init; } = [];
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
        return new NexIrcConfiguration
        {
            SchemaVersion = ConfigurationSchema.CurrentVersion,
            Preferences = preferences with
            {
                CustomHighlightWords = words,
                LastSelectedNetworkProfileId = selected
            },
            Profiles = profiles
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
            ReconnectMaximumAttempts = Math.Clamp(profile.ReconnectMaximumAttempts, 1, 10)
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

    private static bool IsLineBreak(char character) => character is '\r' or '\n';
}
