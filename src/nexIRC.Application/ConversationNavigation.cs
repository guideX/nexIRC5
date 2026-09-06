namespace nexIRC.Application;

public enum ConversationOrderingMode
{
    Workspace,
    RecentActivity
}

/// <summary>
/// Stable logical identity for a conversation. The runtime view id is only a
/// presentation handle and is intentionally not part of this identity.
/// </summary>
public sealed record ConversationIdentity(Guid NetworkId, WorkspaceViewKind Kind, string Name)
{
    public string StableKey => $"{NetworkId:N}:{Kind}:{nexIRC.Core.State.IrcCaseMappingComparer.Fold(Name, nexIRC.Core.State.IrcCaseMapping.Rfc1459)}";

    public bool SameAs(ConversationIdentity other) => NetworkId == other.NetworkId
        && Kind == other.Kind
        && nexIRC.Core.State.IrcCaseMappingComparer.Equals(Name, other.Name, nexIRC.Core.State.IrcCaseMapping.Rfc1459);

    public static ConversationIdentity From(WorkspaceView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var name = view switch
        {
            ChannelView channel => channel.Channel,
            QueryView query => query.Nickname,
            _ => view.Title
        };
        return new ConversationIdentity(view.NetworkId, view.Kind, name);
    }
}

public sealed record ConversationNavigationItem(
    ConversationIdentity Identity,
    Guid ViewId,
    string NetworkDisplayName,
    string NetworkIdentity,
    string Name,
    WorkspaceViewKind Kind,
    WorkspaceActivity Activity,
    ConversationLifecycleState LifecycleState,
    bool IsViewOpen,
    DateTimeOffset LastActivity,
    long LastActivitySequence = 0)
{
    public bool IsUnread => Activity != WorkspaceActivity.None;

    public bool IsHighlight => Activity == WorkspaceActivity.Important;

    public string StateMarker => LifecycleState switch
    {
        ConversationLifecycleState.Joining => "◐",
        ConversationLifecycleState.Joined or ConversationLifecycleState.Active => "●",
        ConversationLifecycleState.Disconnected => "◌",
        ConversationLifecycleState.Parted or ConversationLifecycleState.Kicked => "○",
        _ => "◇"
    };

    public string LifecycleText => LifecycleState switch
    {
        ConversationLifecycleState.Joining => "joining",
        ConversationLifecycleState.Joined => "joined",
        ConversationLifecycleState.Parted => "parted",
        ConversationLifecycleState.Kicked => "kicked",
        ConversationLifecycleState.Active => "active",
        ConversationLifecycleState.Disconnected => "disconnected",
        _ => "historical"
    };

    public string ActivityText => Activity switch
    {
        WorkspaceActivity.Important => "highlight / priority",
        WorkspaceActivity.Unread => "unread",
        _ => "read"
    };

    public string DisplayText => Kind == WorkspaceViewKind.ServerStatus
        ? $"{NetworkDisplayName} · server status"
        : $"{NetworkDisplayName} · {Name}";

    public string AccessibleText => $"{DisplayText}; {LifecycleText}; {ActivityText}";
}

/// <summary>
/// A bounded back/forward history of logical conversations. It does not own
/// views, so closed conversations and disconnected networks do not cause
/// unbounded object retention.
/// </summary>
public sealed class ConversationNavigationHistory
{
    private readonly int _maximumEntries;
    private readonly List<ConversationIdentity> _entries = [];
    private int _cursor = -1;

    public ConversationNavigationHistory(int maximumEntries = ConfigurationLimits.MaximumConversationNavigationHistory)
    {
        if (maximumEntries < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        _maximumEntries = maximumEntries;
    }

    public IReadOnlyList<ConversationIdentity> Entries => _entries.ToArray();

    public bool CanGoBack => _cursor > 0;

    public bool CanGoForward => _cursor >= 0 && _cursor < _entries.Count - 1;

    public void Record(ConversationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (_cursor >= 0 && _entries[_cursor].SameAs(identity))
        {
            return;
        }

        if (_cursor < _entries.Count - 1)
        {
            _entries.RemoveRange(_cursor + 1, _entries.Count - _cursor - 1);
        }

        _entries.RemoveAll(existing => existing.SameAs(identity));
        _entries.Add(identity);
        while (_entries.Count > _maximumEntries)
        {
            _entries.RemoveAt(0);
        }

        _cursor = _entries.Count - 1;
    }

    public bool TryGoBack(Func<ConversationIdentity, bool> isAvailable, out ConversationIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        for (var index = _cursor - 1; index >= 0; index--)
        {
            if (isAvailable(_entries[index]))
            {
                _cursor = index;
                identity = _entries[index];
                return true;
            }
        }

        identity = null;
        return false;
    }

    public bool TryGoForward(Func<ConversationIdentity, bool> isAvailable, out ConversationIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(isAvailable);
        for (var index = _cursor + 1; index < _entries.Count; index++)
        {
            if (isAvailable(_entries[index]))
            {
                _cursor = index;
                identity = _entries[index];
                return true;
            }
        }

        identity = null;
        return false;
    }

    public void RemoveNetwork(Guid networkId)
    {
        _entries.RemoveAll(identity => identity.NetworkId == networkId);
        _cursor = Math.Min(_cursor, _entries.Count - 1);
    }

    public void Remove(ConversationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var removedBeforeCursor = _cursor >= 0 && _entries.Take(_cursor + 1).Any(existing => existing.SameAs(identity));
        _entries.RemoveAll(existing => existing.SameAs(identity));
        if (_entries.Count == 0)
        {
            _cursor = -1;
        }
        else if (removedBeforeCursor)
        {
            _cursor = Math.Max(0, _cursor - 1);
        }
        else
        {
            _cursor = Math.Min(_cursor, _entries.Count - 1);
        }
    }
}
