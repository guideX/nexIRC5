using System.Globalization;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public sealed class ChannelActionService
{
    private readonly NetworkSessionManager _sessions;

    public ChannelActionService(NetworkSessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public ChannelAuthority GetAuthority(NetworkWorkspace network, ChannelView channel, ChannelMemberView? target = null) =>
        ChannelAuthority.Evaluate(network, channel, target);

    public async ValueTask<CommandDispatchResult> RequestCurrentModesAsync(
        NetworkWorkspace network,
        ChannelView channel,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseChannel(network, channel, out var failure))
        {
            return failure!;
        }

        await network.Session.SendCommandAsync("MODE", [channel.Channel], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Channel modes requested for {channel.Channel}.", channel);
    }

    public async ValueTask<CommandDispatchResult> SetFlagModeAsync(
        NetworkWorkspace network,
        ChannelView channel,
        char mode,
        bool adding,
        CancellationToken cancellationToken = default)
    {
        if (!CanEditModes(network, channel, mode, IrcChannelModeKind.NoParameter, out var failure))
        {
            return failure!;
        }

        var command = IrcChannelCommandBuilder.BuildFlagMode(CommandBuilder(network), channel.Channel, mode, adding);
        var operation = _sessions.StartOperation(
            IrcOperationType.ModeChange,
            network.Id,
            channel.Channel,
            requestedMode: $"{(adding ? '+' : '-')}{mode}",
            command: "MODE");
        try
        {
            await network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _sessions.CancelOperation(network.Id, operation.Id, IrcOperationState.Cancelled, "The channel-mode request could not be sent.");
            throw;
        }

        return CommandDispatchResult.Success($"Channel mode {(adding ? "enable" : "disable")} requested for {channel.Channel}; waiting for server confirmation.", channel, operation);
    }

    public async ValueTask<CommandDispatchResult> SetParameterizedModeAsync(
        NetworkWorkspace network,
        ChannelView channel,
        char mode,
        bool adding,
        string? parameter,
        CancellationToken cancellationToken = default)
    {
        var grammar = network.Snapshot.Features.ChannelModes ?? IrcChannelModeGrammar.Default;
        var kind = KindOf(grammar, mode);
        if (kind is not (IrcChannelModeKind.ParameterAlways or IrcChannelModeKind.ParameterWhenSet))
        {
            return CommandDispatchResult.Failure($"Mode {mode} is not a modeled parameter mode.", channel);
        }

        if (!CanUseChannel(network, channel, out var lifecycleFailure))
        {
            return lifecycleFailure!;
        }

        var authority = ChannelAuthority.Evaluate(network, channel).ChangeChannelModes;
        if (!authority.IsAllowed)
        {
            return CommandDispatchResult.Failure($"Channel-mode authority is {authority.StateText}: {authority.Reason}", channel);
        }

        if (adding)
        {
            if (string.IsNullOrWhiteSpace(parameter) || parameter.Any(char.IsWhiteSpace) || parameter.Any(char.IsControl))
            {
                return CommandDispatchResult.Failure($"Mode {mode} requires one safe parameter.", channel);
            }

            if (mode == 'l' && (!int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit) || limit is < 1 or > 1_000_000))
            {
                return CommandDispatchResult.Failure("The channel user limit must be an integer from 1 to 1,000,000.", channel);
            }
        }

        var command = adding
            ? IrcChannelCommandBuilder.BuildParameterizedMode(CommandBuilder(network), channel.Channel, mode, true, parameter!)
            : IrcChannelCommandBuilder.BuildFlagMode(CommandBuilder(network), channel.Channel, mode, false);
        var operation = _sessions.StartOperation(
            IrcOperationType.ModeChange,
            network.Id,
            channel.Channel,
            requestedMode: $"{(adding ? '+' : '-')}{mode}",
            command: "MODE");
        try
        {
            await network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _sessions.CancelOperation(network.Id, operation.Id, IrcOperationState.Cancelled, "The channel-mode request could not be sent.");
            throw;
        }

        var detail = mode == 'k' ? " (key value is not retained by nexIRC)" : string.Empty;
        return CommandDispatchResult.Success($"Channel mode {(adding ? "set" : "cleared")} requested for {channel.Channel}{detail}; waiting for server confirmation.", channel, operation);
    }

    public async ValueTask<CommandDispatchResult> EditTopicAsync(
        NetworkWorkspace network,
        ChannelView channel,
        string topic,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(topic);
        if (!CanUseChannel(network, channel, out var lifecycleFailure))
        {
            return lifecycleFailure!;
        }

        if (topic.Any(char.IsControl))
        {
            return CommandDispatchResult.Failure("A topic cannot contain control characters or line breaks.", channel);
        }

        var authority = ChannelAuthority.Evaluate(network, channel).ChangeTopic;
        if (!authority.IsAllowed)
        {
            return CommandDispatchResult.Failure($"Topic authority is {authority.StateText}: {authority.Reason}", channel);
        }

        var command = IrcChannelCommandBuilder.BuildTopic(CommandBuilder(network), channel.Channel, topic);
        var operation = _sessions.StartOperation(IrcOperationType.TopicChange, network.Id, channel.Channel, command: "TOPIC");
        try
        {
            await network.Session.SendCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _sessions.CancelOperation(network.Id, operation.Id, IrcOperationState.Cancelled, "The topic request could not be sent.");
            throw;
        }

        return CommandDispatchResult.Success($"Topic change requested for {channel.Channel}; waiting for server confirmation.", channel, operation);
    }

    public async ValueTask<CommandDispatchResult> OpenBanListAsync(
        NetworkWorkspace network,
        ChannelView channel,
        CancellationToken cancellationToken = default)
    {
        if (!CanUseChannel(network, channel, out var failure))
        {
            return failure!;
        }

        try
        {
            var request = await _sessions.RequestBanListAsync(network.Id, channel.Channel, cancellationToken).ConfigureAwait(false);
            return CommandDispatchResult.Success(
                request.WasCoalesced ? "A ban-list request is already in progress." : $"Ban list requested for {channel.Channel}.",
                request.View);
        }
        catch (InvalidOperationException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, channel);
        }
    }

    private static bool CanUseChannel(NetworkWorkspace network, ChannelView channel, out CommandDispatchResult? failure)
    {
        if (network.Id != channel.NetworkId)
        {
            failure = CommandDispatchResult.Failure("The channel belongs to another network.", channel);
            return false;
        }

        var snapshot = network.Snapshot;
        if (snapshot.Registration != RegistrationState.Registered
            || snapshot.State is ServerSessionState.Disconnected or ServerSessionState.Failed or ServerSessionState.ReconnectWaiting)
        {
            failure = CommandDispatchResult.Failure("The network is not registered.", channel);
            return false;
        }

        if (!channel.IsJoined)
        {
            failure = CommandDispatchResult.Failure("You are not joined to this channel.", channel);
            return false;
        }

        failure = null;
        return true;
    }

    private static bool CanEditModes(
        NetworkWorkspace network,
        ChannelView channel,
        char mode,
        IrcChannelModeKind expectedKind,
        out CommandDispatchResult? failure)
    {
        if (!CanUseChannel(network, channel, out failure))
        {
            return false;
        }

        var grammar = network.Snapshot.Features.ChannelModes ?? IrcChannelModeGrammar.Default;
        if (KindOf(grammar, mode) != expectedKind)
        {
            failure = CommandDispatchResult.Failure($"Mode {mode} is not a modeled flag mode.", channel);
            return false;
        }

        var authority = ChannelAuthority.Evaluate(network, channel).ChangeChannelModes;
        if (!authority.IsAllowed)
        {
            failure = CommandDispatchResult.Failure($"Channel-mode authority is {authority.StateText}: {authority.Reason}", channel);
            return false;
        }

        return true;
    }

    private static IrcChannelModeKind KindOf(IrcChannelModeGrammar grammar, char mode) =>
        grammar.ListModes.Contains(mode) ? IrcChannelModeKind.List
        : grammar.ParameterAlwaysModes.Contains(mode) ? IrcChannelModeKind.ParameterAlways
        : grammar.ParameterWhenSetModes.Contains(mode) ? IrcChannelModeKind.ParameterWhenSet
        : grammar.NoParameterModes.Contains(mode) ? IrcChannelModeKind.NoParameter
        : IrcChannelModeKind.Unknown;

    private static IrcCommandBuilder CommandBuilder(NetworkWorkspace network) =>
        new(Math.Max(3, Math.Min(network.Session.MaximumOutboundLineBytes, network.Snapshot.Features.LineLength)));
}

