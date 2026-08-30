using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Session;

internal sealed class SessionStateStore
{
    private readonly Dictionary<string, MutableChannel> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableQuery> _queries = new(StringComparer.Ordinal);
    private string _nickname;
    private int _connectionGeneration;

    public SessionStateStore(string nickname)
    {
        _nickname = nickname;
    }

    public string Nickname => _nickname;

    public IReadOnlyList<IrcChannelSnapshot> Channels => _channels.Values.Select(channel => channel.Snapshot()).ToArray();

    public IReadOnlyList<IrcQuerySnapshot> Queries => _queries.Values.Select(query => query.Snapshot()).ToArray();

    public void SetGeneration(int generation)
    {
        _connectionGeneration = generation;
        foreach (var channel in _channels.Values)
        {
            channel.IsStale = true;
            channel.IsJoined = false;
            channel.ConnectionGeneration = generation;
            channel.Members.Clear();
        }

        foreach (var query in _queries.Values)
        {
            query.ConnectionGeneration = generation;
            query.Messages.Clear();
        }
    }

    public void SetNickname(string nickname)
    {
        _nickname = nickname;
    }

    public IReadOnlyList<IrcSemanticEvent> Apply(IrcMessage message, ServerFeatureSet features)
    {
        var events = new List<IrcSemanticEvent>();
        var command = message.Command;
        switch (command)
        {
            case "NICK":
                ApplyNick(message, events);
                break;
            case "JOIN":
                ApplyJoin(message, events);
                break;
            case "PART":
                ApplyPart(message, events);
                break;
            case "QUIT":
                ApplyQuit(message, events);
                break;
            case "PRIVMSG":
            case "NOTICE":
                ApplyMessage(message, features, events, command == "NOTICE");
                break;
            case "TOPIC":
                ApplyTopic(message, events);
                break;
            case "353":
                ApplyNames(message, features, events);
                break;
            case "332":
                ApplyTopicReply(message, events);
                break;
            case "001":
                events.Add(new IrcWelcomeEvent(message, features.NetworkName));
                break;
        }

        return events;
    }

    private void ApplyNick(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var newNickname = Parameter(message, 0);
        if (string.IsNullOrEmpty(newNickname))
        {
            return;
        }

        var oldNickname = message.Prefix?.Name;
        var isSelf = NamesEqual(oldNickname, _nickname, IrcCaseMapping.Rfc1459);
        if (isSelf)
        {
            var previous = _nickname;
            _nickname = newNickname;
            events.Add(new IrcNicknameChangedEvent(message, previous, newNickname));
        }

        foreach (var channel in _channels.Values)
        {
            if (oldNickname is null || !channel.Members.Remove(oldNickname, out var member))
            {
                continue;
            }

            channel.Members[newNickname] = member with { Nickname = newNickname };
        }

        if (!isSelf)
        {
            events.Add(new IrcNicknameChangedEvent(message, oldNickname, newNickname));
        }
    }

    private void ApplyJoin(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 0);
        var nickname = message.Prefix?.Name ?? _nickname;
        if (string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(nickname))
        {
            return;
        }

