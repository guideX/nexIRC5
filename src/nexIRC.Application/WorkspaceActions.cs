using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public enum WorkspaceActionId
{
    Connect,
    Disconnect,
    Reconnect,
    OpenNetwork,
    OpenChannel,
    JoinChannel,
    RejoinChannel,
    PartChannel,
    RequestTopic,
    EditTopic,
    RequestModes,
    RefreshNames,
    LoadOlderMessages,
    LoadNewerMessages,
    ReturnToLatest,
    GoToHistoryTimestamp,
    JumpToHistoryMessage,
    LoadContextAround,
    Reply
}

public enum WorkspaceActionTargetKind
{
    Network,
    Channel,
    Query,
    Nickname
}

public sealed record WorkspaceActionTarget(
    Guid NetworkId,
    Guid? ViewId = null,
    string? Name = null,
    string? Nickname = null);

public sealed record WorkspaceActionDescriptor(
    WorkspaceActionId Action,
    string Label,
    WorkspaceActionTargetKind TargetKind,
    WorkspaceActionTarget Target,
    bool IsEnabled,
    string? DisabledReason = null)
{
    public string AccessibleText => IsEnabled || string.IsNullOrWhiteSpace(DisabledReason)
        ? Label
        : $"{Label} ({DisabledReason})";
}

/// <summary>
/// Shared application action boundary for commands and contextual UI.  It
/// owns target/state validation and routes accepted operations through the
/// session manager; WPF never writes protocol lines directly.
/// </summary>
public sealed class WorkspaceActionRouter
{
    private readonly NetworkSessionManager _sessions;

    public WorkspaceActionRouter(NetworkSessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        ParticipantActions = new ParticipantActionService(sessions);
        ChannelActions = new ChannelActionService(sessions);
    }

    public ParticipantActionService ParticipantActions { get; }

    public ChannelActionService ChannelActions { get; }

    public ValueTask<HistoryNavigationResult> NavigateHistoryAsync(
        NetworkWorkspace network,
        WorkspaceView view,
        HistoryNavigationRequest request,
        CancellationToken cancellationToken = default) =>
        _sessions.NavigateHistoryAsync(network, view, request, cancellationToken);

    public IReadOnlyList<WorkspaceActionDescriptor> BuildNetworkActions(NetworkWorkspace network)
    {
        ArgumentNullException.ThrowIfNull(network);
        var state = network.State;
        var canConnect = state is NetworkDisplayState.Disconnected or NetworkDisplayState.Failed;
        var canDisconnect = state is not (NetworkDisplayState.Disconnected or NetworkDisplayState.Failed or NetworkDisplayState.ReconnectWaiting);
        var canReconnect = state is NetworkDisplayState.Disconnected
            or NetworkDisplayState.Failed
            or NetworkDisplayState.Registered
            or NetworkDisplayState.Connected;
        var target = new WorkspaceActionTarget(network.Id, network.StatusView.Id, network.DisplayName);
        return
        [
            new(WorkspaceActionId.Connect, "Connect", WorkspaceActionTargetKind.Network, target, canConnect,
                canConnect ? null : "The network is already connected or is transitioning."),
            new(WorkspaceActionId.Disconnect, "Disconnect", WorkspaceActionTargetKind.Network, target, canDisconnect,
                canDisconnect ? null : "The network is already disconnected or waiting to reconnect."),
            new(WorkspaceActionId.Reconnect, "Reconnect", WorkspaceActionTargetKind.Network, target, canReconnect,
                canReconnect ? null : "Reconnect is unavailable during this lifecycle transition."),
            new(WorkspaceActionId.OpenNetwork, "Open status", WorkspaceActionTargetKind.Network, target, true)
        ];
    }