public sealed class ChannelPropertiesViewModel : ObservableObject
{
    private readonly ChannelActionService _actions;
    private string _topicDraft;
    private IReadOnlyList<ChannelModeProjection> _modes;

    public ChannelPropertiesViewModel(NetworkSessionManager sessions, NetworkWorkspace network, ChannelView channel)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        Network = network ?? throw new ArgumentNullException(nameof(network));
        Channel = channel ?? throw new ArgumentNullException(nameof(channel));
        if (network.Id != channel.NetworkId)
        {
            throw new ArgumentException("The channel must belong to the supplied network.", nameof(channel));
        }

        _actions = new ChannelActionService(sessions);
        _topicDraft = channel.Topic ?? string.Empty;
        _modes = channel.ModeProjections;
    }

    public NetworkWorkspace Network { get; }

    public ChannelView Channel { get; }

    public string NetworkName => Network.NetworkName ?? Network.DisplayName;

    public string ChannelName => Channel.Channel;

    public string TopicText => Channel.TopicText;

    public string TopicSetterText => Channel.TopicMetadataText;

    public string ApparentPrivilegeText => ChannelAuthority.Evaluate(Network, Channel).CurrentUserPrivilege ?? "unknown/server-dependent";

    public string AuthorityText => ChannelAuthority.Evaluate(Network, Channel).ChangeChannelModes.StateText;

    public bool IsReadOnly => !ChannelAuthority.Evaluate(Network, Channel).ChangeChannelModes.IsAllowed;

    public bool IsJoined => Channel.IsJoined;

    public string TopicDraft
    {
        get => _topicDraft;
        set => SetProperty(ref _topicDraft, value);
    }

    public IReadOnlyList<ChannelModeProjection> Modes
    {
        get => _modes;
        private set => SetProperty(ref _modes, value);
    }

    public string CreationTimeText => "unknown";

    public void Refresh()
    {
        Modes = Channel.ModeProjections;
        OnPropertyChanged(nameof(TopicText));
        OnPropertyChanged(nameof(TopicSetterText));
        OnPropertyChanged(nameof(ApparentPrivilegeText));
        OnPropertyChanged(nameof(AuthorityText));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(IsJoined));
    }

    public ValueTask<CommandDispatchResult> SetFlagModeAsync(char mode, bool adding, CancellationToken cancellationToken = default) =>
        _actions.SetFlagModeAsync(Network, Channel, mode, adding, cancellationToken);

    public ValueTask<CommandDispatchResult> SetParameterizedModeAsync(char mode, bool adding, string? parameter, CancellationToken cancellationToken = default) =>
        _actions.SetParameterizedModeAsync(Network, Channel, mode, adding, parameter, cancellationToken);

    public ValueTask<CommandDispatchResult> SaveTopicAsync(CancellationToken cancellationToken = default) =>
        _actions.EditTopicAsync(Network, Channel, TopicDraft, cancellationToken);

    public ValueTask<CommandDispatchResult> OpenBanListAsync(CancellationToken cancellationToken = default) =>
        _actions.OpenBanListAsync(Network, Channel, cancellationToken);
}
