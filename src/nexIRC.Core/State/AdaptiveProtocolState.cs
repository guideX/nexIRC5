using System.Collections.ObjectModel;
using nexIRC.Core.Protocol;

namespace nexIRC.Core.State;

public sealed class IrcCapability
{
    internal IrcCapability(string rawToken, string name, string? rawValue, string? value)
    {
        RawToken = rawToken;
        Name = name;
        RawValue = rawValue;
        Value = value;
    }

    public string RawToken { get; }

    public string Name { get; }

    public string? RawValue { get; }

    public string? Value { get; }
}

public sealed class CapabilitySnapshot
{
    internal CapabilitySnapshot(
        IReadOnlyDictionary<string, IrcCapability> available,
        IReadOnlySet<string> enabled,
        IReadOnlyList<string> rawAdvertisedTokens,
        IReadOnlyList<string> requestedTokens,
        CapNegotiationState negotiationState)
    {
        Available = available;
        Enabled = enabled;
        RawAdvertisedTokens = rawAdvertisedTokens;
        RequestedTokens = requestedTokens;
        NegotiationState = negotiationState;
    }

    public IReadOnlyDictionary<string, IrcCapability> Available { get; }

    public IReadOnlySet<string> Enabled { get; }

    public IReadOnlyList<string> RawAdvertisedTokens { get; }

    public IReadOnlyList<string> RequestedTokens { get; }

    public CapNegotiationState NegotiationState { get; }

    public bool IsAvailable(string name) => Available.ContainsKey(Normalize(name));

    public bool IsEnabled(string name) => Enabled.Contains(Normalize(name));

    public static CapabilitySnapshot Empty { get; } = new(
        new ReadOnlyDictionary<string, IrcCapability>(new Dictionary<string, IrcCapability>(StringComparer.Ordinal)),
        new HashSet<string>(StringComparer.Ordinal),
        Array.Empty<string>(),
        Array.Empty<string>(),
        CapNegotiationState.NotStarted);

    internal static string Normalize(string name) => name.ToLowerInvariant();
}

public enum CapNegotiationState
{
    NotStarted,
    Listing,
    Requesting,
    Ended
}

public sealed class CapNegotiationResult
{
    internal CapNegotiationResult(CapabilitySnapshot snapshot, IReadOnlyList<IrcOutboundMessage> commands)
    {
        Snapshot = snapshot;
        Commands = commands;
    }

    public CapabilitySnapshot Snapshot { get; }

    public IReadOnlyList<IrcOutboundMessage> Commands { get; }
}