    public IReadOnlyList<WorkspaceActionDescriptor> BuildChannelActions(NetworkWorkspace network, ChannelView channel)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(channel);
        var consistent = network.Id == channel.NetworkId;
        var registered = consistent && IsRegistered(network);
        var logicallyJoined = channel.IsJoined && channel.LifecycleState is not (ConversationLifecycleState.Parted or ConversationLifecycleState.Kicked or ConversationLifecycleState.Disconnected or ConversationLifecycleState.HistoricalOnly);
        var joined = registered && logicallyJoined;
        var authority = consistent ? ChannelAuthority.Evaluate(network, channel) : null;
        var target = new WorkspaceActionTarget(network.Id, channel.Id, channel.Channel);
        var canLoadOlder = joined && _sessions.CanLoadOlderHistory(network, channel);
        var canLoadNewer = joined && _sessions.CanLoadNewerHistory(network, channel);
        var channelEntries = channel.EntriesSnapshot;
        var contextAnchor = channelEntries.Count == 0 ? null : channelEntries[^1];
        var canLoadContext = joined && contextAnchor is not null && _sessions.CanLoadContextAround(network, channel, contextAnchor);
        return
        [
            new(WorkspaceActionId.OpenChannel, "Open channel", WorkspaceActionTargetKind.Channel, target, consistent),
            new(WorkspaceActionId.JoinChannel, "Join channel", WorkspaceActionTargetKind.Channel, target, registered && !logicallyJoined,
                registered && !logicallyJoined ? null : !registered ? "Connect and register first" : "The channel is already joined."),
            new(WorkspaceActionId.RejoinChannel, "Rejoin channel", WorkspaceActionTargetKind.Channel, target, registered && !logicallyJoined,
                registered && !logicallyJoined ? null : !registered ? "Connect and register first" : "The channel is already joined."),
            new(WorkspaceActionId.PartChannel, "Part channel", WorkspaceActionTargetKind.Channel, target, joined,
                joined ? null : !registered ? "Connect and register first" : "The channel is not currently joined."),
            new(WorkspaceActionId.RequestTopic, "Request topic", WorkspaceActionTargetKind.Channel, target, joined,
                joined ? null : "Join the channel first."),
            new(WorkspaceActionId.EditTopic, "Edit topic", WorkspaceActionTargetKind.Channel, target, joined && authority!.ChangeTopic.IsAllowed,
                joined && authority!.ChangeTopic.IsAllowed ? null : !joined ? "Join the channel first." : authority!.ChangeTopic.Reason),
            new(WorkspaceActionId.RequestModes, "Request channel modes", WorkspaceActionTargetKind.Channel, target, joined,
                joined ? null : "Join the channel first."),
            new(WorkspaceActionId.RefreshNames, "Refresh member list", WorkspaceActionTargetKind.Channel, target, joined,
                joined ? null : "Join the channel first."),
            new(WorkspaceActionId.LoadOlderMessages, "Load older messages", WorkspaceActionTargetKind.Channel, target, canLoadOlder,
                canLoadOlder ? null : !joined ? "Join the channel first." : "Server history is unavailable or already exhausted."),
            new(WorkspaceActionId.LoadNewerMessages, "Load newer messages", WorkspaceActionTargetKind.Channel, target, canLoadNewer,
                canLoadNewer ? null : !joined ? "Join the channel first." : "Newer history is unavailable or already exhausted."),
            new(WorkspaceActionId.ReturnToLatest, "Return to latest", WorkspaceActionTargetKind.Channel, target, channel.EntryCount > 0,
                channel.EntryCount > 0 ? null : "There is no projected history to return to."),
            new(WorkspaceActionId.GoToHistoryTimestamp, "Go to date/time", WorkspaceActionTargetKind.Channel, target, false,
                "Choose a validated timestamp through the typed history-navigation API."),
            new(WorkspaceActionId.JumpToHistoryMessage, "Jump to message", WorkspaceActionTargetKind.Channel, target, false,
                "Choose a canonical message anchor through the typed history-navigation API."),
            new(WorkspaceActionId.LoadContextAround, "Load context around latest message", WorkspaceActionTargetKind.Channel, target, canLoadContext,
                canLoadContext ? null : "A supported server-time or msgid anchor is required.")
        ];
    }

    public IReadOnlyList<WorkspaceActionDescriptor> BuildQueryActions(NetworkWorkspace network, QueryView query)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(query);
        var consistent = network.Id == query.NetworkId;
        var registered = consistent && IsRegistered(network);
        var target = new WorkspaceActionTarget(network.Id, query.Id, query.Nickname, query.Nickname);
        var canLoadOlder = registered && _sessions.CanLoadOlderHistory(network, query);
        var canLoadNewer = registered && _sessions.CanLoadNewerHistory(network, query);
        var queryEntries = query.EntriesSnapshot;
        var contextAnchor = queryEntries.Count == 0 ? null : queryEntries[^1];
        var canLoadContext = registered && contextAnchor is not null && _sessions.CanLoadContextAround(network, query, contextAnchor);
        return
        [
            new(WorkspaceActionId.LoadOlderMessages, "Load older messages", WorkspaceActionTargetKind.Query, target, canLoadOlder,
                canLoadOlder ? null : !registered ? "The network is not registered." : "Server history is unavailable or already exhausted."),
            new(WorkspaceActionId.LoadNewerMessages, "Load newer messages", WorkspaceActionTargetKind.Query, target, canLoadNewer,
                canLoadNewer ? null : !registered ? "The network is not registered." : "Newer history is unavailable or already exhausted."),
            new(WorkspaceActionId.ReturnToLatest, "Return to latest", WorkspaceActionTargetKind.Query, target, query.EntryCount > 0,
                query.EntryCount > 0 ? null : "There is no projected history to return to."),
            new(WorkspaceActionId.GoToHistoryTimestamp, "Go to date/time", WorkspaceActionTargetKind.Query, target, false,
                "Choose a validated timestamp through the typed history-navigation API."),
            new(WorkspaceActionId.JumpToHistoryMessage, "Jump to message", WorkspaceActionTargetKind.Query, target, false,
                "Choose a canonical message anchor through the typed history-navigation API."),
            new(WorkspaceActionId.LoadContextAround, "Load context around latest message", WorkspaceActionTargetKind.Query, target, canLoadContext,
                canLoadContext ? null : "A supported server-time or msgid anchor is required.")
        ];
    }

    public async ValueTask<CommandDispatchResult> ExecuteNetworkAsync(
        NetworkWorkspace network,
        WorkspaceActionId action,
        CancellationToken cancellationToken = default)
    {
        var descriptor = BuildNetworkActions(network).FirstOrDefault(item => item.Action == action);
        if (descriptor is null)
        {
            return CommandDispatchResult.Failure("The network action is not supported.", network.StatusView);
        }

        if (!descriptor.IsEnabled)
        {
            return CommandDispatchResult.Failure(descriptor.DisabledReason ?? "The network action is unavailable.", network.StatusView);
        }

        switch (action)
        {
            case WorkspaceActionId.Connect:
                await _sessions.ConnectAsync(network.Id, cancellationToken).ConfigureAwait(false);
                return CommandDispatchResult.Success($"Connecting to {network.DisplayName}.", network.StatusView);
            case WorkspaceActionId.Disconnect:
                await _sessions.DisconnectAsync(network.Id).ConfigureAwait(false);
                return CommandDispatchResult.Success($"Disconnect requested for {network.DisplayName}.", network.StatusView);
            case WorkspaceActionId.Reconnect:
                await _sessions.ReconnectAsync(network.Id, cancellationToken).ConfigureAwait(false);
                return CommandDispatchResult.Success($"Reconnecting to {network.DisplayName}.", network.StatusView);
            case WorkspaceActionId.OpenNetwork:
                _sessions.ActivateView(network.StatusView.Id);
                return CommandDispatchResult.Success($"Opened {network.DisplayName}.", network.StatusView);
            default:
                return CommandDispatchResult.Failure("The network action is not supported.", network.StatusView);
        }
    }

    public async ValueTask<CommandDispatchResult> ExecuteChannelAsync(
        NetworkWorkspace network,
        ChannelView channel,
        WorkspaceActionId action,
        CancellationToken cancellationToken = default)
    {
        var descriptor = BuildChannelActions(network, channel).FirstOrDefault(item => item.Action == action);
        if (descriptor is null)
        {
            return CommandDispatchResult.Failure("The channel action is not supported.", channel);
        }

        if (!descriptor.IsEnabled)
        {
            return CommandDispatchResult.Failure(descriptor.DisabledReason ?? "The channel action is unavailable.", channel);
        }

        switch (action)
        {
            case WorkspaceActionId.OpenChannel:
                _sessions.ActivateView(channel.Id);
                return CommandDispatchResult.Success($"Opened {channel.Channel}.", channel);
            case WorkspaceActionId.JoinChannel:
                await network.Session.JoinChannelAsync(channel.Channel, cancellationToken).ConfigureAwait(false);
                _sessions.ActivateView(channel.Id);
                return CommandDispatchResult.Success($"Joining {channel.Channel}.", channel);
            case WorkspaceActionId.RejoinChannel:
                await network.Session.RejoinChannelAsync(channel.Channel, cancellationToken).ConfigureAwait(false);
                _sessions.ActivateView(channel.Id);
                return CommandDispatchResult.Success($"Rejoining {channel.Channel}.", channel);
            case WorkspaceActionId.PartChannel:
                return await PartChannelAsync(network, channel, null, allowPendingJoinCancellation: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            case WorkspaceActionId.RequestTopic:
                await network.Session.SendCommandAsync("TOPIC", [channel.Channel], cancellationToken: cancellationToken).ConfigureAwait(false);
                return CommandDispatchResult.Success($"Topic requested for {channel.Channel}.", channel);
            case WorkspaceActionId.EditTopic:
                return CommandDispatchResult.Failure("Use the topic editor or /topic <text> to set a topic.", channel);
            case WorkspaceActionId.RequestModes:
                await network.Session.SendCommandAsync("MODE", [channel.Channel], cancellationToken: cancellationToken).ConfigureAwait(false);
                return CommandDispatchResult.Success($"Channel modes requested for {channel.Channel}.", channel);
            case WorkspaceActionId.RefreshNames:
                await network.Session.RequestNamesAsync(channel.Channel, cancellationToken).ConfigureAwait(false);
                return CommandDispatchResult.Success($"Refreshing members for {channel.Channel}.", channel);
            case WorkspaceActionId.LoadOlderMessages:
                return await _sessions.LoadOlderMessagesAsync(network, channel, cancellationToken).ConfigureAwait(false);
            case WorkspaceActionId.LoadNewerMessages:
                return await _sessions.LoadNewerMessagesAsync(network, channel, cancellationToken).ConfigureAwait(false);
            case WorkspaceActionId.ReturnToLatest:
                return ToDispatchResult(await _sessions.ReturnToLatestAsync(network, channel, cancellationToken).ConfigureAwait(false), channel);
            case WorkspaceActionId.LoadContextAround:
                return await _sessions.LoadContextAroundAsync(network, channel, channel.EntriesSnapshot[^1], cancellationToken).ConfigureAwait(false);
            default:
                return CommandDispatchResult.Failure("The channel action is not supported.", channel);
        }
    }

    public async ValueTask<CommandDispatchResult> ExecuteQueryAsync(
        NetworkWorkspace network,
        QueryView query,
        WorkspaceActionId action,
        CancellationToken cancellationToken = default)
    {
        var descriptor = BuildQueryActions(network, query).FirstOrDefault(item => item.Action == action);
        if (descriptor is null)
        {
            return CommandDispatchResult.Failure("The query action is not supported.", query);
        }

        if (!descriptor.IsEnabled)
        {
            return CommandDispatchResult.Failure(descriptor.DisabledReason ?? "The query action is unavailable.", query);
        }

        return action switch
        {
            WorkspaceActionId.LoadOlderMessages => await _sessions.LoadOlderMessagesAsync(network, query, cancellationToken).ConfigureAwait(false),
            WorkspaceActionId.LoadNewerMessages => await _sessions.LoadNewerMessagesAsync(network, query, cancellationToken).ConfigureAwait(false),
            WorkspaceActionId.ReturnToLatest => ToDispatchResult(await _sessions.ReturnToLatestAsync(network, query, cancellationToken).ConfigureAwait(false), query),
            WorkspaceActionId.LoadContextAround => await _sessions.LoadContextAroundAsync(network, query, query.EntriesSnapshot[^1], cancellationToken).ConfigureAwait(false),
            _ => CommandDispatchResult.Failure("The query action is not supported.", query)
        };
    }

    public async ValueTask<CommandDispatchResult> PartChannelAsync(
        NetworkWorkspace network,
        ChannelView channel,
        string? reason = null,
        bool allowPendingJoinCancellation = false,
        CancellationToken cancellationToken = default)
    {
        var descriptor = BuildChannelActions(network, channel).FirstOrDefault(item => item.Action == WorkspaceActionId.PartChannel);
        var pendingJoin = network.Session.Snapshot.DesiredChannels.Any(item => IrcIdentity.Equals(item, channel.Channel, network.Snapshot.Features.CaseMapping));
        if (descriptor is null || !descriptor.IsEnabled && !(allowPendingJoinCancellation && pendingJoin && IsRegistered(network)))
        {
            return CommandDispatchResult.Failure(descriptor?.DisabledReason ?? "The channel action is unavailable.", channel);
        }

        if (reason?.Any(char.IsControl) == true)
        {
            return CommandDispatchResult.Failure("A part reason cannot contain control characters.", channel);
        }

        await network.Session.PartChannelAsync(channel.Channel, string.IsNullOrWhiteSpace(reason) ? null : reason, cancellationToken).ConfigureAwait(false);
        channel.SetLifecycleState(ConversationLifecycleState.Parted);
        return CommandDispatchResult.Success($"Leaving {channel.Channel}.", channel);
    }

    public QueryView OpenQuery(NetworkWorkspace network, string nickname)
    {
        ArgumentNullException.ThrowIfNull(network);
        ValidateTarget(nickname, "nickname");
        var view = _sessions.EnsureQuery(network.Id, nickname);
        _sessions.ActivateView(view.Id);
        _sessions.RecordRecent(network, DestinationKind.Query, nickname);
        return view;
    }

    public async ValueTask<CommandDispatchResult> RequestWhoisAsync(NetworkWorkspace network, string nickname, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", network.StatusView);
        }

        ValidateTarget(nickname, "nickname");
        var request = await _sessions.RequestWhoisAsync(network.Id, nickname, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"WHOIS requested for {nickname}.", request.View);
    }

    public async ValueTask<CommandDispatchResult> SendMessageAsync(
        NetworkWorkspace network,
        string target,
        string text,
        WorkspaceView? preferredView = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", preferredView ?? network.StatusView);
        }

        ValidateTarget(target, "target");
        ValidateText(text, "message");
        var isChannel = IsChannelTarget(network, target);
        WorkspaceView view;
        if (preferredView is { NetworkId: var networkId } candidate
            && networkId == network.Id
            && ((isChannel && candidate is ChannelView channel && IrcIdentity.Equals(channel.Channel, target, network.Snapshot.Features.CaseMapping))
                || (!isChannel && candidate is QueryView query && IrcIdentity.Equals(query.Nickname, target, network.Snapshot.Features.CaseMapping))))
        {
            view = candidate;
        }
        else
        {
            view = isChannel
                ? (WorkspaceView)_sessions.EnsureChannel(network.Id, target)
                : OpenQuery(network, target);
        }

        _sessions.ActivateView(view.Id);
        if (isChannel)
        {
            _sessions.RecordRecent(network, DestinationKind.Channel, target);
        }

        await network.Session.SendCommandAsync("PRIVMSG", [target], text, cancellationToken).ConfigureAwait(false);
        if (!network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.EchoMessage))
        {
            _sessions.AppendLocal(view, IrcEventPresentation.CreateLocalMessage(
                network.Session.Snapshot.Nickname,
                text,
                isChannel ? OutgoingMessageKind.ChannelMessage : OutgoingMessageKind.PrivateMessage));
        }
        return CommandDispatchResult.Success($"Message sent to {target}.", view);
    }

    public bool CanReplyTo(
        NetworkWorkspace network,
        WorkspaceView view,
        TranscriptEntry entry,
        out string? disabledReason)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(entry);
        disabledReason = null;
        if (network.Id != view.NetworkId || view is not (ChannelView or QueryView))
        {
            disabledReason = "The message is not in this network conversation.";
        }
        else if (!IsRegistered(network))
        {
            disabledReason = "The network is not registered.";
        }
        else if (!network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.MessageTags))
        {
            disabledReason = "The server did not negotiate message-tags.";
        }
        else if (network.Snapshot.Features.RuntimeISupport.IsClientTagDenied("+reply")
            || network.Snapshot.Features.RuntimeISupport.IsClientTagDenied("reply"))
        {
            disabledReason = "The server's CLIENTTAGDENY policy disallows replies.";
        }
        else if (view is ChannelView channel
            && (!channel.IsJoined || channel.LifecycleState is ConversationLifecycleState.Parted or ConversationLifecycleState.Kicked or ConversationLifecycleState.Disconnected))
        {
            disabledReason = "Join the channel before replying.";
        }
        else if (view is QueryView query && !query.IsIdentityBoundToCurrentSession)
        {
            disabledReason = "The private-message identity is not bound in the current connection.";
        }
        else if (!IrcReplyReference.TryParse(entry.ServerMessageId, out _))
        {
            disabledReason = "This message has no valid server msgid.";
        }

        return disabledReason is null;
    }

    public bool CanReactTo(
        NetworkWorkspace network,
        WorkspaceView view,
        TranscriptEntry entry,
        bool unreaction,
        out string? disabledReason)
    {
        if (!CanReplyTo(network, view, entry, out disabledReason))
        {
            return false;
        }

        if (!view.EntriesSnapshot.Any(item => string.Equals(item.ServerMessageId, entry.ServerMessageId, StringComparison.Ordinal)))
        {
            disabledReason = "The parent message is not part of the current conversation view.";
            return false;
        }

        var tag = unreaction ? IrcReaction.UnreactTag : IrcReaction.ReactTag;
        var bareTag = unreaction ? "draft/unreact" : "draft/react";
        if (network.Snapshot.Features.RuntimeISupport.IsClientTagDenied(tag)
            || network.Snapshot.Features.RuntimeISupport.IsClientTagDenied(bareTag))
        {
            disabledReason = "The server's CLIENTTAGDENY policy disallows this reaction.";
            return false;
        }

        return true;
    }

    public async ValueTask<CommandDispatchResult> SendReactionAsync(
        NetworkWorkspace network,
        WorkspaceView view,
        TranscriptEntry entry,
        string value,
        bool unreaction = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            IrcReactionCommandBuilder.ValidateReactionValue(value);
        }
        catch (ArgumentException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, view);
        }

        if (!CanReactTo(network, view, entry, unreaction, out var disabledReason))
        {
            return CommandDispatchResult.Failure(disabledReason ?? "Reactions are unavailable for this message.", view);
        }

        var target = view is ChannelView channel ? channel.Channel : ((QueryView)view).Nickname;
        try
        {
            ValidateTarget(target, "reaction target");
            await network.Session.SendReactionAsync(
                target,
                value,
                entry.ServerMessageId!,
                unreaction,
                network.Snapshot.ConnectionGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return CommandDispatchResult.Failure(exception.Message, view);
        }

        if (!network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.EchoMessage))
        {
            var actor = ReactionActorIdentity.ForLocal(network.Id, network.Session.Snapshot.Nickname);
            _sessions.ApplyLocalReaction(view, new ReactionEvent
            {
                NetworkId = network.Id,
                ConversationKey = HistoryConversationKey(view),
                ParentMessageId = entry.ServerMessageId!,
                Value = value,
                Operation = unreaction ? ReactionOperation.Unreact : ReactionOperation.React,
                Actor = actor,
                Timestamp = DateTimeOffset.UtcNow,
                ReceivedAt = DateTimeOffset.UtcNow,
                IsLocal = true
            });
        }

        return CommandDispatchResult.Success(unreaction ? $"Removed reaction from {target}." : $"Reaction sent to {target}.", view);
    }

    public bool TryCreateReplyComposer(
        NetworkWorkspace network,
        WorkspaceView view,
        TranscriptEntry entry,
        out ReplyComposerState? state,
        out string? disabledReason)
    {
        if (!CanReplyTo(network, view, entry, out disabledReason))
        {
            state = null;
            return false;
        }

        var target = view switch
        {
            ChannelView channel => channel.Channel,
            QueryView query => query.Nickname,
            _ => string.Empty
        };
        state = new ReplyComposerState
        {
            NetworkId = network.Id,
            ViewId = view.Id,
            ConversationKey = HistoryConversationKey(view),
            ParentMessageId = entry.ServerMessageId!,
            ParentSender = entry.Sender,
            ParentPreview = ReplyText.BoundedPreview(entry.Text),
            ConnectionGeneration = network.Snapshot.ConnectionGeneration,
            Target = target
        };
        return true;
    }

    public async ValueTask<CommandDispatchResult> SendReplyAsync(
        NetworkWorkspace network,
        WorkspaceView view,
        ReplyComposerState state,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(state);
        ValidateText(text, "reply");
        if (state.NetworkId != network.Id
            || state.ViewId != view.Id
            || view is not (ChannelView or QueryView)
            || !string.Equals(state.ConversationKey, HistoryConversationKey(view), StringComparison.Ordinal)
            || !IrcReplyReference.TryParse(state.ParentMessageId, out var parent))
        {
            return CommandDispatchResult.Failure("The reply composer is no longer scoped to this conversation.", view);
        }

        if (view is ChannelView channel && (!channel.IsJoined || channel.LifecycleState is ConversationLifecycleState.Parted or ConversationLifecycleState.Kicked or ConversationLifecycleState.Disconnected))
        {
            return CommandDispatchResult.Failure("Join the channel before replying.", view);
        }

        if (view is QueryView query && !query.IsIdentityBoundToCurrentSession)
        {
            return CommandDispatchResult.Failure("The private-message identity is not bound in the current connection.", view);
        }

        if (!IsRegistered(network) || !network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.MessageTags))
        {
            return CommandDispatchResult.Failure("Replies are unavailable until the registered server negotiates message-tags.", view);
        }

        var target = view is ChannelView channelView ? channelView.Channel : ((QueryView)view).Nickname;
        try
        {
            await network.Session.SendReplyAsync(
                target,
                text,
                parent!.MessageId,
                network.Snapshot.ConnectionGeneration,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, view);
        }

        if (!network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.EchoMessage))
        {
            _sessions.AppendLocal(
                view,
                IrcEventPresentation.CreateLocalReply(
                    network.Session.Snapshot.Nickname,
                    text,
                    view is ChannelView ? OutgoingMessageKind.ChannelMessage : OutgoingMessageKind.PrivateMessage,
                    parent.MessageId));
        }

        return CommandDispatchResult.Success($"Reply sent to {target}.", view);
    }

    private static string HistoryConversationKey(WorkspaceView view) => view switch
    {
        QueryView query => query.HistoryConversationKey,
        ChannelView channel => ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, channel.Channel),
        _ => string.Empty
    };

    public async ValueTask<CommandDispatchResult> SendNoticeAsync(NetworkWorkspace network, string target, string text, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", network.StatusView);
        }

        ValidateTarget(target, "target");
        ValidateText(text, "NOTICE text");
        var isChannel = IsChannelTarget(network, target);
        WorkspaceView view = isChannel
            ? _sessions.EnsureChannel(network.Id, target)
            : OpenQuery(network, target);
        var command = new IrcCommandBuilder(CommandLimit(network)).Build("NOTICE", [target], text);
        await network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        _sessions.AppendLocal(view, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, text, OutgoingMessageKind.Notice));
        return CommandDispatchResult.Success($"Notice sent to {target}.", view);
    }

    public async ValueTask<CommandDispatchResult> SendActionAsync(NetworkWorkspace network, WorkspaceView activeView, string text, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", activeView);
        }

        var target = activeView switch
        {
            ChannelView channel => channel.Channel,
            QueryView query => query.Nickname,
            _ => null
        };
        if (target is null)
        {
            return CommandDispatchResult.Failure("Select a channel or query before sending an action.", activeView);
        }

        ValidateText(text, "action");
        var command = IrcParticipantCommandBuilder.BuildAction(new IrcCommandBuilder(CommandLimit(network)), target, text);
        await network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        _sessions.AppendLocal(activeView, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, text, OutgoingMessageKind.Action));
        return CommandDispatchResult.Success($"Action sent to {target}.", activeView);
    }

    public async ValueTask<CommandDispatchResult> ChangeNicknameAsync(NetworkWorkspace network, string nickname, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", network.StatusView);
        }

        ValidateTarget(nickname, "nickname");
        await network.Session.SendCommandAsync("NICK", [nickname], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Nickname change requested: {nickname}.", network.StatusView);
    }

    public async ValueTask<CommandDispatchResult> SendAwayAsync(NetworkWorkspace network, string? message, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", network.StatusView);
        }

        if (message?.Any(char.IsControl) == true)
        {
            return CommandDispatchResult.Failure("Away text cannot contain control characters.", network.StatusView);
        }

        await network.Session.SendCommandAsync("AWAY", trailingParameter: string.IsNullOrWhiteSpace(message) ? null : message.Trim(), cancellationToken: cancellationToken).ConfigureAwait(false);
        var detail = string.IsNullOrWhiteSpace(message) ? "Away status cleared." : "Away status set.";
        _sessions.AppendLocal(network.StatusView, IrcEventPresentation.CreateLocalCommand(detail));
        return CommandDispatchResult.Success(detail, network.StatusView);
    }

    public async ValueTask<CommandDispatchResult> SendWhoAsync(NetworkWorkspace network, string? target, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", network.StatusView);
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            ValidateTarget(target, "WHO target");
        }

        await network.Session.SendCommandAsync("WHO", string.IsNullOrWhiteSpace(target) ? null : [target], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success(string.IsNullOrWhiteSpace(target) ? "WHO requested." : $"WHO requested for {target}.", network.StatusView);
    }

    public async ValueTask<CommandDispatchResult> SendNamesAsync(NetworkWorkspace network, string? channel, CancellationToken cancellationToken = default)
    {
        if (!IsRegistered(network))
        {
            return CommandDispatchResult.Failure("The network is not registered.", network.StatusView);
        }

        if (!string.IsNullOrWhiteSpace(channel))
        {
            ValidateTarget(channel, "channel");
        }

        await network.Session.RequestNamesAsync(channel, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success(string.IsNullOrWhiteSpace(channel) ? "NAMES requested." : $"NAMES requested for {channel}.", network.StatusView);
    }

    private static bool IsRegistered(NetworkWorkspace network) =>
        network.Snapshot.Registration == RegistrationState.Registered
        && network.Snapshot.State is not (ServerSessionState.Disconnected or ServerSessionState.Failed or ServerSessionState.ReconnectWaiting);

    private static bool IsChannelTarget(NetworkWorkspace network, string target) =>
        target.Length > 0 && network.Snapshot.Features.ChannelTypes.Contains(target[0]);

    private static int CommandLimit(NetworkWorkspace network) =>
        Math.Max(3, Math.Min(network.Session.MaximumOutboundLineBytes, network.Snapshot.Features.LineLength));

    private static CommandDispatchResult ToDispatchResult(HistoryNavigationResult result, WorkspaceView view) =>
        result.Succeeded
            ? CommandDispatchResult.Success(result.Message, view)
            : CommandDispatchResult.Failure(result.Message, view);

    private static void ValidateTarget(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) || value[0] == ':')
        {
            throw new ArgumentException($"The {name} must be one safe IRC token.", name);
        }
    }

    private static void ValidateText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new ArgumentException($"The {name} is required and cannot contain line breaks or NUL characters.", name);
        }
    }
}
