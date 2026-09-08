using System.Globalization;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Core.Protocol;

public enum ChathistoryOperation
{
    Latest,
    Before,
    After,
    Around,
    Between,
    Targets
}

public enum ChathistoryRequestPurpose
{
    InitialContext,
    ReconnectGap,
    LoadOlder,
    ReconnectDiscovery,
    LoadContext,
    ExactGapRepair
}

public enum ChathistoryReferenceType
{
    MessageId,
    Timestamp
}

/// <summary>
/// A server reference is deliberately separate from local durable identity.
/// A local sequence or locally generated id can never be serialized as a
/// CHATHISTORY reference.
/// </summary>
public sealed record ChathistoryReference
{
    private ChathistoryReference(ChathistoryReferenceType type, string value, bool isWildcard)
    {
        Type = type;
        Value = value;
        IsWildcard = isWildcard;
    }

    public ChathistoryReferenceType Type { get; }

    public string Value { get; }

    public bool IsWildcard { get; }

    public bool IsConcrete => !IsWildcard;

    public static ChathistoryReference MessageId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Create(ChathistoryReferenceType.MessageId, value.StartsWith("msgid=", StringComparison.OrdinalIgnoreCase) ? value["msgid=".Length..] : value);
    }

    public static ChathistoryReference Timestamp(DateTimeOffset value) =>
        Create(ChathistoryReferenceType.Timestamp, value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

    public static ChathistoryReference Timestamp(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.StartsWith("timestamp=", StringComparison.OrdinalIgnoreCase))
        {
            value = value["timestamp=".Length..];
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new ArgumentException("The timestamp reference must be an RFC3339 timestamp.", nameof(value));
        }

        return Timestamp(timestamp);
    }

    public static ChathistoryReference Wildcard { get; } = new(ChathistoryReferenceType.Timestamp, "*", true);

    public string Serialize() => IsWildcard ? "*" : Value;

    /// <summary>
    /// Serializes the selector syntax required by the current draft.  The
    /// unprefixed value remains available for diagnostics and compatibility
    /// with older persisted request descriptions; it is never used on the
    /// current CHATHISTORY wire path.
    /// </summary>
    public string SerializeWire() => IsWildcard
        ? "*"
        : Type == ChathistoryReferenceType.MessageId
            ? $"msgid={Value}"
            : $"timestamp={Value}";

    public static bool AreCompatible(ChathistoryReference left, ChathistoryReference right) =>
        left is not null
        && right is not null
        && left.IsConcrete
        && right.IsConcrete
        && left.Type == right.Type;

    private static ChathistoryReference Create(ChathistoryReferenceType type, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length > 256 || value.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\0'))
        {
            throw new ArgumentException("The history reference is not a safe IRC token.", nameof(value));
        }

        return new ChathistoryReference(type, value, false);
    }
}

/// <summary>
/// Typed, bounded input to a server history request. Network and generation
/// are carried with the request so a response can never be applied to a
/// replacement session accidentally.
/// </summary>
public sealed record ChathistoryRequest
{
    public required Guid NetworkId { get; init; }

    public required int ConnectionGeneration { get; init; }

    /// <summary>
    /// Logical conversation identity for content requests.  TARGETS has no
    /// conversation target and therefore leaves this null.
    /// </summary>
    public string? Conversation { get; init; }

    /// <summary>
    /// IRC target for content requests.  TARGETS deliberately leaves this
    /// null instead of using a fake channel name.
    /// </summary>
    public string? Target { get; init; }

    public required ChathistoryOperation Operation { get; init; }

    /// <summary>
    /// Primary selector.  For TARGETS this is the lower timestamp bound.
    /// </summary>
    public required ChathistoryReference Reference { get; init; }

    /// <summary>Second selector used only by BETWEEN and TARGETS.</summary>
    public ChathistoryReference? SecondaryReference { get; init; }

    /// <summary>Readable alias for callers that model an interval as an end reference.</summary>
    public ChathistoryReference? EndReference { get; init; }

    public int Limit { get; init; }

    public ChathistoryRequestPurpose Purpose { get; init; } = ChathistoryRequestPurpose.LoadOlder;

    public bool IsDiscovery => Operation == ChathistoryOperation.Targets;

    public static ChathistoryRequest ForTargets(
        Guid networkId,
        int connectionGeneration,
        ChathistoryReference from,
        ChathistoryReference to,
        int limit,
        ChathistoryRequestPurpose purpose = ChathistoryRequestPurpose.ReconnectDiscovery) => new()
        {
            NetworkId = networkId,
            ConnectionGeneration = connectionGeneration,
            Operation = ChathistoryOperation.Targets,
            Reference = from,
            SecondaryReference = to,
            Limit = limit,
            Purpose = purpose
        };

    public static ChathistoryRequest ForBetween(
        Guid networkId,
        int connectionGeneration,
        string conversation,
        string target,
        ChathistoryReference older,
        ChathistoryReference newer,
        int limit,
        ChathistoryRequestPurpose purpose = ChathistoryRequestPurpose.ExactGapRepair) => new()
        {
            NetworkId = networkId,
            ConnectionGeneration = connectionGeneration,
            Conversation = conversation,
            Target = target,
            Operation = ChathistoryOperation.Between,
            Reference = older,
            SecondaryReference = newer,
            Limit = limit,
            Purpose = purpose
        };
}

