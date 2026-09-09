using System.Collections.ObjectModel;

namespace nexIRC.Core.Protocol;

public enum IrcPrefixKind
{
    Unknown,
    ServerOrNick,
    User
}

public sealed class IrcPrefix
{
    private IrcPrefix(string raw, string name, string? user, string? host, IrcPrefixKind kind)
    {
        Raw = raw;
        Name = name;
        User = user;
        Host = host;
        Kind = kind;
    }

    public string Raw { get; }

    public string Name { get; }

    public string? User { get; }

    public string? Host { get; }

    public IrcPrefixKind Kind { get; }

    public bool HasUser => User is not null;

    public bool HasHost => Host is not null;

    public static IrcPrefix Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        var bang = raw.IndexOf('!');
        var at = raw.IndexOf('@', bang >= 0 ? bang + 1 : 0);
        if (bang >= 0)
        {
            var name = raw[..bang];
            if (at > bang)
            {
                return new IrcPrefix(raw, name, raw[(bang + 1)..at], raw[(at + 1)..], IrcPrefixKind.User);
            }

            return new IrcPrefix(raw, name, raw[(bang + 1)..], null, IrcPrefixKind.User);
        }

        if (at > 0)
        {
            return new IrcPrefix(raw, raw[..at], null, raw[(at + 1)..], IrcPrefixKind.User);
        }

        return new IrcPrefix(raw, raw, null, null, raw.Length == 0 ? IrcPrefixKind.Unknown : IrcPrefixKind.ServerOrNick);
    }
}

public sealed class IrcMessageTag
{
    internal IrcMessageTag(string raw, string key, string? rawValue, string? value)
    {
        Raw = raw;
        Key = key;
        RawValue = rawValue;
        Value = value;
    }

    public string Raw { get; }

    public string Key { get; }

    public string? RawValue { get; }

    public string? Value { get; }

    public bool HasValue => RawValue is not null;
}

/// <summary>
/// The opaque server message id carried by the ratified IRCv3 <c>+reply</c>
/// client tag.  It is deliberately not normalized: ids are case-sensitive,
/// network-scoped, and have no ordering semantics.
/// </summary>
public sealed record IrcReplyReference
{
    public const int MaximumMessageIdLength = 256;

    private IrcReplyReference(string messageId)
    {
        MessageId = messageId;
    }

    public string MessageId { get; }

    public static IrcReplyReference Create(string messageId)
    {
        if (!TryParse(messageId, out var reference, out var error))
        {
            throw new ArgumentException(error ?? "The reply parent msgid is invalid.", nameof(messageId));
        }

        return reference!;
    }

    public static bool TryParse(string? messageId, out IrcReplyReference? reference, out string? error)
    {
        if (string.IsNullOrEmpty(messageId))
        {
            reference = null;
            error = "The +reply tag must contain a non-empty parent msgid.";
            return false;
        }

        if (messageId.Length > MaximumMessageIdLength)
        {
            reference = null;
            error = $"The +reply parent msgid exceeds {MaximumMessageIdLength} characters.";
            return false;
        }

        if (messageId.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character == '\0'))
        {
            reference = null;
            error = "The +reply parent msgid contains whitespace or a control character.";
            return false;
        }

        reference = new IrcReplyReference(messageId);
        error = null;
        return true;
    }

    public static bool TryParse(string? messageId, out IrcReplyReference? reference) =>
        TryParse(messageId, out reference, out _);
}

public sealed class IrcMessage
{
    internal IrcMessage(
        string rawLine,
        IReadOnlyList<IrcMessageTag> tags,
        IrcPrefix? prefix,
        string rawCommand,
        IReadOnlyList<string> middleParameters,
        bool hasTrailingParameter,
        string? trailingParameter)
    {
        RawLine = rawLine;
        Tags = tags;
        TagValues = new ReadOnlyDictionary<string, string?>(tags
            .GroupBy(tag => tag.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal));
        Prefix = prefix;
        RawCommand = rawCommand;
        Command = rawCommand.ToUpperInvariant();
        NumericCommand = IsNumericCommand(Command) ? int.Parse(Command, System.Globalization.CultureInfo.InvariantCulture) : null;
        MiddleParameters = middleParameters;
        HasTrailingParameter = hasTrailingParameter;
        TrailingParameter = trailingParameter;
        Parameters = hasTrailingParameter
            ? new ReadOnlyCollection<string>(middleParameters.Concat(new[] { trailingParameter! }).ToList())
            : middleParameters;
        ServerTimestamp = TagValues.TryGetValue("time", out var time)
            && IrcServerTime.TryParse(time, out var parsedTimestamp)
                ? parsedTimestamp
                : null;
        ServerMessageId = IrcMessageIdentity.FindServerMessageId(TagValues);
        HasReplyTag = TagValues.ContainsKey("+reply");
        if (TagValues.TryGetValue("+reply", out var replyValue))
        {
            if (IrcReplyReference.TryParse(replyValue, out var replyReference, out var replyError))
            {
                ReplyReference = replyReference;
            }
            else
            {
                ReplyValidationError = replyError;
            }
        }
        BatchId = TagValues.TryGetValue("batch", out var batch)
            && !string.IsNullOrWhiteSpace(batch)
            && batch.Length <= 65
                ? batch
                : null;
    }

