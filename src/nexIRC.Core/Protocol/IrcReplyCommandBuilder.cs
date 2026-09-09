namespace nexIRC.Core.Protocol;

/// <summary>Builds the standards-compliant IRCv3 reply wire form.</summary>
public static class IrcReplyCommandBuilder
{
    public static IrcOutboundMessage Build(
        IrcCommandBuilder builder,
        string target,
        string text,
        string parentMessageId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var parent = IrcReplyReference.Create(parentMessageId);
        return builder.BuildWithTags(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["+reply"] = parent.MessageId
            },
            "PRIVMSG",
            [target],
            text);
    }
}