public enum ChathistoryTargetKind
{
    Unknown,
    Channel,
    Query
}

public sealed record ChathistoryTarget(
    Guid NetworkId,
    int ConnectionGeneration,
    string Target,
    DateTimeOffset LatestTimestamp,
    ChathistoryTargetKind Kind);

/// <summary>
/// Capability and ISUPPORT-derived history support. The server limit of zero
/// means unlimited according to the draft; EffectiveMaximumRequestSize still
/// imposes nexIRC's own bound.
/// </summary>
public sealed record ChathistorySupport
{
    public const int DefaultClientMaximumRequestSize = 100;

    public const string CapabilityName = "draft/chathistory";

    public bool CapabilityEnabled { get; init; }

    public bool BatchEnabled { get; init; }

    public bool ServerTimeEnabled { get; init; }

    public bool MessageTagsEnabled { get; init; }

    public bool EventPlaybackEnabled { get; init; }

    public int? ServerMaximumRequestSize { get; init; }

    public int ClientMaximumRequestSize { get; init; } = DefaultClientMaximumRequestSize;

    public IReadOnlyList<ChathistoryReferenceType> SupportedReferenceTypes { get; init; } = [ChathistoryReferenceType.Timestamp];

    public ChathistoryReferenceType? PreferredReferenceType => SupportedReferenceTypes.Count == 0 ? null : SupportedReferenceTypes[0];

    public int EffectiveMaximumRequestSize => ServerMaximumRequestSize is > 0
        ? Math.Min(ClientMaximumRequestSize, ServerMaximumRequestSize.Value)
        : ClientMaximumRequestSize;

    public bool IsUsable => CapabilityEnabled && BatchEnabled && ServerTimeEnabled && MessageTagsEnabled;

    public bool Supports(ChathistoryReferenceType type) => SupportedReferenceTypes.Contains(type);

    public static ChathistorySupport From(CapabilitySnapshot capabilities, ISupportSnapshot isupport, int clientMaximumRequestSize = DefaultClientMaximumRequestSize)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(isupport);
        var references = isupport.MessageReferenceTypes.Count > 0
            ? isupport.MessageReferenceTypes
            : [ChathistoryReferenceType.Timestamp, ChathistoryReferenceType.MessageId];
        return new ChathistorySupport
        {
            CapabilityEnabled = capabilities.IsEnabled(CapabilityName),
            BatchEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.Batch),
            ServerTimeEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.ServerTime),
            MessageTagsEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.MessageTags),
            EventPlaybackEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.EventPlayback),
            ServerMaximumRequestSize = isupport.ChathistoryLimit,
            ClientMaximumRequestSize = Math.Clamp(clientMaximumRequestSize, 1, 10000),
            SupportedReferenceTypes = references.Distinct().ToArray()
        };
    }

    public static ChathistorySupport Unavailable { get; } = new() { ClientMaximumRequestSize = DefaultClientMaximumRequestSize };
}

public sealed record ChathistoryRequestBuildResult(
    IrcOutboundMessage Command,
    ChathistoryRequest Request,
    int EffectiveLimit);

/// <summary>Centralized bounded CHATHISTORY wire command construction.</summary>
public static class ChathistoryCommandBuilder
{
    public static IrcOutboundMessage Build(
        ChathistoryRequest request,
        ChathistorySupport support,
        int maximumOutboundLineBytes = IrcCommandBuilder.DefaultMaximumLineBytes) =>
        BuildValidated(request, support, maximumOutboundLineBytes).Command;

