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
    Between
}

public enum ChathistoryRequestPurpose
{
    InitialContext,
    ReconnectGap,
    LoadOlder
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

    public static ChathistoryReference MessageId(string value) => Create(ChathistoryReferenceType.MessageId, value);

    public static ChathistoryReference Timestamp(DateTimeOffset value) =>
        Create(ChathistoryReferenceType.Timestamp, value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

    public static ChathistoryReference Timestamp(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp))
        {
            throw new ArgumentException("The timestamp reference must be an RFC3339 timestamp.", nameof(value));
        }

        return Timestamp(timestamp);
    }

    public static ChathistoryReference Wildcard { get; } = new(ChathistoryReferenceType.Timestamp, "*", true);

    public string Serialize() => IsWildcard ? "*" : Value;

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

    public required string Conversation { get; init; }

    public required string Target { get; init; }

    public required ChathistoryOperation Operation { get; init; }

    public required ChathistoryReference Reference { get; init; }

    public int Limit { get; init; }

    public ChathistoryRequestPurpose Purpose { get; init; } = ChathistoryRequestPurpose.LoadOlder;
}

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
            : [ChathistoryReferenceType.Timestamp];
        return new ChathistorySupport
        {
            CapabilityEnabled = capabilities.IsEnabled(CapabilityName),
            BatchEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.Batch),
            ServerTimeEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.ServerTime),
            MessageTagsEnabled = capabilities.IsEnabled(IrcCapabilityCatalog.MessageTags),
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

        ValidateTarget(request.Target);
        if (string.IsNullOrWhiteSpace(request.Conversation) || request.Conversation.Length > 256)
        {
            throw new ArgumentException("A bounded logical conversation identity is required.", nameof(request));
        }

        if (request.ConnectionGeneration < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request.ConnectionGeneration));
        }

        var operation = request.Operation.ToString().ToUpperInvariant();
        if (request.Operation is ChathistoryOperation.Around or ChathistoryOperation.Between)
        {
            throw new NotSupportedException($"CHATHISTORY {operation} is reserved for a later bounded phase.");
        }

        if (!request.Operation.Equals(ChathistoryOperation.Latest) && request.Reference.IsWildcard)
        {
            throw new ArgumentException("BEFORE and AFTER require a concrete server reference.", nameof(request));
        }

        if (!request.Reference.IsWildcard && !support.Supports(request.Reference.Type))
        {
            throw new InvalidOperationException($"The server did not advertise {request.Reference.Type} CHATHISTORY references.");
        }

        var limit = request.Limit;
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Limit), "The CHATHISTORY limit must be positive.");
        }

        limit = Math.Min(limit, support.EffectiveMaximumRequestSize);
        var command = new IrcCommandBuilder(maximumOutboundLineBytes).Build(
            "CHATHISTORY",
            [operation, request.Target, request.Reference.Serialize(), limit.ToString(CultureInfo.InvariantCulture)]);
        return new ChathistoryRequestBuildResult(command, request with { Limit = limit }, limit);
    }

    private static void ValidateTarget(string target)
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
