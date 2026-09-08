using System.Globalization;

namespace nexIRC.Application;

/// <summary>
/// Shared bounds and parsing rules for history search and navigation input.
/// The desktop surface uses these rules directly so invalid input cannot be
/// interpreted differently by a machine's culture or timezone.
/// </summary>
public static class HistorySearchInput
{
    private static readonly string[] UtcDateTimeFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd HH:mm:ss'Z'",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF'Z'"
    ];

    public static bool TryNormalizeServerMessageId(string? value, out string normalized, out string error)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            error = "Enter a server message ID.";
            return false;
        }

        if (normalized.Length > ConfigurationLimits.MaximumHistoryMessageIdLength)
        {
            error = $"The server message ID is limited to {ConfigurationLimits.MaximumHistoryMessageIdLength} characters.";
            normalized = string.Empty;
            return false;
        }

        if (normalized.Any(char.IsWhiteSpace) || normalized.Any(char.IsControl))
        {
            error = "The server message ID must be one opaque token without whitespace or control characters.";
            normalized = string.Empty;
            return false;
        }

        error = string.Empty;
        return true;
    }

    public static bool TryParseUtcDateTime(string? value, out DateTimeOffset timestamp, out string error)
    {
        timestamp = default;
        var input = value?.Trim() ?? string.Empty;
        if (input.Length == 0)
        {
            error = "Enter a UTC date and time in ISO form, for example 2026-09-07T12:30:00Z.";
            return false;
        }

        if (!DateTimeOffset.TryParseExact(
                input,
                UtcDateTimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            error = "Use UTC in the form yyyy-MM-ddTHH:mm:ssZ, optionally with fractional seconds.";
            return false;
        }

        timestamp = timestamp.ToUniversalTime();
        error = string.Empty;
        return true;
    }
}

public static class HistorySearchRequestValidator
{
    public static bool TryNormalize(
        ConversationLogQuery request,
        out ConversationLogQuery normalized,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(request);
        normalized = request;
        error = string.Empty;

        if (!Enum.IsDefined(request.Scope))
        {
            error = "The history search scope is not supported.";
            return false;
        }

        if (request.NetworkId == Guid.Empty || request.HistoryScopeId == Guid.Empty || request.ProfileId == Guid.Empty)
        {
            error = "History search identities must be non-empty network and scope IDs.";
            return false;
        }

        var text = request.Text?.Trim() ?? string.Empty;
        if (!IsBounded(text, ConfigurationLimits.MaximumSearchQueryLength))
        {
            error = $"The search text is limited to {ConfigurationLimits.MaximumSearchQueryLength} characters.";
            return false;
        }

        var sender = NormalizeOptional(request.Sender, ConfigurationLimits.MaximumSearchSenderLength, "sender", out error);
        if (error.Length > 0)
        {
            return false;
        }

        var conversationName = NormalizeOptional(request.ConversationName, ConfigurationLimits.MaximumSearchConversationLength, "conversation", out error);
        if (error.Length > 0)
        {
            return false;
        }

        var conversationKey = NormalizeOptional(request.ConversationKey, ConfigurationLimits.MaximumSearchConversationKeyLength, "conversation key", out error);
        if (error.Length > 0)
        {
            return false;
        }

        if (request.Scope == ConversationLogSearchScope.CurrentConversation
            && (request.NetworkId is null
                || request.HistoryScopeId is null
                || request.ConversationKind is null
                || string.IsNullOrWhiteSpace(conversationName)))
        {
            error = "Current-conversation search requires a network, conversation kind, and conversation name.";
            return false;
        }

        if (request.Scope == ConversationLogSearchScope.CurrentNetwork && request.NetworkId is null)
        {
            error = "Current-network search requires a network.";
            return false;
        }

        if (request.ConversationKind is { } kind && !Enum.IsDefined(kind))
        {
            error = "The conversation kind is not supported.";
            return false;
        }

        if (request.MessageKind is { } messageKind && !Enum.IsDefined(messageKind))
        {
            error = "The event kind is not supported.";
            return false;
        }

        var from = request.From?.ToUniversalTime();
        var to = request.To?.ToUniversalTime();
        if (from is not null && to is not null && from > to)
        {
            error = "The from date must not be later than the to date.";
            return false;
        }

        normalized = request with
        {
            Text = text,
            Sender = sender,
            ConversationName = conversationName,
            ConversationKey = conversationKey,
            From = from,
            To = to,
            MaximumResults = Math.Clamp(request.MaximumResults, 1, ConfigurationLimits.MaximumSearchResults),
            Skip = Math.Clamp(request.Skip, 0, ConfigurationLimits.MaximumSearchResults)
        };
        return true;
    }

    private static string? NormalizeOptional(string? value, int maximum, string label, out string error)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            error = string.Empty;
            return null;
        }

        if (!IsBounded(normalized, maximum))
        {
            error = $"The {label} filter is limited to {maximum} characters.";
            return null;
        }

        if (normalized.Any(char.IsControl))
        {
            error = $"The {label} filter cannot contain control characters.";
            return null;
        }

        error = string.Empty;
        return normalized;
    }

    private static bool IsBounded(string value, int maximum) =>
        value.Length <= maximum && !value.Any(char.IsControl);
}
