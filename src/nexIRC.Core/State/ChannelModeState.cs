using nexIRC.Core.Protocol;

namespace nexIRC.Core.State;

public enum IrcChannelModeKind
{
    Unknown,
    List,
    ParameterAlways,
    ParameterWhenSet,
    NoParameter,
    MemberPrefix
}

public sealed record IrcModeChange(
    char Mode,
    bool IsAdding,
    string? Parameter,
    IrcChannelModeKind Kind)
{
    public bool Adding => IsAdding;
}

public sealed record IrcChannelModeSnapshot(
    IReadOnlySet<char> Modes,
    IReadOnlyDictionary<char, IReadOnlyList<string>> Parameters,
    IReadOnlyDictionary<string, IReadOnlySet<char>> MemberModes)
{
    public IReadOnlySet<char> ActiveModes => Modes;
}

/// <summary>
/// Applies one channel's MODE changes using the server's negotiated PREFIX and
/// CHANMODES grammar. Unknown modes remain observable as changes and never cause
/// the rest of a mode string to be silently discarded.
/// </summary>
public sealed class IrcChannelModeState
{
    private readonly HashSet<char> _modes = [];
    private readonly Dictionary<char, List<string>> _parameters = [];
    private Dictionary<string, HashSet<char>> _memberModes = new(StringComparer.Ordinal);
    private IrcCaseMapping _caseMapping = IrcCaseMapping.Rfc1459;

    public IrcChannelModeState(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new ArgumentException("A channel mode state requires a channel name.", nameof(channel));
        }