/// <summary>
/// Handles CAP protocol messages without coupling capability names to feature code.
/// </summary>
public sealed class IrcCapabilityNegotiator
{
    private readonly IrcCommandBuilder _commandBuilder;
    private readonly Dictionary<string, IrcCapability> _available = new(StringComparer.Ordinal);
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);
    private readonly List<string> _rawAdvertisedTokens = [];
    private readonly List<string> _requestedTokens;
    private CapNegotiationState _state;

    public IrcCapabilityNegotiator(IEnumerable<string>? requestedCapabilities = null, int maximumOutboundLineBytes = IrcCommandBuilder.DefaultMaximumLineBytes)
    {
        _requestedTokens = (requestedCapabilities ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(CapabilitySnapshot.Normalize)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _commandBuilder = new IrcCommandBuilder(maximumOutboundLineBytes);
    }

    public CapabilitySnapshot Snapshot => CreateSnapshot();

    public CapNegotiationResult Start()
    {
        _state = CapNegotiationState.Listing;
        return Result(_commandBuilder.Build("CAP", ["LS", "302"]));
    }

    public CapNegotiationResult Handle(IrcMessage message)
    {
        if (!string.Equals(message.Command, "CAP", StringComparison.Ordinal))
        {
            return Result();
        }

        var subcommandIndex = FindSubcommand(message);
        if (subcommandIndex < 0)
        {
            return Result();
        }

        var subcommand = message.MiddleParameters[subcommandIndex].ToUpperInvariant();
        var tokens = message.HasTrailingParameter
            ? SplitCapabilityTokens(message.TrailingParameter!)
            : Array.Empty<string>();

        switch (subcommand)
        {
            case "LS":
                foreach (var token in tokens)
                {
                    AddAdvertised(token);
                }

                var hasMore = subcommandIndex + 1 < message.MiddleParameters.Count && message.MiddleParameters[subcommandIndex + 1] == "*";
                if (hasMore)
                {
                    _state = CapNegotiationState.Listing;
                    return Result();
                }

                var requested = _requestedTokens.Where(_available.ContainsKey).ToArray();
                if (requested.Length == 0)
                {
                    _state = CapNegotiationState.Ended;
                    return Result(_commandBuilder.Build("CAP", ["END"]));
                }

                _state = CapNegotiationState.Requesting;
                return Result(_commandBuilder.Build("CAP", ["REQ"], string.Join(' ', requested)));

            case "ACK":
                ApplyAcknowledgement(tokens);
                if (_state == CapNegotiationState.Requesting)
                {
                    _state = CapNegotiationState.Ended;
                    return Result(_commandBuilder.Build("CAP", ["END"]));
                }

                return Result();

            case "NAK":
                if (_state == CapNegotiationState.Requesting)
                {
                    _state = CapNegotiationState.Ended;
                    return Result(_commandBuilder.Build("CAP", ["END"]));
                }

                return Result();

            case "NEW":
                foreach (var token in tokens)
                {
                    AddAdvertised(token);
                }

                return Result();

            case "DEL":
                foreach (var token in tokens)
                {
                    RemoveAdvertised(token);
                }

                return Result();

            case "END":
                _state = CapNegotiationState.Ended;
                return Result();

            default:
                return Result();
        }
    }

    public void Reset()
    {
        _available.Clear();
        _enabled.Clear();
        _rawAdvertisedTokens.Clear();
        _state = CapNegotiationState.NotStarted;
    }

    private static int FindSubcommand(IrcMessage message)
    {
        for (var index = 0; index < message.MiddleParameters.Count; index++)
        {
            var value = message.MiddleParameters[index].ToUpperInvariant();
            if (value is "LS" or "REQ" or "ACK" or "NAK" or "LIST" or "CLEAR" or "END" or "NEW" or "DEL")
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] SplitCapabilityTokens(string value) => value
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void AddAdvertised(string rawToken)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var equals = rawToken.IndexOf('=');
        var rawName = equals >= 0 ? rawToken[..equals] : rawToken;
        var name = CapabilitySnapshot.Normalize(rawName.TrimStart('-'));
        if (name.Length == 0)
        {
            return;
        }

        var rawValue = equals >= 0 ? rawToken[(equals + 1)..] : null;
        _available[name] = new IrcCapability(rawToken, name, rawValue, rawValue);
        _rawAdvertisedTokens.Add(rawToken);
    }

    private void ApplyAcknowledgement(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            var name = CapabilityName(token);
            if (name.Length == 0)
            {
                continue;
            }

            if (token.Length > 0 && token[0] == '-')
            {
                _enabled.Remove(name);
            }
            else
            {
                _enabled.Add(name);
                AddAdvertised(token);
            }
        }
    }

    private void RemoveAdvertised(string token)
    {
        var name = CapabilityName(token);
        _available.Remove(name);
        _enabled.Remove(name);
    }

    private static string CapabilityName(string token)
    {
        var withoutSign = token.TrimStart('-');
        var equals = withoutSign.IndexOf('=');
        return CapabilitySnapshot.Normalize(equals >= 0 ? withoutSign[..equals] : withoutSign);
    }

    private CapNegotiationResult Result(params IrcOutboundMessage[] commands) => new(CreateSnapshot(), commands);

    private CapabilitySnapshot CreateSnapshot() => new(
        new ReadOnlyDictionary<string, IrcCapability>(new Dictionary<string, IrcCapability>(_available, StringComparer.Ordinal)),
        new HashSet<string>(_enabled, StringComparer.Ordinal),
        _rawAdvertisedTokens.ToArray(),
        _requestedTokens.ToArray(),
        _state);
}

