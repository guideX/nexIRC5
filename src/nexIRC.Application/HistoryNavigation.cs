using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum HistoryNavigationOutcome
{
    ExactLocalMatch,
    NearestLocalMatch,
    RemotelyRetrievedExactMatch,
    RemotelyRetrievedContext,
    LocalBeginningReached,
    LocalEndReached,
    RemoteExhausted,
    Unsupported,
    UnsafeTarget,
    NotFound,
    Cancelled,
    Stale,
    Failed
}

/// <summary>
/// A reusable, strongly scoped navigation request. The durable conversation
/// address is part of the request so a msgid can never be applied to another
/// network or another query with the same spelling.
/// </summary>
public sealed record HistoryNavigationRequest
{
    public required HistoryConversationAddress Conversation { get; init; }

    public string? ServerMessageId { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Exact canonical location supplied by a local search result. It is used
    /// before timestamp fallback and is validated against JSONL before context
    /// is projected.
    /// </summary>
    public HistoryAnchorLocation? CanonicalAnchor { get; init; }

    public HistoryAnchorDirection TimestampDirection { get; init; } = HistoryAnchorDirection.Around;

    public int BeforeCount { get; init; } = ConfigurationLimits.MaximumHistoryContextEntries / 2;

    public int AfterCount { get; init; } = ConfigurationLimits.MaximumHistoryContextEntries / 2;

    public bool IsMessageRequest => !string.IsNullOrWhiteSpace(ServerMessageId);

    public bool IsTimestampRequest => Timestamp is not null;

    public bool IsCanonicalRequest => CanonicalAnchor is not null;

    public static HistoryNavigationRequest ForMessage(HistoryConversationAddress conversation, string serverMessageId) => new()
    {
        Conversation = conversation,
        ServerMessageId = serverMessageId
    };

    public static HistoryNavigationRequest ForTimestamp(
        HistoryConversationAddress conversation,
        DateTimeOffset timestamp,
        HistoryAnchorDirection direction = HistoryAnchorDirection.Around) => new()
        {
            Conversation = conversation,
            Timestamp = timestamp.ToUniversalTime(),
            TimestampDirection = direction
        };

    public static HistoryNavigationRequest ForSearchResult(
        HistoryConversationAddress conversation,
        ConversationLogSearchResult result) => new()
        {
            Conversation = conversation,
            ServerMessageId = result.ServerMessageId,
            Timestamp = result.ServerMessageId is null ? result.Timestamp : null,
            CanonicalAnchor = result.CanonicalAnchor
        };
}

public sealed record HistoryNavigationResult
{
    public required HistoryNavigationRequest Request { get; init; }

    public required HistoryNavigationOutcome Outcome { get; init; }

    public required string Message { get; init; }

    public HistoryAnchorResult Anchor { get; init; } = HistoryAnchorResult.Missing;

    public IReadOnlyList<ConversationLogRecord> Records { get; init; } = Array.Empty<ConversationLogRecord>();

    public bool Succeeded => Outcome is HistoryNavigationOutcome.ExactLocalMatch
        or HistoryNavigationOutcome.NearestLocalMatch
        or HistoryNavigationOutcome.RemotelyRetrievedExactMatch
        or HistoryNavigationOutcome.RemotelyRetrievedContext
        or HistoryNavigationOutcome.LocalBeginningReached
        or HistoryNavigationOutcome.LocalEndReached;

    public bool IsExact => Outcome is HistoryNavigationOutcome.ExactLocalMatch
        or HistoryNavigationOutcome.RemotelyRetrievedExactMatch;

    public static HistoryNavigationResult Create(
        HistoryNavigationRequest request,
        HistoryNavigationOutcome outcome,
        string message,
        HistoryAnchorResult? anchor = null,
        IReadOnlyList<ConversationLogRecord>? records = null) => new()
        {
            Request = request,
            Outcome = outcome,
            Message = message,
            Anchor = anchor ?? HistoryAnchorResult.Missing,
            Records = records ?? Array.Empty<ConversationLogRecord>()
        };
}

internal static class HistoryNavigationValidation
{
    public static bool IsValid(HistoryNavigationRequest request) =>
        request is not null
        && request.Conversation.NetworkId != Guid.Empty
        && request.Conversation.ScopeId != Guid.Empty
        && !string.IsNullOrWhiteSpace(request.Conversation.ConversationName)
        && (request.IsMessageRequest ^ request.IsTimestampRequest)
        && (!request.IsMessageRequest
            || HistorySearchInput.TryNormalizeServerMessageId(request.ServerMessageId, out _, out _))
        && (!request.IsCanonicalRequest
            || request.CanonicalAnchor is
            {
                SourceOffset: >= 0,
                SourceLength: > 0,
                Record: not null
            });

    public static bool MatchesView(HistoryConversationAddress request, HistoryConversationAddress current) =>
        request.NetworkId == current.NetworkId
        && request.ScopeId == current.ScopeId
        && request.ConversationKind == current.ConversationKind
        && string.Equals(request.EffectiveConversationKey, current.EffectiveConversationKey, StringComparison.Ordinal);

    public static HistoryConversationAddress ForView(NetworkWorkspace network, WorkspaceView view) => view switch
    {
        QueryView query => new HistoryConversationAddress
        {
            NetworkId = network.Id,
            ScopeId = network.ProfileId ?? network.Id,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = query.Nickname,
            ConversationKey = query.HistoryConversationKey
        },
        ChannelView channel => new HistoryConversationAddress
        {
            NetworkId = network.Id,
            ScopeId = network.ProfileId ?? network.Id,
            ConversationKind = LogConversationKind.Channel,
            ConversationName = channel.Channel,
            ConversationKey = ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, channel.Channel)
        },
        _ => throw new ArgumentException("History navigation is available only for channels and queries.", nameof(view))
    };
}
