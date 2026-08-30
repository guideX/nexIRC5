using nexIRC.Core.Protocol;

namespace nexIRC.Headless;

internal sealed class TranscriptReplaySummary
{
    private readonly Dictionary<string, ReplayChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _availableCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _requestedCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _enabledCapabilities = new(StringComparer.OrdinalIgnoreCase);
    private string _nickname = "unknown";
    private string _registration = "NotStarted";
    private string _authentication = "Disabled";
    private int _epoch;

    public void Apply(IrcTranscriptEntry entry)
    {
        _epoch = Math.Max(_epoch, entry.ConnectionGeneration);
        var parsed = IrcMessageParser.Parse(entry.RawLine);
        if (!parsed.Success)
        {
            return;
        }

        Apply(entry.Direction, parsed.Message!);
    }

    public void ApplyInbound(IrcMessage message) => Apply(IrcTranscriptDirection.Inbound, message);

    public string Format()
    {
        var observed = _channels.Values.Where(channel => channel.IsObserved).Select(channel => channel.Name).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase);
        var channelDetails = _channels.Values
            .OrderBy(static channel => channel.Name, StringComparer.OrdinalIgnoreCase)
            .Select(channel => $"{channel.Name}(joined={channel.IsObserved},topic={channel.Topic ?? "<none>"},modes={channel.Modes},members={channel.Members.Count},status={channel.StatusSummary})");
        return string.Join(Environment.NewLine,
            $"Connection epoch: {_epoch}",
            $"Registration: {_registration}",
            $"Authentication: {_authentication}",
            $"Current nickname: {_nickname}",
            $"CAP available: {FormatSet(_availableCapabilities)}",
            $"CAP requested: {FormatSet(_requestedCapabilities)}",
            $"CAP enabled: {FormatSet(_enabledCapabilities)}",
            $"Desired channels: {FormatSet(_channels.Values.Where(static channel => channel.IsDesired).Select(static channel => channel.Name))}",
            $"Observed channels: {FormatSet(observed)}",
            $"Channel summaries: {string.Join("; ", channelDetails)}");
    }

    private void Apply(IrcTranscriptDirection direction, IrcMessage message)
    {
        if (direction == IrcTranscriptDirection.Outbound)
        {
            ApplyOutbound(message);
        }
        else
        {
            ApplyInboundMessage(message);
        }
    }

    private void ApplyOutbound(IrcMessage message)
    {
        switch (message.Command)
        {
            case "NICK" when message.Parameters.Count > 0:
                _nickname = message.Parameters[0];
                break;
            case "CAP" when message.Parameters.Count > 0 && message.Parameters[0].Equals("REQ", StringComparison.OrdinalIgnoreCase):
                foreach (var capability in CapabilityTokens(message))
                {
                    _requestedCapabilities.Add(capability.TrimStart('-'));
                }

                break;
            case "JOIN" when message.Parameters.Count > 0:
                GetChannel(message.Parameters[0]).IsDesired = true;
                break;
            case "AUTHENTICATE" when message.Parameters.Count > 0:
                _authentication = message.Parameters[0].Equals("PLAIN", StringComparison.OrdinalIgnoreCase)
                    ? "Negotiating"
                    : _authentication;
                break;
        }
    }

    private void ApplyInboundMessage(IrcMessage message)
    {
        if (message.Command == "CAP")
        {
            var subcommand = message.MiddleParameters.FirstOrDefault(value => value is "LS" or "ACK" or "NAK" or "NEW" or "DEL");
            var tokens = CapabilityTokens(message).ToArray();
            switch (subcommand)
            {
                case "LS":
                case "NEW":
                    foreach (var token in tokens)
                    {
                        _availableCapabilities.Add(token.Split('=', 2)[0]);
                    }

                    break;
                case "ACK":
                    foreach (var token in tokens)
                    {
                        _enabledCapabilities.Add(token.TrimStart('-').Split('=', 2)[0]);
                    }

                    break;
                case "NAK":
                    break;
                case "DEL":
                    foreach (var token in tokens)
                    {
                        var capability = token.TrimStart('-').Split('=', 2)[0];
                        _availableCapabilities.Remove(capability);
                        _enabledCapabilities.Remove(capability);
                    }

                    break;
            }

            return;
        }

        if (message.NumericCommand is int numeric)
        {
            switch (numeric)
            {
                case 1:
                    _registration = "Registered";
                    if (message.Parameters.Count > 0)
                    {
                        _nickname = message.Parameters[0];
                    }

                    break;
                case 903:
                case 900:
                case 907:
                    _authentication = "Succeeded";
                    break;
                case 904:
                case 905:
                case 906:
                case 908:
                    _authentication = "Failed";
                    break;
                case 332 when message.Parameters.Count > 1:
                    GetChannel(message.Parameters[1]).Topic = Text(message);
                    break;
                case 331 when message.Parameters.Count > 1:
                    GetChannel(message.Parameters[1]).Topic = null;
                    break;
                case 324 when message.Parameters.Count > 2:
                    ApplyModes(GetChannel(message.Parameters[1]), message.Parameters[2]);
                    break;
                case 353 when message.Parameters.Count > 2:
                    ApplyNames(GetChannel(message.Parameters[2]), Text(message));
                    break;
            }

            return;
        }

        switch (message.Command)
        {
            case "JOIN" when message.Parameters.Count > 0:
                ApplyJoin(GetChannel(message.Parameters[0]), message.Prefix?.Name ?? _nickname);
                break;
            case "PART" when message.Parameters.Count > 0:
                RemoveMember(GetChannel(message.Parameters[0]), message.Prefix?.Name ?? _nickname);
                break;
            case "QUIT":
                foreach (var channel in _channels.Values)
                {
                    RemoveMember(channel, message.Prefix?.Name ?? string.Empty);
                }

                break;
            case "KICK" when message.Parameters.Count > 1:
                RemoveMember(GetChannel(message.Parameters[0]), message.Parameters[1]);
                break;
            case "NICK" when message.Parameters.Count > 0:
                var oldNickname = message.Prefix?.Name;
                if (!string.IsNullOrWhiteSpace(oldNickname))
                {
                    foreach (var channel in _channels.Values)
                    {
                        if (RemoveMember(channel, oldNickname))
                        {
                            channel.Members[message.Parameters[0]] = string.Empty;
                        }
                    }

                    if (oldNickname.Equals(_nickname, StringComparison.OrdinalIgnoreCase))
                    {
                        _nickname = message.Parameters[0];
                    }
                }

                break;
            case "TOPIC" when message.Parameters.Count > 0:
                GetChannel(message.Parameters[0]).Topic = Text(message);
                break;
            case "MODE" when message.Parameters.Count > 1 && message.Parameters[0].Length > 0 && message.Parameters[0][0] is '#' or '&' or '+' or '!':
                ApplyModes(GetChannel(message.Parameters[0]), message.Parameters[1]);
                break;
        }
    }

    private void ApplyJoin(ReplayChannel channel, string nickname)
    {
        channel.Members[nickname] = string.Empty;
        if (nickname.Equals(_nickname, StringComparison.OrdinalIgnoreCase))
        {
            channel.IsObserved = true;
        }
    }

    private void ApplyNames(ReplayChannel channel, string names)
    {
        foreach (var item in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = 0;
            var status = new List<char>();
            while (index < item.Length && "~&@%+".Contains(item[index]))
            {
                status.Add(item[index++]);
            }

            var nickname = item[index..];
            if (nickname.Length > 0)
            {
                channel.Members[nickname] = new string(status.ToArray());
                if (nickname.Equals(_nickname, StringComparison.OrdinalIgnoreCase))
                {
                    channel.IsObserved = true;
                }
            }
        }
    }

    private static void ApplyModes(ReplayChannel channel, string modeString)
    {
        var adding = true;
        foreach (var mode in modeString)
        {
            if (mode == '+')
            {
                adding = true;
            }
            else if (mode == '-')
            {
                adding = false;
            }
            else if (char.IsLetter(mode))
            {
                if (adding)
                {
                    channel.ModeSet.Add(mode);
                }
                else
                {
                    channel.ModeSet.Remove(mode);
                }
            }
        }
    }

    private static bool RemoveMember(ReplayChannel channel, string nickname)
    {
        var key = channel.Members.Keys.FirstOrDefault(key => key.Equals(nickname, StringComparison.OrdinalIgnoreCase));
        return key is not null && channel.Members.Remove(key);
    }

    private ReplayChannel GetChannel(string name)
    {
        return _channels.TryGetValue(name, out var channel)
            ? channel
            : (_channels[name] = new ReplayChannel(name));
    }

    private static string[] CapabilityTokens(IrcMessage message) =>
        (message.HasTrailingParameter ? message.TrailingParameter : null)?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        ?? Array.Empty<string>();

    private static string Text(IrcMessage message) => message.HasTrailingParameter
        ? message.TrailingParameter ?? string.Empty
        : message.Parameters.Count == 0 ? string.Empty : message.Parameters[^1];

    private static string FormatSet(IEnumerable<string> values) => string.Join(' ', values.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase));

    private sealed class ReplayChannel(string name)
    {
        public string Name { get; } = name;

        public bool IsDesired { get; set; }

        public bool IsObserved { get; set; }

        public string? Topic { get; set; }

        public Dictionary<string, string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<char> ModeSet { get; } = [];

        public string Modes => new(ModeSet.OrderBy(static mode => mode).ToArray());

        public string StatusSummary => string.Join(',', Members.Values.Where(static status => status.Length > 0).GroupBy(static status => status).OrderBy(static group => group.Key).Select(static group => $"{group.Key}:{group.Count()}"));
    }
}