    public string RawLine { get; }

    public IReadOnlyList<IrcMessageTag> Tags { get; }

    public IReadOnlyDictionary<string, string?> TagValues { get; }

    public IrcPrefix? Prefix { get; }

    public string RawCommand { get; }

    public string Command { get; }

    public int? NumericCommand { get; }

    public IReadOnlyList<string> MiddleParameters { get; }

    public bool HasTrailingParameter { get; }

    public string? TrailingParameter { get; }

    public IReadOnlyList<string> Parameters { get; }

    /// <summary>
    /// The authoritative timestamp supplied by the server-time message tag,
    /// when the tag is present and has the strict IRCv3 timestamp form.
    /// </summary>
    public DateTimeOffset? ServerTimestamp { get; }

    /// <summary>
    /// The stable server supplied message identifier, when the server sent
    /// either the standardized <c>msgid</c> tag or the older
    /// <c>draft/msgid</c> spelling.  An absent value is meaningful: nexIRC
    /// must not manufacture an authoritative identity from message content.
    /// </summary>
    public string? ServerMessageId { get; }

    /// <summary>Whether the message carried the exact <c>+reply</c> tag.</summary>
    public bool HasReplyTag { get; }

    /// <summary>
    /// A validated opaque parent msgid. Invalid metadata does not invalidate
    /// the IRC message; callers can inspect <see cref="ReplyValidationError" />
    /// for a diagnostic instead.
    /// </summary>
    public IrcReplyReference? ReplyReference { get; }

    public string? ReplyValidationError { get; }

    public string? ReplyParentMessageId => ReplyReference?.MessageId;

    /// <summary>The IRCv3 batch association tag, when present.</summary>
    public string? BatchId { get; }

    /// <summary>
    /// Explicit CHATHISTORY pagination evidence.  The draft uses a valueless
    /// <c>draft/chathistory-end</c> message tag on the opening BATCH line.
    /// The unprefixed spelling is accepted defensively for servers which have
    /// adopted the final tag name early.
    /// </summary>
    public bool HasChathistoryEnd =>
        TagValues.ContainsKey("draft/chathistory-end")
        || TagValues.ContainsKey("chathistory-end");

    public bool IsNumeric => NumericCommand.HasValue;

    private static bool IsNumericCommand(string command) => command.Length == 3 && command.All(static c => c is >= '0' and <= '9');
}

public static class IrcMessageIdentity
{
    public static string? FindServerMessageId(IReadOnlyDictionary<string, string?> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (var key in new[] { "msgid", "draft/msgid" })
        {
            if (tags.TryGetValue(key, out var value)
                && !string.IsNullOrWhiteSpace(value)
                && value.Length <= 256)
            {
                return value;
            }
        }

        return null;
    }
}

public static class IrcServerTime
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz"
    ];

    public static bool TryParse(string? value, out DateTimeOffset timestamp)
    {
        if (value is not null
            && DateTimeOffset.TryParseExact(
                value,
                Formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out timestamp))
        {
            return true;
        }

        timestamp = default;
        return false;
    }
}

public sealed class IrcParseResult
{
    private IrcParseResult(IrcMessage? message, string? error)
    {
        Message = message;
        Error = error;
    }

    public bool Success => Message is not null;

    public IrcMessage? Message { get; }

    public string? Error { get; }

    public static IrcParseResult FromMessage(IrcMessage message) => new(message, null);

    public static IrcParseResult FromError(string error) => new(null, error);
}