        var channel = GetChannel(channelName);
        channel.IsStale = false;
        channel.IsJoined |= NamesEqual(nickname, _nickname, IrcCaseMapping.Rfc1459);
        channel.Members[nickname] = MemberFromPrefix(nickname, message.Prefix);
        events.Add(new IrcJoinEvent(message, channelName, nickname));
    }

    private void ApplyPart(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 0);
        var nickname = message.Prefix?.Name ?? _nickname;
        if (string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(nickname))
        {
            return;
        }

        if (_channels.TryGetValue(ChannelKey(channelName), out var channel))
        {
            channel.Members.Remove(nickname);
            if (NamesEqual(nickname, _nickname, IrcCaseMapping.Rfc1459))
            {
                channel.IsJoined = false;
                channel.IsStale = false;
            }
        }

        events.Add(new IrcPartEvent(message, channelName, nickname));
    }

    private void ApplyQuit(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var nickname = message.Prefix?.Name;
        if (string.IsNullOrEmpty(nickname))
        {
            return;
        }

        foreach (var channel in _channels.Values)
        {
            channel.Members.Remove(nickname);
        }

        events.Add(new IrcQuitEvent(message, nickname, message.HasTrailingParameter ? message.TrailingParameter : null));
    }

    private void ApplyMessage(IrcMessage message, ServerFeatureSet features, List<IrcSemanticEvent> events, bool isNotice)
    {
        var target = Parameter(message, 0);
        var text = message.HasTrailingParameter ? message.TrailingParameter ?? string.Empty : Parameter(message, 1) ?? string.Empty;
        if (string.IsNullOrEmpty(target))
        {
            return;
        }

        var semantic = new IrcPrivmsgEvent(message, target, text, isNotice);
        events.Add(semantic);
        if (!IsChannelTarget(target, features.ChannelTypes) && !string.IsNullOrEmpty(message.Prefix?.Name) && !NamesEqual(message.Prefix.Name, _nickname, features.CaseMapping))
        {
            var query = _queries.TryGetValue(NameKey(message.Prefix.Name), out var existing)
                ? existing
                : (_queries[NameKey(message.Prefix.Name)] = new MutableQuery(message.Prefix.Name, _connectionGeneration));
            query.Messages.Add(text);
            events.Add(new IrcQueryMessageEvent(message, message.Prefix.Name, text, isNotice));
        }
    }

    private void ApplyTopic(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 0);
        if (string.IsNullOrEmpty(channelName))
        {
            return;
        }

        var channel = GetChannel(channelName);
        var topic = message.HasTrailingParameter ? message.TrailingParameter ?? string.Empty : Parameter(message, 1) ?? string.Empty;
        channel.Topic = topic;
        events.Add(new IrcTopicEvent(message, channelName, topic));
    }

    private void ApplyTopicReply(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 1);
        if (string.IsNullOrEmpty(channelName))
        {
            return;
        }

        var channel = GetChannel(channelName);
        channel.Topic = message.HasTrailingParameter ? message.TrailingParameter : Parameter(message, 2);
        events.Add(new IrcTopicEvent(message, channelName, channel.Topic ?? string.Empty));
    }

    private void ApplyNames(IrcMessage message, ServerFeatureSet features, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 2);
        if (string.IsNullOrEmpty(channelName) || !message.HasTrailingParameter)
        {
            return;
        }

        var channel = GetChannel(channelName);
        var names = new List<string>();
        foreach (var item in message.TrailingParameter!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var nick = item;
            var modes = new HashSet<char>();
            if (features.Prefix is not null)
            {
                var index = 0;
                while (index < nick.Length && features.Prefix.PrefixToMode.TryGetValue(nick[index], out var mode))
                {
                    modes.Add(mode);
                    index++;
                }

                nick = nick[index..];
            }

            if (nick.Length == 0)
            {
                continue;
            }

            channel.Members[nick] = new IrcChannelMemberSnapshot(nick, null, null, modes);
            names.Add(nick);
        }

        events.Add(new IrcNamesEvent(message, channelName, names));
    }

    private MutableChannel GetChannel(string name)
    {
        var key = ChannelKey(name);
        return _channels.TryGetValue(key, out var channel)
            ? channel
            : (_channels[key] = new MutableChannel(name, _connectionGeneration));
    }

    private static IrcChannelMemberSnapshot MemberFromPrefix(string nickname, IrcPrefix? prefix) =>
        new(nickname, prefix?.User, prefix?.Host, new HashSet<char>());

    private static string? Parameter(IrcMessage message, int index) => index < message.Parameters.Count ? message.Parameters[index] : null;

    private static bool IsChannelTarget(string target, IReadOnlySet<char> channelTypes) => target.Length > 0 && channelTypes.Contains(target[0]);

    private static string ChannelKey(string name) => name.ToLowerInvariant();

    private static string NameKey(string name) => name.ToLowerInvariant();

    private static bool NamesEqual(string? left, string? right, IrcCaseMapping mapping) => left is not null && right is not null && NormalizeName(left, mapping) == NormalizeName(right, mapping);

    private static string NormalizeName(string value, IrcCaseMapping mapping)
    {
        var lower = value.ToLowerInvariant();
        if (mapping is IrcCaseMapping.Rfc1459 or IrcCaseMapping.StrictRfc1459)
        {
            lower = lower.Replace('{', '[').Replace('}', ']').Replace('|', '\\');
            if (mapping == IrcCaseMapping.Rfc1459)
            {
                lower = lower.Replace('^', '~');
            }
        }

        return lower;
    }

    private sealed class MutableChannel
    {
        public MutableChannel(string name, int connectionGeneration)
        {
            Name = name;
            ConnectionGeneration = connectionGeneration;
        }

        public string Name { get; }
        public bool IsJoined { get; set; }
        public bool IsStale { get; set; }
        public string? Topic { get; set; }
        public int ConnectionGeneration { get; set; }
        public Dictionary<string, IrcChannelMemberSnapshot> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IrcChannelSnapshot Snapshot() => new(Name, IsJoined, IsStale, Topic, new Dictionary<string, IrcChannelMemberSnapshot>(Members, StringComparer.OrdinalIgnoreCase), ConnectionGeneration);
    }

    private sealed class MutableQuery
    {
        public MutableQuery(string nickname, int connectionGeneration)
        {
            Nickname = nickname;
            ConnectionGeneration = connectionGeneration;
        }

        public string Nickname { get; }
        public int ConnectionGeneration { get; set; }
        public List<string> Messages { get; } = [];

        public IrcQuerySnapshot Snapshot() => new(Nickname, Messages.ToArray(), ConnectionGeneration);
    }
}
