namespace nexIRC.Core.Protocol;

/// <summary>
/// Typed command construction for participant workflows.  The application
/// layer supplies already-selected identities; this class owns IRC framing,
/// validation, and the small amount of protocol syntax shared by the UI.
/// </summary>
public static class IrcParticipantCommandBuilder
{
    public static IrcOutboundMessage BuildWhois(IrcCommandBuilder builder, string nickname) =>
        builder.Build("WHOIS", [ValidateNickname(nickname)]);

    public static IrcOutboundMessage BuildNotice(IrcCommandBuilder builder, string target, string text) =>
        builder.Build("NOTICE", [ValidateNickname(target)], ValidateText(text, nameof(text)));

    public static IrcOutboundMessage BuildAction(IrcCommandBuilder builder, string target, string text) =>
        builder.Build("PRIVMSG", [ValidateNickname(target)], $"\u0001ACTION {ValidateText(text, nameof(text))}\u0001");

    public static IrcOutboundMessage BuildCtcp(IrcCommandBuilder builder, string target, string command, string? arguments = null)
    {
        target = ValidateNickname(target);
        command = ValidateCtcpCommand(command);
        if (arguments is not null && arguments.Any(character => character is '\u0000' or '\u0001' or '\r' or '\n'))
        {
            throw new ArgumentException("CTCP arguments cannot contain control characters.", nameof(arguments));
        }

        var payload = string.IsNullOrWhiteSpace(arguments) ? command : $"{command} {arguments.Trim()}";
        return builder.Build("PRIVMSG", [target], $"\u0001{payload}\u0001");
    }

    public static IrcOutboundMessage BuildMemberMode(
        IrcCommandBuilder builder,
        string channel,
        char mode,
        bool adding,
        string nickname) =>
        builder.Build("MODE", [ValidateChannel(channel), $"{(adding ? '+' : '-')}{ValidateMode(mode)}", ValidateNickname(nickname)]);

    public static IrcOutboundMessage BuildKick(IrcCommandBuilder builder, string channel, string nickname, string? reason = null) =>
        builder.Build(
            "KICK",
            [ValidateChannel(channel), ValidateNickname(nickname)],
            string.IsNullOrWhiteSpace(reason) ? null : ValidateText(reason, nameof(reason)));

    public static IrcOutboundMessage BuildBan(IrcCommandBuilder builder, string channel, string banMask, char banMode = 'b') =>
        builder.Build("MODE", [ValidateChannel(channel), $"+{ValidateMode(banMode)}", ValidateMask(banMask)]);

    public static IrcOutboundMessage BuildUnban(IrcCommandBuilder builder, string channel, string banMask, char banMode = 'b') =>
        builder.Build("MODE", [ValidateChannel(channel), $"-{ValidateMode(banMode)}", ValidateMask(banMask)]);

    public static IrcOutboundMessage BuildInvite(IrcCommandBuilder builder, string nickname, string channel) =>
        builder.Build("INVITE", [ValidateNickname(nickname), ValidateChannel(channel)]);

    private static string ValidateNickname(string value)
    {
        var normalized = ValidateToken(value, nameof(value));
        if (normalized.Any(character => character is ',' or ':'))
        {
            throw new ArgumentException("A nickname cannot contain ',' or ':'.", nameof(value));
        }

        return normalized;
    }

    private static string ValidateChannel(string value)
    {
        var normalized = ValidateToken(value, nameof(value));
        if (normalized.Any(character => character is ','))
        {
            throw new ArgumentException("A channel cannot contain ','.", nameof(value));
        }

        return normalized;
    }

    private static string ValidateMask(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A ban mask is required and cannot contain whitespace.", nameof(value));
        }

        return value;
    }

    private static string ValidateText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character is '\u0000' or '\u0001' or '\r' or '\n'))
        {
            throw new ArgumentException("Text is required.", parameterName);
        }

        return value;
    }

    private static string ValidateToken(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || value[0] == ':')
        {
            throw new ArgumentException("An IRC participant identity cannot be empty or contain whitespace.", parameterName);
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("IRC participant identities cannot contain CR or LF.", parameterName);
        }

        return value;
    }

    private static char ValidateMode(char mode)
    {
        if (mode is < 'A' or > 'z' || mode is >= '[' and <= '`')
        {
            throw new ArgumentException("An IRC mode must be an ASCII letter.", nameof(mode));
        }

        return mode;
    }

    private static string ValidateCtcpCommand(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(character => character is '\u0000' or '\u0001' or '\r' or '\n'))
        {
            throw new ArgumentException("A CTCP command must be a single safe token.", nameof(value));
        }

        return value.ToUpperInvariant() switch
        {
            "PING" or "VERSION" or "TIME" => value.ToUpperInvariant(),
            _ => throw new ArgumentException("Only CTCP PING, VERSION, and TIME are supported.", nameof(value))
        };
    }
}
