using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Session;

internal sealed class SessionStateStore
{
    private readonly Dictionary<string, MutableChannel> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableQuery> _queries = new(StringComparer.Ordinal);
    private HashSet<string> _desiredChannels;
    private readonly List<string> _motdLines = [];
    private string _nickname;
    private int _connectionGeneration;
    private IrcCaseMapping _caseMapping = IrcCaseMapping.Rfc1459;
    private bool _motdComplete;

    public SessionStateStore(string nickname, IEnumerable<string>? desiredChannels = null)
    {
        _nickname = nickname;
        _desiredChannels = (desiredChannels ?? Array.Empty<string>())
            .Where(static channel => !string.IsNullOrWhiteSpace(channel))
            .ToHashSet(IrcCaseMappingComparer.For(_caseMapping));
    }

    public string Nickname => _nickname;

    public IReadOnlyList<IrcChannelSnapshot> Channels => _channels.Values.Select(channel => channel.Snapshot()).ToArray();

    public IReadOnlyList<IrcQuerySnapshot> Queries => _queries.Values.Select(query => query.Snapshot()).ToArray();

    public IReadOnlySet<string> DesiredChannels => new HashSet<string>(_desiredChannels, IrcCaseMappingComparer.For(_caseMapping));

    public IrcMotdSnapshot Motd => new(_motdComplete, _motdLines.ToArray(), _connectionGeneration);

    public void SetDesiredChannels(IEnumerable<string> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _desiredChannels = new HashSet<string>(IrcCaseMappingComparer.For(_caseMapping));
        foreach (var channel in channels.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            _desiredChannels.Add(channel);
        }
    }

    public bool AddDesiredChannel(string channel) => _desiredChannels.Add(channel);

    public bool RemoveDesiredChannel(string channel) => _desiredChannels.Remove(channel);

    public bool IsDesiredChannel(string channel) => _desiredChannels.Contains(channel);

    public void SetCaseMapping(IrcCaseMapping caseMapping)
    {
        if (_caseMapping == caseMapping)
        {
            return;
        }

        _caseMapping = caseMapping;
        _desiredChannels = _desiredChannels.ToHashSet(IrcCaseMappingComparer.For(_caseMapping));

        var remappedChannels = new Dictionary<string, MutableChannel>(StringComparer.Ordinal);
        foreach (var channel in _channels.Values)
        {
            channel.ModeState.SetCaseMapping(_caseMapping);
            var key = ChannelKey(channel.Name);
            if (remappedChannels.TryGetValue(key, out var existing))
            {
                MergeChannels(existing, channel);
            }
            else
            {
                remappedChannels[key] = channel;
            }
        }

        _channels.Clear();
        foreach (var pair in remappedChannels)
        {
            _channels[pair.Key] = pair.Value;
        }

        var remappedQueries = new Dictionary<string, MutableQuery>(StringComparer.Ordinal);
        foreach (var query in _queries.Values)
        {
            remappedQueries[NameKey(query.Nickname)] = query;
        }

        _queries.Clear();
        foreach (var pair in remappedQueries)
        {
            _queries[pair.Key] = pair.Value;
        }
    }

    public void MarkChannelJoining(string channel)
    {
        GetChannel(channel).Synchronization = ChannelSynchronizationState.Joining;
    }

    public void MarkChannelSynchronizing(string channel)
    {
        GetChannel(channel).Synchronization = ChannelSynchronizationState.Synchronizing;
    }