        Channel = channel;
    }

    public string Channel { get; }

    public IrcCaseMapping CaseMapping => _caseMapping;

    public IReadOnlySet<char> Modes => new HashSet<char>(_modes);

    public IReadOnlyDictionary<char, IReadOnlyList<string>> Parameters =>
        _parameters.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.ToArray());

    public IReadOnlyDictionary<string, IReadOnlySet<char>> MemberModes =>
        _memberModes.ToDictionary(pair => pair.Key, pair => (IReadOnlySet<char>)new HashSet<char>(pair.Value), IrcCaseMappingComparer.For(_caseMapping));

    public IrcChannelModeSnapshot Snapshot => new(Modes, Parameters, MemberModes);

    public void SetCaseMapping(IrcCaseMapping caseMapping)
    {
        if (_caseMapping == caseMapping)
        {
            return;
        }

        var remapped = new Dictionary<string, HashSet<char>>(StringComparer.Ordinal);
        foreach (var pair in _memberModes)
        {
            var existingKey = remapped.Keys.FirstOrDefault(key => IrcCaseMappingComparer.Equals(key, pair.Key, caseMapping));
            if (existingKey is null)
            {
                remapped[pair.Key] = pair.Value;
            }
            else
            {
                remapped[existingKey].UnionWith(pair.Value);
            }
        }

        _memberModes = remapped;
        _caseMapping = caseMapping;
    }

    public IReadOnlyList<IrcModeChange> Apply(IrcMessage message, ServerFeatureSet features) =>
        Apply(message.Parameters, features, parameterOffset: 0);

    public IReadOnlyList<IrcModeChange> Apply(
        IReadOnlyList<string> parameters,
        ServerFeatureSet features,
        int parameterOffset = 0)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(features);
        SetCaseMapping(features.CaseMapping);

        if (parameterOffset < 0 || parameterOffset >= parameters.Count ||
            !IrcCaseMappingComparer.Equals(parameters[parameterOffset], Channel, features.CaseMapping))
        {
            return Array.Empty<IrcModeChange>();
        }

        var modeIndex = parameterOffset + 1;
        if (modeIndex >= parameters.Count || string.IsNullOrEmpty(parameters[modeIndex]))
        {
            return Array.Empty<IrcModeChange>();
        }

        return ApplyModeString(parameters[modeIndex], parameters, modeIndex + 1, features);
    }

    public IReadOnlyList<IrcModeChange> ApplyModeString(
        string modeString,
        IReadOnlyList<string> modeParameters,
        ServerFeatureSet features)
    {
        ArgumentNullException.ThrowIfNull(modeString);
        ArgumentNullException.ThrowIfNull(modeParameters);
        ArgumentNullException.ThrowIfNull(features);
        return ApplyModeString(modeString, modeParameters, 0, features);
    }

    public void Reset()
    {
        _modes.Clear();
        _parameters.Clear();
        _memberModes.Clear();
    }

    public IReadOnlySet<char> GetMemberModes(string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var memberKey = FindMemberKey(nickname);
        return memberKey is not null && _memberModes.TryGetValue(memberKey, out var modes)
            ? new HashSet<char>(modes)
            : new HashSet<char>();
    }

    public void SetMemberModes(string nickname, IEnumerable<char> modes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        ArgumentNullException.ThrowIfNull(modes);
        var memberKey = FindMemberKey(nickname);
        if (memberKey is not null)
        {
            _memberModes.Remove(memberKey);
        }

        _memberModes[nickname] = modes.ToHashSet();
    }

    public void RenameMember(string oldNickname, string newNickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldNickname);
        ArgumentException.ThrowIfNullOrWhiteSpace(newNickname);
        var oldKey = FindMemberKey(oldNickname);
        if (oldKey is not null && _memberModes.Remove(oldKey, out var modes))
        {
            var newKey = FindMemberKey(newNickname);
            if (newKey is not null)
            {
                _memberModes.Remove(newKey);
            }

            _memberModes[newNickname] = modes;
        }
    }

    public void RemoveMember(string nickname)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nickname);
        var memberKey = FindMemberKey(nickname);
        if (memberKey is not null)
        {
            _memberModes.Remove(memberKey);
        }
    }

    public void ClearMemberModes() => _memberModes.Clear();

    private List<IrcModeChange> ApplyModeString(
        string modeString,
        IReadOnlyList<string> modeParameters,
        int parameterIndex,
        ServerFeatureSet features)
    {
        var grammar = features.ChannelModes ?? IrcChannelModeGrammar.Default;
        var prefix = features.Prefix ?? IrcPrefixGrammar.Default;
        var changes = new List<IrcModeChange>();
        var adding = true;
        foreach (var mode in modeString)
        {
            if (mode == '+')
            {
                adding = true;
                continue;
            }

            if (mode == '-')
            {
                adding = false;
                continue;
            }

            var kind = GetKind(mode, grammar, prefix);
            var consumesParameter = kind is IrcChannelModeKind.List or IrcChannelModeKind.ParameterAlways or
                IrcChannelModeKind.MemberPrefix || (kind == IrcChannelModeKind.ParameterWhenSet && adding);
            string? parameter = null;
            if (consumesParameter && parameterIndex < modeParameters.Count)
            {
                parameter = modeParameters[parameterIndex++];
            }

            var change = new IrcModeChange(mode, adding, parameter, kind);
            changes.Add(change);
            if (!RequiresParameter(kind) || parameter is not null)
            {
                ApplyChange(change);
            }
        }

        return changes;
    }

    private void ApplyChange(IrcModeChange change)
    {
        if (change.Kind == IrcChannelModeKind.MemberPrefix)
        {
            if (string.IsNullOrWhiteSpace(change.Parameter))
            {
                return;
            }

            if (!_memberModes.TryGetValue(change.Parameter, out var memberModes))
            {
                memberModes = [];
                _memberModes[change.Parameter] = memberModes;
            }

            if (change.IsAdding)
            {
                memberModes.Add(change.Mode);
            }
            else
            {
                memberModes.Remove(change.Mode);
                if (memberModes.Count == 0)
                {
                    _memberModes.Remove(change.Parameter);
                }
            }

            return;
        }

        if (change.IsAdding)
        {
            _modes.Add(change.Mode);
            if (change.Parameter is not null && change.Kind is (IrcChannelModeKind.List or IrcChannelModeKind.ParameterAlways or IrcChannelModeKind.ParameterWhenSet))
            {
                if (!_parameters.TryGetValue(change.Mode, out var values))
                {
                    values = [];
                    _parameters[change.Mode] = values;
                }

                if (change.Kind == IrcChannelModeKind.List)
                {
                    if (!values.Contains(change.Parameter, StringComparer.Ordinal))
                    {
                        values.Add(change.Parameter);
                    }
                }
                else
                {
                    values.Clear();
                    values.Add(change.Parameter);
                }
            }

            return;
        }

        _modes.Remove(change.Mode);
        if (!_parameters.TryGetValue(change.Mode, out var removedValues))
        {
            return;
        }

        if (change.Kind == IrcChannelModeKind.List && change.Parameter is not null)
        {
            removedValues.Remove(change.Parameter);
            if (removedValues.Count > 0)
            {
                _modes.Add(change.Mode);
                return;
            }
        }

        _parameters.Remove(change.Mode);
    }

    private static IrcChannelModeKind GetKind(char mode, IrcChannelModeGrammar grammar, IrcPrefixGrammar? prefix)
    {
        if (prefix?.ModeToPrefix.ContainsKey(mode) == true)
        {
            return IrcChannelModeKind.MemberPrefix;
        }

        if (grammar.ListModes.Contains(mode))
        {
            return IrcChannelModeKind.List;
        }

        if (grammar.ParameterAlwaysModes.Contains(mode))
        {
            return IrcChannelModeKind.ParameterAlways;
        }

        if (grammar.ParameterWhenSetModes.Contains(mode))
        {
            return IrcChannelModeKind.ParameterWhenSet;
        }

        if (grammar.NoParameterModes.Contains(mode))
        {
            return IrcChannelModeKind.NoParameter;
        }

        return IrcChannelModeKind.Unknown;
    }

    private static bool RequiresParameter(IrcChannelModeKind kind) => kind is IrcChannelModeKind.List or
        IrcChannelModeKind.ParameterAlways or IrcChannelModeKind.MemberPrefix;

    private string? FindMemberKey(string nickname) => _memberModes.Keys.FirstOrDefault(key => IrcCaseMappingComparer.Equals(key, nickname, _caseMapping));
}
