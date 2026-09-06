using nexIRC.Core.Protocol;

namespace nexIRC.Core.Session;

public enum IrcNumericCategory
{
    Information,
    Success,
    Error
}

public sealed record IrcNumericDefinition(int Numeric, string Name, IrcNumericCategory Category);

public sealed record IrcNumericInterpretation(
    IrcNumericDefinition Definition,
    string FriendlyExplanation,
    string ProtocolText,
    string? TargetChannel,
    string? TargetNickname,
    string? Command)
{
    public int Numeric => Definition.Numeric;

    public string Name => Definition.Name;

    public bool IsError => Definition.Category == IrcNumericCategory.Error;

    public bool IsSuccess => Definition.Category == IrcNumericCategory.Success;
}

/// <summary>
/// Bounded, standard-oriented interpretation for numerics used by the
/// participant and moderation workflows. Network-specific numerics retain
/// deliberately conservative wording.
/// </summary>
public static class IrcNumericCatalog
{
    private static readonly IReadOnlyDictionary<int, IrcNumericDefinition> Definitions =
        new Dictionary<int, IrcNumericDefinition>
        {
            [324] = new(324, "RPL_CHANNELMODEIS", IrcNumericCategory.Information),
            [331] = new(331, "RPL_NOTOPIC", IrcNumericCategory.Information),
            [332] = new(332, "RPL_TOPIC", IrcNumericCategory.Information),
            [333] = new(333, "RPL_TOPICWHOTIME", IrcNumericCategory.Information),
            [341] = new(341, "RPL_INVITING", IrcNumericCategory.Success),
            [401] = new(401, "ERR_NOSUCHNICK", IrcNumericCategory.Error),
            [403] = new(403, "ERR_NOSUCHCHANNEL", IrcNumericCategory.Error),
            [404] = new(404, "ERR_CANNOTSENDTOCHAN", IrcNumericCategory.Error),
            [405] = new(405, "ERR_TOOMANYCHANNELS", IrcNumericCategory.Error),
            [407] = new(407, "ERR_TOOMANYTARGETS", IrcNumericCategory.Error),
            [411] = new(411, "ERR_NORECIPIENT", IrcNumericCategory.Error),
            [412] = new(412, "ERR_NOTEXTTOSEND", IrcNumericCategory.Error),
            [421] = new(421, "ERR_UNKNOWNCOMMAND", IrcNumericCategory.Error),
            [442] = new(442, "ERR_NOTONCHANNEL", IrcNumericCategory.Error),
            [443] = new(443, "ERR_USERONCHANNEL", IrcNumericCategory.Error),
            [461] = new(461, "ERR_NEEDMOREPARAMS", IrcNumericCategory.Error),
            [471] = new(471, "ERR_CHANNELISFULL", IrcNumericCategory.Error),
            [472] = new(472, "ERR_UNKNOWNMODE", IrcNumericCategory.Error),
            [473] = new(473, "ERR_INVITEONLYCHAN", IrcNumericCategory.Error),
            [474] = new(474, "ERR_BANNEDFROMCHAN", IrcNumericCategory.Error),
            [475] = new(475, "ERR_BADCHANNELKEY", IrcNumericCategory.Error),
            [476] = new(476, "ERR_BADCHANMASK", IrcNumericCategory.Error),
            [477] = new(477, "ERR_NOCHANMODES", IrcNumericCategory.Error),
            [481] = new(481, "ERR_NOPRIVILEGES", IrcNumericCategory.Error),
            [482] = new(482, "ERR_CHANOPRIVSNEEDED", IrcNumericCategory.Error),
            [485] = new(485, "ERR_NETWORKPRIVILEGES", IrcNumericCategory.Error)
        };

    public static IReadOnlyCollection<IrcNumericDefinition> KnownDefinitions => Definitions.Values.ToArray();

    public static bool IsRecognized(int numeric) => Definitions.ContainsKey(numeric);

    public static bool TryInterpret(IrcMessage message, out IrcNumericInterpretation interpretation)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.NumericCommand is not int numeric || !Definitions.TryGetValue(numeric, out var definition))
        {
            interpretation = null!;
            return false;
        }

        var channel = numeric switch
        {
            324 or 331 or 332 or 333 => Parameter(message, 1),
            341 => Parameter(message, 2),
            403 or 404 or 405 or 442 or 471 or 473 or 474 or 475 or 476 or 477 or 482 => Parameter(message, 1),
            443 => Parameter(message, 2),
            _ => null
        };
        var nickname = numeric switch
        {
            341 or 401 or 443 => Parameter(message, 1),
            _ => null
        };
        var command = numeric is 421 or 461 ? Parameter(message, 1) : null;
        var protocolText = MessageText(message);
        var subject = nickname ?? channel ?? command;
        var friendly = numeric switch
        {
            341 => $"Server acknowledged inviting {nickname ?? "the user"} to {channel ?? "the channel"}.",
            401 => subject is null ? "No such nickname." : $"No such nickname: {subject}.",
            403 => subject is null ? "No such channel." : $"No such channel: {subject}.",
            404 => subject is null ? "You cannot send to that channel." : $"You cannot send to {subject}.",
            405 => "You have joined too many channels.",
            407 => "The server rejected the request because it has too many targets.",
            411 => "The command did not include a recipient.",
            412 => "The command did not include text to send.",
            421 => command is null ? "The server does not recognize that command." : $"The server does not recognize the command {command}.",
            442 => subject is null ? "You are not on that channel." : $"You are not on {subject}.",
            443 => nickname is null || channel is null ? "That user is already on the channel." : $"{nickname} is already on {channel}.",
            461 => command is null ? "The server needs more parameters for that command." : $"The server needs more parameters for {command}.",
            471 => subject is null ? "The channel is full." : $"{subject} is full.",
            472 => Parameter(message, 1) is { } unknownMode
                ? $"The server does not recognize that channel mode: {unknownMode}."
                : "The server does not recognize that channel mode.",
            473 => subject is null ? "The channel is invite-only." : $"{subject} is invite-only.",
            474 => subject is null ? "You are banned from that channel." : $"You are banned from {subject}.",
            475 => subject is null ? "The channel requires a different key." : $"The key for {subject} was rejected.",
            476 => subject is null ? "The channel mask is invalid." : $"The channel mask {subject} is invalid.",
            477 => subject is null ? "The server reported a network-specific channel-mode restriction." : $"The server reported a network-specific channel-mode restriction for {subject}.",
            481 => "You do not have the server privileges required for that command.",
            482 => subject is null ? "You do not have permission to change channel modes." : $"You do not have permission to change modes in {subject}.",
            485 => "The server reported a network-specific privilege restriction.",
            _ => protocolText
        };

        interpretation = new IrcNumericInterpretation(definition, friendly, protocolText, channel, nickname, command);
        return true;
    }

    private static string? Parameter(IrcMessage message, int index) => index < message.Parameters.Count ? message.Parameters[index] : null;

    private static string MessageText(IrcMessage message) => message.HasTrailingParameter
        ? message.TrailingParameter ?? string.Empty
        : string.Join(' ', message.Parameters);
}
