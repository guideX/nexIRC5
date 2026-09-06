namespace nexIRC.Core.Protocol;

/// <summary>
/// Typed command construction for channel-wide operations. The mode grammar is
/// negotiated by the session; this builder only owns safe IRC syntax and line
/// framing.
/// </summary>
public static class IrcChannelCommandBuilder
{
    public static IrcOutboundMessage BuildTopic(IrcCommandBuilder builder, string channel, string topic) =>
        builder.Build("TOPIC", [ValidateChannel(channel)], ValidateTopic(topic));

    public static IrcOutboundMessage BuildTopicQuery(IrcCommandBuilder builder, string channel) =>
        builder.Build("TOPIC", [ValidateChannel(channel)]);

    public static IrcOutboundMessage BuildFlagMode(IrcCommandBuilder builder, string channel, char mode, bool adding) =>
        builder.Build("MODE", [ValidateChannel(channel), FormatMode(mode, adding)]);

    public static IrcOutboundMessage BuildParameterizedMode(IrcCommandBuilder builder, string channel, char mode, bool adding, string parameter) =>
        builder.Build("MODE", [ValidateChannel(channel), FormatMode(mode, adding), ValidateParameter(parameter)]);

    private static string ValidateChannel(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || value[0] == ':')
        {
            throw new ArgumentException("A channel name is required and cannot contain whitespace or control characters.", nameof(value));
        }

        return value;
    }

    private static string ValidateTopic(string value)
    {
        if (value.Any(character => character is '\r' or '\n' or '\0' or '\u0001'))
        {
            throw new ArgumentException("A topic cannot contain line breaks or control characters.", nameof(value));
        }

        return value;
    }

    private static string ValidateParameter(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A mode parameter is required and cannot contain whitespace or control characters.", nameof(value));
        }

        return value;
    }

    private static string FormatMode(char mode, bool adding)
    {
        if (!char.IsLetter(mode) || mode > 127)
        {
            throw new ArgumentException("A channel mode must be an ASCII letter.", nameof(mode));
        }

        return $"{(adding ? '+' : '-')}{mode}";
    }
}