public sealed record IrcPrefixGrammar(
    string RawValue,
    IReadOnlyList<char> Modes,
    IReadOnlyList<char> Prefixes,
    IReadOnlyDictionary<char, char> ModeToPrefix,
    IReadOnlyDictionary<char, char> PrefixToMode);

public sealed record IrcChannelModeGrammar(
    string RawValue,
    IReadOnlySet<char> ListModes,
    IReadOnlySet<char> ParameterAlwaysModes,
    IReadOnlySet<char> ParameterWhenSetModes,
    IReadOnlySet<char> NoParameterModes)
{
    public IReadOnlySet<char> AllModes => ListModes
        .Concat(ParameterAlwaysModes)
        .Concat(ParameterWhenSetModes)
        .Concat(NoParameterModes)
        .ToHashSet();
}

public sealed record IrcISupportToken(
    string RawToken,
    string Name,
    string? RawValue,
    bool IsNegated,
    string? Value,
    string SourceLine);

public sealed class ISupportSnapshot
{
    internal ISupportSnapshot(
        IReadOnlyList<IrcISupportToken> rawTokens,
        IReadOnlyDictionary<string, IrcISupportToken> tokens,
        IReadOnlySet<string> removedTokens,
        string? networkName,
        IrcCaseMapping caseMapping,
        bool hasCaseMapping,
        IReadOnlySet<char> channelTypes,
        bool hasChannelTypes,
        IrcPrefixGrammar? prefix,
        IrcChannelModeGrammar? channelModes,
        string statusMessagePrefixes,
        bool supportsExcepts,
        bool supportsInvex,
        int? lineLength,
        IReadOnlyDictionary<char, int> maxList,
        IReadOnlyDictionary<string, int?> targetMax,
        bool utf8Only)
    {
        RawTokens = rawTokens;
        Tokens = tokens;
        RemovedTokens = removedTokens;
        NetworkName = networkName;
        CaseMapping = caseMapping;
        HasCaseMapping = hasCaseMapping;
        ChannelTypes = channelTypes;
        HasChannelTypes = hasChannelTypes;
        Prefix = prefix;
        ChannelModes = channelModes;
        StatusMessagePrefixes = statusMessagePrefixes;
        SupportsExcepts = supportsExcepts;
        SupportsInvex = supportsInvex;
        LineLength = lineLength;
        MaxList = maxList;
        TargetMax = targetMax;
        Utf8Only = utf8Only;
    }

    public IReadOnlyList<IrcISupportToken> RawTokens { get; }

    public IReadOnlyDictionary<string, IrcISupportToken> Tokens { get; }

    public IReadOnlySet<string> RemovedTokens { get; }

    public string? NetworkName { get; }

    public IrcCaseMapping CaseMapping { get; }

    public bool HasCaseMapping { get; }

    public IReadOnlySet<char> ChannelTypes { get; }

    public bool HasChannelTypes { get; }

    public IrcPrefixGrammar? Prefix { get; }

    public IrcChannelModeGrammar? ChannelModes { get; }

    public string StatusMessagePrefixes { get; }

    public bool SupportsExcepts { get; }

    public bool SupportsInvex { get; }

    public int? LineLength { get; }

    public IReadOnlyDictionary<char, int> MaxList { get; }

    public IReadOnlyDictionary<string, int?> TargetMax { get; }

    public bool Utf8Only { get; }

    public bool Contains(string name) => Tokens.ContainsKey(name.ToUpperInvariant()) && !RemovedTokens.Contains(name.ToUpperInvariant());

    public static ISupportSnapshot Empty { get; } = new(
        Array.Empty<IrcISupportToken>(),
        new ReadOnlyDictionary<string, IrcISupportToken>(new Dictionary<string, IrcISupportToken>(StringComparer.Ordinal)),
        new HashSet<string>(StringComparer.Ordinal),
        null,
        IrcCaseMapping.Rfc1459,
        false,
        new HashSet<char>(),
        false,
        null,
        null,
        string.Empty,
        false,
        false,
        null,
        new Dictionary<char, int>(),
        new Dictionary<string, int?>(StringComparer.Ordinal),
        false);
}

