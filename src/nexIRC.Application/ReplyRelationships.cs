using nexIRC.Core.Protocol;

namespace nexIRC.Application;

public enum ReplyResolutionState
{
    Unresolved,
    ResolvedLocally,
    RecoverableRemotely,
    Unavailable,
    AmbiguousOrInvalid
}

/// <summary>
/// A durable, network- and conversation-scoped relationship from a child
/// message to its parent. The parent id remains opaque and case-sensitive.
/// </summary>
public sealed record ReplyRelationship
{
    public required Guid NetworkId { get; init; }
    public required string ConversationKey { get; init; }
    public string? ChildMessageId { get; init; }
    public required string ParentMessageId { get; init; }
    public ConversationEntryProvenance Provenance { get; init; } = ConversationEntryProvenance.Live;
    public ReplyResolutionState Resolution { get; init; } = ReplyResolutionState.Unresolved;
    public string? ParentSender { get; init; }
    public string? ParentPreview { get; init; }
    public string? Diagnostic { get; init; }

    public static bool TryCreate(
        Guid networkId,
        string conversationKey,
        string? childMessageId,
        string? parentMessageId,
        ConversationEntryProvenance provenance,
        out ReplyRelationship? relationship)
    {
        relationship = null;
        if (networkId == Guid.Empty
            || string.IsNullOrWhiteSpace(conversationKey)
            || !IrcReplyReference.TryParse(parentMessageId, out var parent))
        {
            return false;
        }

        relationship = new ReplyRelationship
        {
            NetworkId = networkId,
            ConversationKey = conversationKey,
            ChildMessageId = childMessageId,
            ParentMessageId = parent!.MessageId,
            Provenance = provenance
        };
        return true;
    }

    public static ReplyRelationship FromRecord(ConversationLogRecord record) =>
        new()
        {
            NetworkId = record.NetworkId,
            ConversationKey = ConversationLoggingService.EffectiveConversationKey(record),
            ChildMessageId = record.ServerMessageId,
            ParentMessageId = record.ReplyParentMessageId!,
            Provenance = record.Provenance
        };
}

/// <summary>State captured by the reply composer, including its reconnect boundary.</summary>
public sealed record ReplyComposerState
{
    public required Guid NetworkId { get; init; }
    public required Guid ViewId { get; init; }
    public required string ConversationKey { get; init; }
    public required string ParentMessageId { get; init; }
    public string? ParentSender { get; init; }
    public string ParentPreview { get; init; } = string.Empty;
    public int ConnectionGeneration { get; init; }
    public required string Target { get; init; }
    public string BannerText => string.IsNullOrWhiteSpace(ParentSender)
        ? $"Replying to: {ParentPreview}"
        : $"Replying to {ParentSender}: {ParentPreview}";
    public string AccessibleText => $"{BannerText}. Press Escape to cancel reply.";
}

public static class ReplyText
{
    public const int MaximumPreviewLength = 120;

    public static string BoundedPreview(string? text, int maximumLength = MaximumPreviewLength)
    {
        if (string.IsNullOrEmpty(text) || maximumLength <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maximumLength)
        {
            return text;
        }

        var length = Math.Max(0, maximumLength - 1);
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length] + "…";
    }
}