    public void SetGeneration(int generation)
    {
        _connectionGeneration = generation;
        _motdLines.Clear();
        _motdComplete = false;
        foreach (var channel in _channels.Values)
        {
            channel.IsStale = true;
            channel.IsJoined = false;
            channel.Synchronization = ChannelSynchronizationState.NotRequested;
            channel.NamesInProgress = false;
            channel.ConnectionGeneration = generation;
            channel.Members.Clear();
            channel.ModeState.Reset();
            channel.Topic = null;
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
        SetCaseMapping(features.CaseMapping);
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
            case "MODE":
                ApplyMode(message, features, events);
                break;
            case "KICK":
                ApplyKick(message, events);
                break;
            case "353":
                ApplyNames(message, features, events);
                break;
            case "332":
                ApplyTopicReply(message, events);
                break;
            case "331":
                ApplyTopicUnset(message, events);
                break;
            case "324":
                ApplyNumericMode(message, features, events);
                break;
            case "366":
                ApplyNamesComplete(message, events);
                break;
            case "375":
                _motdLines.Clear();
                _motdComplete = false;
                events.Add(new IrcMotdEvent(message, IrcMotdEventKind.Start, Text(message)));
                break;
            case "372":
                _motdLines.Add(Text(message));
                events.Add(new IrcMotdEvent(message, IrcMotdEventKind.Line, Text(message)));
                break;
            case "376":
            case "422":
                _motdComplete = true;
                events.Add(new IrcMotdEvent(message, IrcMotdEventKind.End, Text(message)));
                break;
            case "321":
                events.Add(new IrcListStartEvent(message));
                break;
            case "322":
                ApplyListItem(message, events);
                break;
            case "323":
                events.Add(new IrcListEndEvent(message));
                break;
            case "352":
                ApplyWho(message, features, events);
                break;
            case "315":
                events.Add(new IrcWhoEndEvent(message, Parameter(message, 1) ?? string.Empty, Text(message)));
                break;
            case "311":
            case "312":
            case "313":
            case "317":
            case "318":
            case "319":
            case "330":
            case "301":
            case "307":
            case "310":
            case "335":
            case "338":
            case "378":
            case "379":
            case "671":
                ApplyWhois(message, events);
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
        var isSelf = NamesEqual(oldNickname, _nickname, _caseMapping);
        if (isSelf)
        {
            var previous = _nickname;
            _nickname = newNickname;
            events.Add(new IrcNicknameChangedEvent(message, previous, newNickname));
        }

        foreach (var channel in _channels.Values)
        {
            if (oldNickname is null)
            {
                continue;
            }

            var oldKey = FindMemberKey(channel, oldNickname);
            if (oldKey is null || !channel.Members.Remove(oldKey, out var member))
            {
                continue;
            }

            var duplicateKey = FindMemberKey(channel, newNickname);
            if (duplicateKey is not null && !string.Equals(duplicateKey, oldKey, StringComparison.Ordinal))
            {
                channel.Members.Remove(duplicateKey);
                channel.ModeState.RemoveMember(duplicateKey);
            }

            channel.Members[newNickname] = member with { Nickname = newNickname };
            channel.ModeState.RenameMember(oldKey, newNickname);
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
        channel.IsJoined |= NamesEqual(nickname, _nickname, _caseMapping);
        if (NamesEqual(nickname, _nickname, _caseMapping))
        {
            channel.Synchronization = ChannelSynchronizationState.Synchronizing;
        }
        var existingKey = FindMemberKey(channel, nickname);
        var existing = existingKey is not null ? channel.Members[existingKey] : null;
        SetMember(channel, existing is null
            ? MemberFromPrefix(nickname, message.Prefix)
            : existing with
            {
                Nickname = nickname,
                Username = message.Prefix?.User ?? existing.Username,
                Host = message.Prefix?.Host ?? existing.Host
            });
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
            var memberKey = FindMemberKey(channel, nickname);
            if (memberKey is not null)
            {
                channel.Members.Remove(memberKey);
                channel.ModeState.RemoveMember(memberKey);
            }
            if (NamesEqual(nickname, _nickname, _caseMapping))
            {
                channel.IsJoined = false;
                channel.IsStale = false;
                channel.Synchronization = ChannelSynchronizationState.NotRequested;
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
            var memberKey = FindMemberKey(channel, nickname);
            if (memberKey is not null)
            {
                channel.Members.Remove(memberKey);
                channel.ModeState.RemoveMember(memberKey);
            }
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

    private void ApplyTopicUnset(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 1);
        if (string.IsNullOrEmpty(channelName))
        {
            return;
        }

        var channel = GetChannel(channelName);
        channel.Topic = null;
        events.Add(new IrcTopicUnsetEvent(message, channelName));
    }

    private void ApplyMode(IrcMessage message, ServerFeatureSet features, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 0);
        if (string.IsNullOrEmpty(channelName) || !IsChannelTarget(channelName, features.ChannelTypes))
        {
            return;
        }

        var channel = GetChannel(channelName);
        var changes = channel.ModeState.Apply(message, features);
        ApplyModeChangesToMembers(channel, changes);
        if (changes.Count > 0)
        {
            events.Add(new IrcModeEvent(message, channelName, changes));
        }
    }

    private void ApplyNumericMode(IrcMessage message, ServerFeatureSet features, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 1);
        if (string.IsNullOrEmpty(channelName))
        {
            return;
        }

        var channel = GetChannel(channelName);
        var changes = channel.ModeState.Apply(message.Parameters, features, parameterOffset: 1);
        ApplyModeChangesToMembers(channel, changes);
        if (changes.Count > 0)
        {
            events.Add(new IrcModeEvent(message, channelName, changes));
        }
    }

    private void ApplyModeChangesToMembers(MutableChannel channel, IReadOnlyList<IrcModeChange> changes)
    {
        foreach (var change in changes.Where(static item => item.Kind == IrcChannelModeKind.MemberPrefix && item.Parameter is not null))
        {
            var nickname = change.Parameter!;
            var memberKey = FindMemberKey(channel, nickname) ?? nickname;
            if (!channel.Members.TryGetValue(memberKey, out var member))
            {
                member = new IrcChannelMemberSnapshot(nickname, null, null, new HashSet<char>());
            }

            var modes = new HashSet<char>(member.PrefixModes);
            if (change.IsAdding)
            {
                modes.Add(change.Mode);
            }
            else
            {
                modes.Remove(change.Mode);
            }

            channel.Members.Remove(memberKey);
            var duplicateKey = FindMemberKey(channel, nickname);
            if (duplicateKey is not null)
            {
                channel.Members.Remove(duplicateKey);
            }

            channel.Members[nickname] = member with { Nickname = nickname, PrefixModes = modes };
            channel.ModeState.SetMemberModes(nickname, modes);
        }
    }

    private void ApplyKick(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 0);
        var nickname = Parameter(message, 1);
        if (string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(nickname))
        {
            return;
        }

        if (_channels.TryGetValue(ChannelKey(channelName), out var channel))
        {
            var memberKey = FindMemberKey(channel, nickname);
            if (memberKey is not null)
            {
                channel.Members.Remove(memberKey);
                channel.ModeState.RemoveMember(memberKey);
            }

            if (NamesEqual(nickname, _nickname, _caseMapping))
            {
                channel.IsJoined = false;
                channel.IsStale = false;
                channel.Synchronization = ChannelSynchronizationState.NotRequested;
            }
        }

        events.Add(new IrcKickEvent(message, channelName, nickname, message.HasTrailingParameter ? message.TrailingParameter : null));
    }