public enum IrcCaseMapping
{
    Rfc1459,
    StrictRfc1459,
    Ascii,
    Unknown
}

public sealed class ISupportState
{
    private readonly List<IrcISupportToken> _rawTokens = [];
    private readonly Dictionary<string, IrcISupportToken> _tokens = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removedTokens = new(StringComparer.Ordinal);

    public ISupportSnapshot Snapshot => CreateSnapshot();

    public ISupportSnapshot Apply(IrcMessage message)
    {
        if (message.NumericCommand != 5)
        {
            return Snapshot;
        }

        foreach (var rawToken in message.MiddleParameters.Skip(1))
        {
            ApplyToken(rawToken, message.RawLine);
        }

        return CreateSnapshot();
    }

    public void Reset()
    {
        _rawTokens.Clear();
        _tokens.Clear();
        _removedTokens.Clear();
    }

    private void ApplyToken(string rawToken, string sourceLine)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var isNegated = rawToken[0] == '-';
        var tokenWithoutSign = isNegated ? rawToken[1..] : rawToken;
        var equals = tokenWithoutSign.IndexOf('=');
        var rawName = equals >= 0 ? tokenWithoutSign[..equals] : tokenWithoutSign;
        var name = rawName.ToUpperInvariant();
        if (name.Length == 0)
        {
            return;
        }

        var rawValue = equals >= 0 ? tokenWithoutSign[(equals + 1)..] : null;
        var token = new IrcISupportToken(rawToken, name, rawValue, isNegated, rawValue, sourceLine);
        _rawTokens.Add(token);
        _tokens[name] = token;
        if (isNegated)
        {
            _removedTokens.Add(name);
        }
        else
        {
            _removedTokens.Remove(name);
        }
    }

    private ISupportSnapshot CreateSnapshot()
    {
        var network = PositiveValue("NETWORK");
        var caseMappingValue = PositiveValue("CASEMAPPING");
        var hasCaseMapping = caseMappingValue is not null;
        var caseMapping = caseMappingValue?.ToLowerInvariant() switch
        {
            "ascii" => IrcCaseMapping.Ascii,
            "strict-rfc1459" => IrcCaseMapping.StrictRfc1459,
            "rfc1459" => IrcCaseMapping.Rfc1459,
            null => IrcCaseMapping.Rfc1459,
            _ => IrcCaseMapping.Unknown
        };

        var channelTypesValue = PositiveValue("CHANTYPES");
        var channelTypes = channelTypesValue?.ToHashSet() ?? new HashSet<char>();
        return new ISupportSnapshot(
            _rawTokens.ToArray(),
            new ReadOnlyDictionary<string, IrcISupportToken>(new Dictionary<string, IrcISupportToken>(_tokens, StringComparer.Ordinal)),
            new HashSet<string>(_removedTokens, StringComparer.Ordinal),
            network,
            caseMapping,
            hasCaseMapping,
            channelTypes,
            channelTypesValue is not null,
            ParsePrefix(PositiveValue("PREFIX")),
            ParseChannelModes(PositiveValue("CHANMODES")),
            PositiveValue("STATUSMSG") ?? string.Empty,
            HasPositive("EXCEPTS"),
            HasPositive("INVEX"),
            ParseInt(PositiveValue("LINELEN")),
            ParseMaxList(PositiveValue("MAXLIST")),
            ParseTargetMax(PositiveValue("TARGMAX")),
            HasPositive("UTF8ONLY"));
    }

    private string? PositiveValue(string name)
    {
        var normalized = name.ToUpperInvariant();
        return _removedTokens.Contains(normalized) || !_tokens.TryGetValue(normalized, out var token) || token.IsNegated
            ? null
            : token.Value ?? string.Empty;
    }

    private bool HasPositive(string name) => _tokens.ContainsKey(name) && !_removedTokens.Contains(name);

    private static int? ParseInt(string? value) => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;

    private static IrcPrefixGrammar? ParsePrefix(string? value)
    {
        if (string.IsNullOrEmpty(value) || value[0] != '(')
        {
            return null;
        }

        var close = value.IndexOf(')');
        if (close <= 1 || close == value.Length - 1)
        {
            return null;
        }

        var modeText = value[1..close];
        var prefixText = value[(close + 1)..];
        if (modeText.Length != prefixText.Length)
        {
            return null;
        }

        var modeToPrefix = new Dictionary<char, char>();
        var prefixToMode = new Dictionary<char, char>();
        for (var index = 0; index < modeText.Length; index++)
        {
            modeToPrefix[modeText[index]] = prefixText[index];
            prefixToMode[prefixText[index]] = modeText[index];
        }

        return new IrcPrefixGrammar(value, modeText.ToArray(), prefixText.ToArray(), modeToPrefix, prefixToMode);
    }

    private static IrcChannelModeGrammar? ParseChannelModes(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var categories = value.Split(',', StringSplitOptions.None);
        var sets = Enumerable.Range(0, 4)
            .Select(index => (categories.Length > index ? categories[index] : string.Empty).ToHashSet())
            .ToArray();
        return new IrcChannelModeGrammar(value, sets[0], sets[1], sets[2], sets[3]);
    }

    private static Dictionary<char, int> ParseMaxList(string? value)
    {
        var result = new Dictionary<char, int>();
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = item.IndexOf(':');
            if (colon <= 0 || !int.TryParse(item[(colon + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var limit))
            {
                continue;
            }

            foreach (var mode in item[..colon])
            {
                result[mode] = limit;
            }
        }

        return result;
    }

    private static Dictionary<string, int?> ParseTargetMax(string? value)
    {
        var result = new Dictionary<string, int?>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value))
        {
            return result;
        }

        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = item.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var command = item[..colon].ToUpperInvariant();
            result[command] = ParseInt(item[(colon + 1)..]);
        }

        return result;
    }
}