    public static ChathistoryRequestBuildResult BuildValidated(
        ChathistoryRequest request,
        ChathistorySupport support,
        int maximumOutboundLineBytes = IrcCommandBuilder.DefaultMaximumLineBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(support);
        if (!support.IsUsable)
        {
            throw new InvalidOperationException("CHATHISTORY requires draft/chathistory, batch, server-time, and message-tags to be enabled.");
        }

        if (request.ConnectionGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ConnectionGeneration));
        }

        var operation = request.Operation.ToString().ToUpperInvariant();
        if (request.Operation == ChathistoryOperation.Targets)
        {
            if (request.Target is not null || request.Conversation is not null)
            {
                throw new ArgumentException("TARGETS must not carry a conversation target.", nameof(request));
            }

            if (request.Reference.IsWildcard
                || request.Reference.Type != ChathistoryReferenceType.Timestamp
                || !support.Supports(ChathistoryReferenceType.Timestamp)
                || EffectiveSecondary(request) is not { IsWildcard: false, Type: ChathistoryReferenceType.Timestamp })
            {
                throw new ArgumentException("TARGETS requires two concrete timestamp references.", nameof(request));
            }

            var from = ParseTimestamp(request.Reference.Value);
            var to = ParseTimestamp(EffectiveSecondary(request)!.Value);
            if (from > to)
            {
                throw new ArgumentException("The TARGETS timestamp interval must be ordered.", nameof(request));
            }

            var targetsLimit = ValidateAndClampLimit(request.Limit, support);
            var targetsCommand = new IrcCommandBuilder(maximumOutboundLineBytes).Build(
                "CHATHISTORY",
                [operation, request.Reference.SerializeWire(), EffectiveSecondary(request)!.SerializeWire(), targetsLimit.ToString(CultureInfo.InvariantCulture)]);
            return new ChathistoryRequestBuildResult(targetsCommand, request with { Limit = targetsLimit }, targetsLimit);
        }

        ValidateTarget(request.Target);
        if (string.IsNullOrWhiteSpace(request.Conversation) || request.Conversation.Length > 256)
        {
            throw new ArgumentException("A bounded logical conversation identity is required.", nameof(request));
        }

        if (EffectiveSecondary(request) is not null && request.Operation != ChathistoryOperation.Between)
        {
            throw new ArgumentException("A second selector is valid only for BETWEEN.", nameof(request));
        }

        if (request.Operation == ChathistoryOperation.Between && EffectiveSecondary(request) is null)
        {
            throw new ArgumentException("BETWEEN requires two concrete server references.", nameof(request));
        }

        if (!request.Operation.Equals(ChathistoryOperation.Latest) && request.Reference.IsWildcard)
        {
            throw new ArgumentException("BEFORE and AFTER require a concrete server reference.", nameof(request));
        }

        if (!request.Reference.IsWildcard && !support.Supports(request.Reference.Type))
        {
            throw new InvalidOperationException($"The server did not advertise {request.Reference.Type} CHATHISTORY references.");
        }

        if (request.Operation == ChathistoryOperation.Between
            && (request.Reference.IsWildcard || EffectiveSecondary(request)!.IsWildcard
                || !support.Supports(EffectiveSecondary(request)!.Type)))
        {
            throw new ArgumentException("BETWEEN requires two supported concrete references.", nameof(request));
        }

        var limit = ValidateAndClampLimit(request.Limit, support);
        var parameters = request.Operation == ChathistoryOperation.Between
            ? new[]
            {
                operation,
                request.Target!,
                request.Reference.SerializeWire(),
                EffectiveSecondary(request)!.SerializeWire(),
                limit.ToString(CultureInfo.InvariantCulture)
            }
            : new[]
            {
                operation,
                request.Target!,
                request.Reference.SerializeWire(),
                limit.ToString(CultureInfo.InvariantCulture)
            };
        var command = new IrcCommandBuilder(maximumOutboundLineBytes).Build(
            "CHATHISTORY",
            parameters);
        return new ChathistoryRequestBuildResult(command, request with { Limit = limit }, limit);
    }

    private static int ValidateAndClampLimit(int requested, ChathistorySupport support)
    {
        if (requested <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), "The CHATHISTORY limit must be positive.");
        }

        return Math.Min(requested, support.EffectiveMaximumRequestSize);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            ? timestamp
            : throw new ArgumentException("The history timestamp is invalid.", nameof(value));

    private static ChathistoryReference? EffectiveSecondary(ChathistoryRequest request) => request.SecondaryReference ?? request.EndReference;

    private static void ValidateTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target.Length > 256
            || target.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == ':' || character == '\0'))
        {
            throw new ArgumentException("The CHATHISTORY target must be one safe IRC token.", nameof(target));
        }
    }
}

public enum ChathistoryRequestCompletion
{
    Pending,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    Disconnected,
    StaleGeneration
}

public sealed record ChathistoryResult(
    long RequestId,
    ChathistoryRequest Request,
    ChathistoryRequestCompletion Completion,
    IReadOnlyList<IrcSemanticEvent> Messages,
    string? Failure = null,
    bool Exhausted = false)
{
    public IReadOnlyList<ChathistoryTarget> Targets { get; init; } = Array.Empty<ChathistoryTarget>();

    /// <summary>Batch identity and shape retained for conservative callers.</summary>
    public string? BatchId { get; init; }

    public string? BatchType { get; init; }

    public string? BatchTarget { get; init; }

    /// <summary>
    /// True only when the server supplied explicit end-of-history evidence on
    /// the opening history batch.  It is intentionally distinct from an
    /// empty result, which is valid evidence only for the requested interval.
    /// </summary>
    public bool HistoryEndSignaled => Exhausted;

    public bool Succeeded => Completion == ChathistoryRequestCompletion.Succeeded;

    public bool IsEmpty => Messages.Count == 0;

    public int MessageCount
    {
        get
        {
            var unique = new HashSet<IrcMessage>();
            foreach (var message in Messages)
            {
                unique.Add(message.Message);
            }

            return unique.Count;
        }
    }
}

public sealed record ChathistoryConversationState(
    string Conversation,
    int ConnectionGeneration,
    bool RequestActive,
    bool BeginningReached,
    bool LastRequestFailed,
    string? LastFailure)
{
    public static ChathistoryConversationState Empty(string conversation, int generation) =>
        new(conversation, generation, false, false, false, null);
}