    private void ApplyNames(IrcMessage message, ServerFeatureSet features, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 2);
        if (string.IsNullOrEmpty(channelName) || !message.HasTrailingParameter)
        {
            return;
        }

        var channel = GetChannel(channelName);
        if (!channel.NamesInProgress)
        {
            channel.Members.Clear();
            channel.ModeState.ClearMemberModes();
            channel.NamesInProgress = true;
        }

        var names = new List<string>();
        foreach (var item in message.TrailingParameter!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var nick = item;
            var modes = new HashSet<char>();
            var prefix = features.Prefix ?? IrcPrefixGrammar.Default;
            var index = 0;
            while (index < nick.Length && prefix.PrefixToMode.TryGetValue(nick[index], out var mode))
            {
                modes.Add(mode);
                index++;
            }

            nick = nick[index..];

            if (nick.Length == 0)
            {
                continue;
            }

            var existing = FindMemberKey(channel, nick);
            channel.Members[nick] = new IrcChannelMemberSnapshot(
                nick,
                existing is not null ? channel.Members[existing].Username : null,
                existing is not null ? channel.Members[existing].Host : null,
                modes);
            channel.ModeState.SetMemberModes(nick, modes);
            if (existing is not null && !string.Equals(existing, nick, StringComparison.Ordinal))
            {
                channel.Members.Remove(existing);
            }

            if (NamesEqual(nick, _nickname, _caseMapping))
            {
                channel.IsJoined = true;
                channel.IsStale = false;
                channel.Synchronization = ChannelSynchronizationState.Synchronizing;
            }
            names.Add(nick);
        }