public sealed record ServerFeatureHints(
    IReadOnlySet<char>? ChannelTypes = null,
    IrcPrefixGrammar? Prefix = null,
    IrcChannelModeGrammar? ChannelModes = null,
    int? LineLength = null);

public sealed record IRCdProfile(
    string Name,
    IrcdFamily Family,
    string Description,
    ServerFeatureHints? Hints = null,
    IReadOnlySet<string>? Quirks = null);

public sealed record ServicesProfile(string Name, string Description);

public sealed record NetworkProfile(
    string Name,
    IrcdFamily? LikelyIrcd = null,
    string? ServicesProfileName = null,
    ServerFeatureHints? Hints = null);

public sealed record ServerProfileSet(
    IRCdProfile? Ircd = null,
    NetworkProfile? Network = null,
    ServicesProfile? Services = null);

public enum IrcdFamily
{
    Unknown,
    Generic,
    UnrealIRCd,
    InspIRCd,
    Solanum,
    IrcdHybrid,
    Ergo
}

public static class ServerProfileCatalog
{
    public static IReadOnlyList<IRCdProfile> SeedIrcdProfiles { get; } =
    [
        new IRCdProfile("generic", IrcdFamily.Generic, "Generic modern IRC compatibility hints."),
        new IRCdProfile("unrealircd", IrcdFamily.UnrealIRCd, "UnrealIRCd family hints."),
        new IRCdProfile("inspircd", IrcdFamily.InspIRCd, "InspIRCd family hints."),
        new IRCdProfile("solanum", IrcdFamily.Solanum, "Solanum family hints."),
        new IRCdProfile("ircd-hybrid", IrcdFamily.IrcdHybrid, "ircd-hybrid family hints."),
        new IRCdProfile("ergo", IrcdFamily.Ergo, "Ergo family hints.")
    ];
}

