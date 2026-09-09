using System.Text;

namespace nexIRC.Core.Protocol;

/// <summary>The two exact IRCv3 draft reaction tag operations supported by nexIRC.</summary>
public enum IrcReactionOperation
{
    React,
    Unreact
}

/// <summary>
/// Typed protocol metadata for an IRCv3 draft reaction.  It is deliberately
/// independent of conversation routing; the application adds network and
/// durable-conversation scope before projecting it.
/// </summary>
public sealed record IrcReaction
{
    public const string ReactTag = "+draft/react";
    public const string UnreactTag = "+draft/unreact";
    public const int MaximumCanonicalParentLength = IrcReplyReference.MaximumMessageIdLength;

    private IrcReaction(
        IrcReactionOperation operation,
        string value,
        IrcReplyReference parent,
        IrcPrefix? sender,
        string? account,
        string? eventMessageId,
        DateTimeOffset? serverTimestamp)
    {
        Operation = operation;
        Value = value;
        Parent = parent;
        Sender = sender;
        Account = account;
        EventMessageId = eventMessageId;
        ServerTimestamp = serverTimestamp;
    }

    public IrcReactionOperation Operation { get; }

    public string Value { get; }

    public IrcReplyReference Parent { get; }

    public string ParentMessageId => Parent.MessageId;

    public IrcPrefix? Sender { get; }

    /// <summary>Authenticated account evidence from the exact message, if present.</summary>
    public string? Account { get; }

    /// <summary>The event's canonical msgid, when the server supplied one.</summary>
    public string? EventMessageId { get; }

    public DateTimeOffset? ServerTimestamp { get; }

    public static bool TryParse(IrcMessage message, out IrcReaction? reaction, out string? error)
    {
        ArgumentNullException.ThrowIfNull(message);
        reaction = null;
        error = null;

        // A reaction without a message-tag section is not an IRCv3 reaction.
        // The containing IRC message remains valid and is handled by callers as
        // an ordinary, unrecognized TAGMSG.
        if (message.Tags.Count == 0)
        {
            error = "A reaction requires message-tags.";
            return false;
        }

        var hasReact = message.TagValues.TryGetValue(ReactTag, out var reactValue);
        var hasUnreact = message.TagValues.TryGetValue(UnreactTag, out var unreactValue);
        if (!hasReact && !hasUnreact)
        {
            error = "The message did not contain a draft reaction tag.";
            return false;
        }

        if (hasReact && hasUnreact)
        {
            error = "A message cannot contain both +draft/react and +draft/unreact.";
            return false;
        }

        var value = hasReact ? reactValue : unreactValue;
        if (string.IsNullOrEmpty(value))
        {
            error = "A reaction tag must contain a non-empty value.";
            return false;
        }

        if (value.Contains('\0'))
        {
            error = "A reaction value cannot contain NUL.";
            return false;
        }

        if (message.ReplyReference is not { } parent)
        {
            error = message.HasReplyTag
                ? message.ReplyValidationError ?? "The +reply parent msgid is invalid."
                : "A reaction requires the canonical +reply parent msgid.";
            return false;
        }

        reaction = new IrcReaction(
            hasReact ? IrcReactionOperation.React : IrcReactionOperation.Unreact,
            value,
            parent,
            message.Prefix,
            message.TagValues.TryGetValue("account", out var account)
                && !string.IsNullOrWhiteSpace(account)
                && !string.Equals(account, "*", StringComparison.Ordinal)
                ? account
                : null,
            message.ServerMessageId,
            message.ServerTimestamp);
        return true;
    }

    public static bool TryParse(IrcMessage message, out IrcReaction? reaction) =>
        TryParse(message, out reaction, out _);
}

/// <summary>Builds the exact draft/react and draft/unreact TAGMSG wire forms.</summary>
public static class IrcReactionCommandBuilder
{
    // This is a product-side limit, intentionally well below the IRC tag-data
    // ceiling. It is a byte limit and never truncates Unicode input.
    public const int MaximumOutboundReactionUtf8Bytes = 128;

    public static IrcOutboundMessage Build(
        IrcCommandBuilder builder,
        string target,
        string value,
        IrcReactionOperation operation,
        string parentMessageId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ValidateReactionValue(value);
        var parent = IrcReplyReference.Create(parentMessageId);
        var tag = operation switch
        {
            IrcReactionOperation.React => IrcReaction.ReactTag,
            IrcReactionOperation.Unreact => IrcReaction.UnreactTag,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

        return builder.BuildWithTags(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["+reply"] = parent.MessageId,
                [tag] = value
            },
            "TAGMSG",
            [target],
            orderedTagKeys: ["+reply", tag]);
    }

    public static void ValidateReactionValue(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Any(static character => character is '\r' or '\n' or '\0' || char.IsControl(character)))
        {
            throw new ArgumentException("A reaction value cannot contain control characters.", nameof(value));
        }

        if (Encoding.UTF8.GetByteCount(value) > MaximumOutboundReactionUtf8Bytes)
        {
            throw new ArgumentException($"A reaction value cannot exceed {MaximumOutboundReactionUtf8Bytes} UTF-8 bytes.", nameof(value));
        }
    }
}