        events.Add(new IrcNamesEvent(message, channelName, names));
    }

    private void ApplyNamesComplete(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channelName = Parameter(message, 1);
        if (string.IsNullOrEmpty(channelName))
        {
            return;
        }

        var channel = GetChannel(channelName);
        channel.NamesInProgress = false;
        if (channel.IsJoined)
        {
            channel.Synchronization = ChannelSynchronizationState.Synchronized;
            channel.IsStale = false;
        }

        events.Add(new IrcNamesCompleteEvent(message, channelName));
    }

    private void ApplyListItem(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var channel = Parameter(message, 1);
        if (string.IsNullOrEmpty(channel))
        {
            return;
        }

        var count = int.TryParse(Parameter(message, 2), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        events.Add(new IrcListItemEvent(message, channel, count, Text(message)));
    }

    private void ApplyWho(IrcMessage message, ServerFeatureSet features, List<IrcSemanticEvent> events)
    {
        if (message.Parameters.Count < 8)
        {
            return;
        }

        var channelName = Parameter(message, 1) ?? string.Empty;
        var username = Parameter(message, 2) ?? string.Empty;
        var host = Parameter(message, 3) ?? string.Empty;
        var server = Parameter(message, 4) ?? string.Empty;
        var nickname = Parameter(message, 5) ?? string.Empty;
        var status = Parameter(message, 6) ?? string.Empty;
        var realName = Parameter(message, 7) ?? string.Empty;
        if (string.IsNullOrEmpty(channelName) || string.IsNullOrEmpty(nickname))
        {
            return;
        }

        var channel = GetChannel(channelName);
        var existingKey = FindMemberKey(channel, nickname);
        var existingModes = existingKey is not null ? channel.Members[existingKey].PrefixModes : ParsePrefixModes(status, features);
        channel.Members[nickname] = new IrcChannelMemberSnapshot(nickname, username, host, existingModes);
        channel.ModeState.SetMemberModes(nickname, existingModes);
        if (existingKey is not null && !string.Equals(existingKey, nickname, StringComparison.Ordinal))
        {
            channel.Members.Remove(existingKey);
        }

        events.Add(new IrcWhoEvent(message, channelName, nickname, username, host, server, status, realName));
    }

    private static void ApplyWhois(IrcMessage message, List<IrcSemanticEvent> events)
    {
        var nickname = Parameter(message, 1) ?? Parameter(message, 0) ?? string.Empty;
        events.Add(new IrcWhoisEvent(message, message.NumericCommand ?? 0, nickname, message.Parameters, message.HasTrailingParameter ? message.TrailingParameter : null));
    }

    private static string Text(IrcMessage message) => message.HasTrailingParameter
        ? message.TrailingParameter ?? string.Empty
        : message.Parameters.Count == 0 ? string.Empty : message.Parameters[message.Parameters.Count - 1];

    private static HashSet<char> ParsePrefixModes(string status, ServerFeatureSet features)
    {
        var modes = new HashSet<char>();
        var grammar = features.Prefix ?? IrcPrefixGrammar.Default;

        foreach (var prefix in status)
        {
            if (grammar.PrefixToMode.TryGetValue(prefix, out var mode))
            {
                modes.Add(mode);
            }
        }

        return modes;
    }

    private void SetMember(MutableChannel channel, IrcChannelMemberSnapshot member)
    {
        var existingKey = FindMemberKey(channel, member.Nickname);
        if (existingKey is not null)
        {
            channel.Members.Remove(existingKey);
            channel.ModeState.RemoveMember(existingKey);
        }

        channel.Members[member.Nickname] = member;
        channel.ModeState.SetMemberModes(member.Nickname, member.PrefixModes);
    }

    private static void MergeChannels(MutableChannel destination, MutableChannel source)
    {
        destination.IsJoined |= source.IsJoined;
        destination.IsStale &= source.IsStale;
        destination.Topic ??= source.Topic;
        destination.ConnectionGeneration = Math.Max(destination.ConnectionGeneration, source.ConnectionGeneration);
        if (destination.Synchronization < source.Synchronization)
        {
            destination.Synchronization = source.Synchronization;
        }

        foreach (var member in source.Members.Values)
        {
            destination.Members[member.Nickname] = member;
        }
    }

    private MutableChannel GetChannel(string name)
    {
        var key = ChannelKey(name);
        if (_channels.TryGetValue(key, out var channel))
        {
            return channel;
        }

        channel = new MutableChannel(name, _connectionGeneration);
        channel.ModeState.SetCaseMapping(_caseMapping);
        _channels[key] = channel;
        return channel;
    }

    private static IrcChannelMemberSnapshot MemberFromPrefix(string nickname, IrcPrefix? prefix) =>
        new(nickname, prefix?.User, prefix?.Host, new HashSet<char>());

    private static string? Parameter(IrcMessage message, int index) => index < message.Parameters.Count ? message.Parameters[index] : null;

    private static bool IsChannelTarget(string target, IReadOnlySet<char> channelTypes) => target.Length > 0 && channelTypes.Contains(target[0]);

    private string ChannelKey(string name) => NormalizeName(name, _caseMapping);

    private string NameKey(string name) => NormalizeName(name, _caseMapping);

    private static bool NamesEqual(string? left, string? right, IrcCaseMapping mapping) => IrcCaseMappingComparer.Equals(left, right, mapping);

    private static string NormalizeName(string value, IrcCaseMapping mapping) => IrcCaseMappingComparer.Fold(value, mapping);

    private string? FindMemberKey(MutableChannel channel, string nickname)
    {
        return channel.Members.Keys.FirstOrDefault(key => NamesEqual(key, nickname, _caseMapping));
    }

    private sealed class MutableChannel
    {
        public MutableChannel(string name, int connectionGeneration)
        {
            Name = name;
            ConnectionGeneration = connectionGeneration;
            ModeState = new IrcChannelModeState(name);
        }

        public string Name { get; }
        public bool IsJoined { get; set; }
        public bool IsStale { get; set; }
        public string? Topic { get; set; }
        public int ConnectionGeneration { get; set; }
        public bool NamesInProgress { get; set; }
        public ChannelSynchronizationState Synchronization { get; set; }
        public IrcChannelModeState ModeState { get; }
        public Dictionary<string, IrcChannelMemberSnapshot> Members { get; } = new(StringComparer.Ordinal);

        public IrcChannelSnapshot Snapshot()
        {
            var modeSnapshot = ModeState.Snapshot;
            return new IrcChannelSnapshot(Name, IsJoined, IsStale, Topic, new Dictionary<string, IrcChannelMemberSnapshot>(Members, IrcCaseMappingComparer.For(ModeState.CaseMapping)), ConnectionGeneration)
            {
                Modes = modeSnapshot.Modes,
                ModeParameters = modeSnapshot.Parameters,
                Synchronization = Synchronization
            };
        }
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