public sealed class ServerFeatureSet
{
    private ServerFeatureSet(
        CapabilitySnapshot capabilities,
        ISupportSnapshot runtimeISupport,
        ServerProfileSet? profiles,
        ServerIdentity identity)
    {
        Capabilities = capabilities;
        RuntimeISupport = runtimeISupport;
        Profiles = profiles;
        Identity = identity;
        NetworkName = runtimeISupport.NetworkName ?? profiles?.Network?.Name;
        CaseMapping = runtimeISupport.HasCaseMapping ? runtimeISupport.CaseMapping : IrcCaseMapping.Rfc1459;
        ChannelTypes = runtimeISupport.HasChannelTypes
            ? runtimeISupport.ChannelTypes
            : profiles?.Ircd?.Hints?.ChannelTypes ?? profiles?.Network?.Hints?.ChannelTypes ?? new HashSet<char>("#&+!".ToCharArray());
        Prefix = runtimeISupport.Prefix ?? profiles?.Network?.Hints?.Prefix ?? profiles?.Ircd?.Hints?.Prefix;
        ChannelModes = runtimeISupport.ChannelModes ?? profiles?.Network?.Hints?.ChannelModes ?? profiles?.Ircd?.Hints?.ChannelModes;
        LineLength = runtimeISupport.LineLength ?? profiles?.Network?.Hints?.LineLength ?? profiles?.Ircd?.Hints?.LineLength ?? 512;
    }

    public CapabilitySnapshot Capabilities { get; }

    public ISupportSnapshot RuntimeISupport { get; }

    public ServerProfileSet? Profiles { get; }

    public ServerIdentity Identity { get; }

    public string? NetworkName { get; }

    public IrcCaseMapping CaseMapping { get; }

    public IReadOnlySet<char> ChannelTypes { get; }

    public IrcPrefixGrammar? Prefix { get; }

    public IrcChannelModeGrammar? ChannelModes { get; }

    public int LineLength { get; }

    public static ServerFeatureSet Build(CapabilitySnapshot capabilities, ISupportSnapshot runtimeISupport, ServerProfileSet? profiles, ServerIdentity identity) =>
        new(capabilities, runtimeISupport, profiles, identity);
}

public enum ServerIdentityEvidenceType
{
    MyInfo,
    SoftwareVersion,
    NetworkToken,
    IsupportFingerprint,
    CapabilityFingerprint,
    Hostname,
    ManualOverride
}

public sealed record ServerIdentityEvidence(
    ServerIdentityEvidenceType Type,
    string Value,
    double Confidence,
    string Source,
    IrcdFamily? Candidate = null);

public sealed class ServerIdentity
{
    internal ServerIdentity(string? networkName, IrcdFamily probableIrcd, double confidence, IReadOnlyList<ServerIdentityEvidence> evidence)
    {
        NetworkName = networkName;
        ProbableIrcd = probableIrcd;
        Confidence = confidence;
        Evidence = evidence;
    }

    public string? NetworkName { get; }

    public IrcdFamily ProbableIrcd { get; }

    public double Confidence { get; }

    public IReadOnlyList<ServerIdentityEvidence> Evidence { get; }

    public static ServerIdentity Unknown { get; } = new(null, IrcdFamily.Unknown, 0, Array.Empty<ServerIdentityEvidence>());
}

public sealed class ServerIdentityDetector
{
    private readonly List<ServerIdentityEvidence> _evidence = [];
    private string? _networkName;
    private string? _manualNetworkName;
    private IrcdFamily? _manualIrcd;

    public ServerIdentity Snapshot => BuildSnapshot();

    public void ObserveMyInfo(IrcMessage message)
    {
        if (message.NumericCommand != 4 || message.MiddleParameters.Count < 2)
        {
            return;
        }

        var value = string.Join(' ', message.MiddleParameters.Skip(1));
        AddSoftwareEvidence(value, ServerIdentityEvidenceType.MyInfo, 0.95, "004");
    }

    public void ObserveISupport(ISupportSnapshot snapshot, string source = "005")
    {
        if (snapshot.NetworkName is not null)
        {
            _networkName = snapshot.NetworkName;
            _evidence.Add(new ServerIdentityEvidence(ServerIdentityEvidenceType.NetworkToken, snapshot.NetworkName, 0.98, source));
        }

        var fingerprint = string.Join(',', snapshot.Tokens.Keys.OrderBy(static value => value, StringComparer.Ordinal));
        if (fingerprint.Length > 0)
        {
            _evidence.Add(new ServerIdentityEvidence(ServerIdentityEvidenceType.IsupportFingerprint, fingerprint, 0.15, source));
        }
    }

    public void ObserveCapabilities(CapabilitySnapshot snapshot, string source = "CAP")
    {
        if (snapshot.Available.Count == 0)
        {
            return;
        }

        _evidence.Add(new ServerIdentityEvidence(
            ServerIdentityEvidenceType.CapabilityFingerprint,
            string.Join(',', snapshot.Available.Keys.OrderBy(static value => value, StringComparer.Ordinal)),
            0.15,
            source));
    }

    public void ObserveHostname(string hostname, string source = "endpoint")
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return;
        }

        var candidate = CandidateFromText(hostname);
        if (candidate is not null)
        {
            _evidence.Add(new ServerIdentityEvidence(ServerIdentityEvidenceType.Hostname, hostname, 0.2, source, candidate));
        }
    }

    public void SetManualOverride(string? networkName, IrcdFamily? ircd)
    {
        _manualNetworkName = networkName;
        _manualIrcd = ircd;
        if (networkName is not null)
        {
            _evidence.Add(new ServerIdentityEvidence(ServerIdentityEvidenceType.ManualOverride, networkName, 0.7, "manual-network"));
        }

        if (ircd.HasValue)
        {
            _evidence.Add(new ServerIdentityEvidence(ServerIdentityEvidenceType.ManualOverride, ircd.Value.ToString(), 0.7, "manual-ircd", ircd));
        }
    }

    public void Reset()
    {
        _evidence.Clear();
        _networkName = null;
        _manualNetworkName = null;
        _manualIrcd = null;
    }

    private void AddSoftwareEvidence(string value, ServerIdentityEvidenceType type, double confidence, string source)
    {
        var candidate = CandidateFromText(value);
        _evidence.Add(new ServerIdentityEvidence(type, value, confidence, source, candidate));
        if (candidate is not null)
        {
            _evidence.Add(new ServerIdentityEvidence(ServerIdentityEvidenceType.SoftwareVersion, value, confidence, source, candidate));
        }
    }

    private ServerIdentity BuildSnapshot()
    {
        var candidateScores = new Dictionary<IrcdFamily, double>();
        foreach (var item in _evidence.Where(static evidence => evidence.Candidate.HasValue))
        {
            var candidate = item.Candidate!.Value;
            candidateScores[candidate] = candidateScores.TryGetValue(candidate, out var score)
                ? score + item.Confidence
                : item.Confidence;
        }

        var best = candidateScores.OrderByDescending(static pair => pair.Value).FirstOrDefault();
        var strongest = _evidence.Where(static evidence => evidence.Candidate.HasValue).OrderByDescending(static evidence => evidence.Confidence).FirstOrDefault();
        var probable = best.Key;
        var confidence = best.Value;
        if (strongest is null || confidence < 0.6)
        {
            probable = IrcdFamily.Unknown;
            confidence = 0;
        }

        if (_manualIrcd.HasValue && (strongest is null || strongest.Confidence < 0.7))
        {
            probable = _manualIrcd.Value;
            confidence = 0.7;
        }

        return new ServerIdentity(_networkName ?? _manualNetworkName, probable, Math.Min(1, confidence), _evidence.ToArray());
    }

    private static IrcdFamily? CandidateFromText(string value)
    {
        var text = value.ToLowerInvariant();
        if (text.Contains("unrealircd", StringComparison.Ordinal) || text.Contains("unreal", StringComparison.Ordinal))
        {
            return IrcdFamily.UnrealIRCd;
        }

        if (text.Contains("inspircd", StringComparison.Ordinal))
        {
            return IrcdFamily.InspIRCd;
        }

        if (text.Contains("solanum", StringComparison.Ordinal))
        {
            return IrcdFamily.Solanum;
        }

        if (text.Contains("ircd-hybrid", StringComparison.Ordinal) || text.Contains("hybrid", StringComparison.Ordinal))
        {
            return IrcdFamily.IrcdHybrid;
        }

        if (text.Contains("ergo", StringComparison.Ordinal))
        {
            return IrcdFamily.Ergo;
        }

        return null;
    }
}
