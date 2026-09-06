using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;

namespace nexIRC.Application;

public sealed record CommandDispatchResult(bool Succeeded, string Message, WorkspaceView? View = null, IrcOperationResult? Operation = null)
{
    public static CommandDispatchResult Success(
        string message,
        WorkspaceView? view = null,
        IrcOperationResult? operation = null) => new(true, message, view, operation);

    public static CommandDispatchResult Failure(string message, WorkspaceView? view = null) => new(false, message, view);
}

/// <summary>
/// Application-level IRC input handling. The desktop shell delegates a line
/// here instead of embedding protocol decisions in menu or control handlers.
/// </summary>
public sealed class IrcCommandDispatcher
{
    public static IReadOnlyList<string> SupportedCommands { get; } =
    [
        "server", "join", "rejoin", "part", "msg", "query", "q", "nick", "me", "quit",
        "disconnect", "whois", "list", "banlist", "notice", "ctcp", "op", "deop", "voice",
        "devoice", "kick", "ban", "unban", "invite", "mode", "topic", "clear", "close", "raw", "quote", "help"
    ];

    private readonly NetworkSessionManager _sessions;

    public IrcCommandDispatcher(NetworkSessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public static bool IsBuiltInCommand(string command) =>
        SupportedCommands.Contains(command.Trim().TrimStart('/'), StringComparer.OrdinalIgnoreCase);

    public ValueTask<CommandDispatchResult> DispatchAsync(
        NetworkWorkspace? network,
        WorkspaceView? activeView,
        string input,
        CancellationToken cancellationToken = default) =>
        DispatchAsync(network, activeView, input, null, cancellationToken);

    public async ValueTask<CommandDispatchResult> DispatchAsync(
        NetworkWorkspace? network,
        WorkspaceView? activeView,
        string input,
        AliasContext? aliasContext,
        CancellationToken cancellationToken = default)
    {
        if (network is null || activeView is null || string.IsNullOrWhiteSpace(input))
        {
            return CommandDispatchResult.Failure("Select a network view before entering an IRC command.");
        }

        input = input.Trim();
        if (input[0] != '/')
        {
            var target = activeView switch
            {
                ChannelView channel => channel.Channel,
                QueryView query => query.Nickname,
                _ => null
            };
            if (target is null)
            {
                return CommandDispatchResult.Failure("Select a channel or query before sending a message.", activeView);
            }

            try
            {
                await network.Session.SendCommandAsync("PRIVMSG", [target], input, cancellationToken).ConfigureAwait(false);
                _sessions.AppendLocal(activeView, IrcEventPresentation.CreateLocalMessage(
                    network.Session.Snapshot.Nickname,
                    input,
                    activeView is QueryView ? OutgoingMessageKind.PrivateMessage : OutgoingMessageKind.ChannelMessage));
                return CommandDispatchResult.Success($"Message sent to {target}.", activeView);
            }
            catch (InvalidOperationException exception)
            {
                return CommandDispatchResult.Failure(exception.Message, activeView);
            }
        }

        var commandLine = input[1..].TrimStart();
        if (commandLine.Length == 0)
        {
            return CommandDispatchResult.Failure("Enter an IRC command after '/'.", activeView);
        }

        var separator = commandLine.IndexOfAny([' ', '\t']);
        var command = (separator < 0 ? commandLine : commandLine[..separator]).ToUpperInvariant();
        var arguments = separator < 0 ? string.Empty : commandLine[(separator + 1)..].Trim();
        var parts = arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!IsBuiltInCommand(command))
        {
            var expanded = AliasExpander.Expand(
                input,
                _sessions.Configuration?.Aliases ?? Array.Empty<AliasDefinition>(),
                aliasContext ?? AliasContext.From(network, activeView));
            if (!expanded.Succeeded)
            {
                return CommandDispatchResult.Failure(expanded.Error ?? "Alias expansion failed.", activeView);
            }

            if (!string.Equals(expanded.Input, input, StringComparison.Ordinal))
            {
                return await DispatchAsync(network, activeView, expanded.Input, aliasContext, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            switch (command)
            {
                case "SERVER":
                    return await ConnectServerAsync(network, parts, cancellationToken).ConfigureAwait(false);
                case "JOIN":
                    return await JoinAsync(network, parts, cancellationToken).ConfigureAwait(false);
                case "REJOIN":
                    return await RejoinAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "PART":
                    return await PartAsync(network, activeView, arguments, parts, cancellationToken).ConfigureAwait(false);
                case "MSG":
                    return await MessageAsync(network, arguments, cancellationToken).ConfigureAwait(false);
                case "QUERY":
                case "Q":
                    return Query(network, parts);
                case "NICK":
                    return await NickAsync(network, parts, cancellationToken).ConfigureAwait(false);
                case "WHOIS":
                    return await WhoisAsync(network, parts, cancellationToken).ConfigureAwait(false);
                case "LIST":
                    return await ListAsync(network, parts, cancellationToken).ConfigureAwait(false);
                case "BANLIST":
                    return await BanListAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "NOTICE":
                    return await NoticeAsync(network, arguments, cancellationToken).ConfigureAwait(false);
                case "CTCP":
                    return await CtcpAsync(network, arguments, cancellationToken).ConfigureAwait(false);
                case "OP":
                    return await MemberModeAsync(network, activeView, parts, 'o', adding: true, cancellationToken).ConfigureAwait(false);
                case "DEOP":
                    return await MemberModeAsync(network, activeView, parts, 'o', adding: false, cancellationToken).ConfigureAwait(false);
                case "VOICE":
                    return await MemberModeAsync(network, activeView, parts, 'v', adding: true, cancellationToken).ConfigureAwait(false);
                case "DEVOICE":
                    return await MemberModeAsync(network, activeView, parts, 'v', adding: false, cancellationToken).ConfigureAwait(false);
                case "KICK":
                    return await KickAsync(network, activeView, arguments, parts, cancellationToken).ConfigureAwait(false);
                case "BAN":
                    return await BanAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "UNBAN":
                    return await UnbanAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "INVITE":
                    return await InviteAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "MODE":
                    return await ModeAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "TOPIC":
                    return await TopicAsync(network, activeView, arguments, parts, cancellationToken).ConfigureAwait(false);
                case "CLEAR":
                    activeView.ClearEntries();
                    return CommandDispatchResult.Success("Local view cleared.", activeView);
                case "HELP":
                    return Help(network, activeView);
                case "CLOSE":
                    return _sessions.CloseView(activeView.Id)
                        ? CommandDispatchResult.Success("Local view closed.", network.StatusView)
                        : CommandDispatchResult.Failure("The server status view cannot be closed.", activeView);
                case "ME":
                    return await ActionAsync(network, activeView, arguments, cancellationToken).ConfigureAwait(false);
                case "QUIT":
                    await network.Session.DisconnectAsync(string.IsNullOrWhiteSpace(arguments) ? "nexIRC quit" : arguments).ConfigureAwait(false);
                    return CommandDispatchResult.Success("Disconnect requested.", network.StatusView);
                case "DISCONNECT":
                    await _sessions.DisconnectAsync(network.Id).ConfigureAwait(false);
                    return CommandDispatchResult.Success("Disconnect requested.", network.StatusView);
                case "RAW":
                case "QUOTE":
                    if (string.IsNullOrWhiteSpace(arguments))
                    {
                        return CommandDispatchResult.Failure("Usage: /raw <IRC command>", activeView);
                    }

                    await network.Session.SendRawCommandAsync(arguments, cancellationToken).ConfigureAwait(false);
                    _sessions.AppendLocal(network.StatusView, IrcEventPresentation.CreateLocalCommand($"> {IrcSensitiveData.RedactLine(arguments)}"));
                    return CommandDispatchResult.Success("Raw command sent.", activeView);
                default:
                    return CommandDispatchResult.Failure($"Unknown command: /{command.ToLowerInvariant()}.", activeView);
            }
        }
        catch (ArgumentException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, activeView);
        }
        catch (InvalidOperationException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, activeView);
        }
    }

    private async ValueTask<CommandDispatchResult> ConnectServerAsync(NetworkWorkspace current, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /server <host> [port]", current.StatusView);
        }

        var host = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var parsedPort) ? parsedPort : 6697;
        var options = new NetworkConnectionOptions
        {
            DisplayName = host,
            Endpoint = new IrcEndpoint(host, port, port == 6697 || port == 6698),
            Nickname = current.Snapshot.DesiredNickname,
            Username = current.Snapshot.Username,
            RealName = current.Snapshot.RealName,
            Reconnect = current.Options.Reconnect,
            RequestedCapabilities = current.Options.RequestedCapabilities
        };
        var workspace = _sessions.Add(options);
        _sessions.ActivateView(workspace.StatusView.Id);
        await _sessions.ConnectAsync(workspace.Id, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Connecting to {host}:{port}.", workspace.StatusView);
    }

    private async ValueTask<CommandDispatchResult> JoinAsync(NetworkWorkspace network, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /join <#channel>", network.StatusView);
        }

        var view = _sessions.EnsureChannel(network.Id, parts[0]);
        _sessions.ActivateView(view.Id);
        _sessions.RecordRecent(network, DestinationKind.Channel, parts[0]);
        await network.Session.JoinChannelAsync(parts[0], cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Joining {parts[0]}.", view);
    }

    private async ValueTask<CommandDispatchResult> PartAsync(
        NetworkWorkspace network,
        WorkspaceView activeView,
        string arguments,
        string[] parts,
        CancellationToken cancellationToken)
    {
        var channel = activeView is ChannelView activeChannelView ? activeChannelView.Channel : parts.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return CommandDispatchResult.Failure("Usage: /part [#channel] [reason]", activeView);
        }

        var reason = parts.Length > 1
            && IrcIdentity.Equals(parts[0], channel, network.Snapshot.Features.CaseMapping)
            ? arguments[channel.Length..].Trim()
            : string.Empty;
        await network.Session.PartChannelAsync(channel, string.IsNullOrWhiteSpace(reason) ? null : reason, cancellationToken).ConfigureAwait(false);
        if (activeView is ChannelView partedChannel
            && IrcIdentity.Equals(partedChannel.Channel, channel, network.Snapshot.Features.CaseMapping))
        {
            partedChannel.SetLifecycleState(ConversationLifecycleState.Parted);
        }

        return CommandDispatchResult.Success($"Leaving {channel}.", activeView);
    }

    private async ValueTask<CommandDispatchResult> RejoinAsync(NetworkWorkspace network, WorkspaceView activeView, string[] parts, CancellationToken cancellationToken)
    {
        var channel = activeView is ChannelView activeChannel ? activeChannel.Channel : parts.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return CommandDispatchResult.Failure("Usage: /rejoin [#channel]", activeView);
        }

        await _sessions.RejoinChannelAsync(network.Id, channel, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Rejoining {channel}.", _sessions.EnsureChannel(network.Id, channel));
    }

    private async ValueTask<CommandDispatchResult> MessageAsync(NetworkWorkspace network, string arguments, CancellationToken cancellationToken)
    {
        var (target, text) = SplitTargetAndText(arguments);
        if (target is null || text is null)
        {
            return CommandDispatchResult.Failure("Usage: /msg <nickname> <message>", network.StatusView);
        }

        var view = _sessions.EnsureQuery(network.Id, target);
        _sessions.ActivateView(view.Id);
        _sessions.RecordRecent(network, DestinationKind.Query, target);
        await network.Session.SendCommandAsync("PRIVMSG", [target], text, cancellationToken).ConfigureAwait(false);
        _sessions.AppendLocal(view, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, text, OutgoingMessageKind.PrivateMessage));
        return CommandDispatchResult.Success($"Message sent to {target}.", view);
    }

    private CommandDispatchResult Query(NetworkWorkspace network, string[] parts)
    {
        if (parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /query <nickname>", network.StatusView);
        }

        var view = _sessions.EnsureQuery(network.Id, parts[0]);
        _sessions.ActivateView(view.Id);
        _sessions.RecordRecent(network, DestinationKind.Query, parts[0]);
        return CommandDispatchResult.Success($"Query opened for {parts[0]}.", view);
    }

    private async ValueTask<CommandDispatchResult> NickAsync(NetworkWorkspace network, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /nick <nickname>", network.StatusView);
        }

        await network.Session.SendCommandAsync("NICK", [parts[0]], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Nickname change requested: {parts[0]}.", network.StatusView);
    }

    private async ValueTask<CommandDispatchResult> WhoisAsync(NetworkWorkspace network, string[] parts, CancellationToken cancellationToken)
    {
        if (parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /whois <nickname>", network.StatusView);
        }

        var request = await _sessions.RequestWhoisAsync(network.Id, parts[0], cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"WHOIS requested for {parts[0]}.", request.View);
    }

    private async ValueTask<CommandDispatchResult> ListAsync(NetworkWorkspace network, string[] parts, CancellationToken cancellationToken)
    {
        IrcQueryRequestResult request;
        try
        {
            request = await _sessions.RequestChannelListAsync(network.Id, parts, cancellationToken).ConfigureAwait(false);
            return CommandDispatchResult.Success(request.WasCoalesced ? "A channel list request is already in progress." : "Channel list requested.", request.View);
        }
        catch (InvalidOperationException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, network.StatusView);
        }
    }

    private async ValueTask<CommandDispatchResult> BanListAsync(
        NetworkWorkspace network,
        WorkspaceView activeView,
        string[] parts,
        CancellationToken cancellationToken)
    {
        var channel = activeView is ChannelView channelView
            ? channelView.Channel
            : parts.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return CommandDispatchResult.Failure("Usage: /banlist [#channel]", activeView);
        }

        if (network.Channels.FirstOrDefault(item => IrcIdentity.Equals(item.Channel, channel, network.Snapshot.Features.CaseMapping)) is not { IsJoined: true })
        {
            return CommandDispatchResult.Failure("Ban lists are available for joined channels only.", activeView);
        }

        try
        {
            var request = await _sessions.RequestBanListAsync(network.Id, channel, cancellationToken).ConfigureAwait(false);
            return CommandDispatchResult.Success(
                request.WasCoalesced ? "A ban-list request is already in progress." : $"Ban list requested for {channel}.",
                request.View);
        }
        catch (InvalidOperationException exception)
        {
            return CommandDispatchResult.Failure(exception.Message, activeView);
        }
    }

    private async ValueTask<CommandDispatchResult> ActionAsync(
        NetworkWorkspace network,
        WorkspaceView activeView,
        string arguments,
        CancellationToken cancellationToken)
    {
        var target = activeView switch
        {
            ChannelView channel => channel.Channel,
            QueryView query => query.Nickname,
            _ => null
        };
        if (target is null || string.IsNullOrWhiteSpace(arguments))
        {
            return CommandDispatchResult.Failure("Usage: /me <action> in a channel or query.", activeView);
        }

        await network.Session.SendCommandAsync("PRIVMSG", [target], $"\u0001ACTION {arguments}\u0001", cancellationToken).ConfigureAwait(false);
        _sessions.AppendLocal(activeView, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, arguments, isAction: true));
        return CommandDispatchResult.Success($"Action sent to {target}.", activeView);
    }

    private async ValueTask<CommandDispatchResult> NoticeAsync(NetworkWorkspace network, string arguments, CancellationToken cancellationToken)
    {
        var (target, text) = SplitTargetAndText(arguments);
        if (target is null || text is null)
        {
            return CommandDispatchResult.Failure("Usage: /notice <target> <message>", network.StatusView);
        }

        var message = IrcParticipantCommandBuilder.BuildNotice(CommandBuilder(network), target, text);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        var view = _sessions.EnsureQuery(network.Id, target);
        _sessions.ActivateView(view.Id);
        _sessions.AppendLocal(view, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, text, OutgoingMessageKind.Notice));
        return CommandDispatchResult.Success($"Notice sent to {target}.", view);
    }

    private async ValueTask<CommandDispatchResult> CtcpAsync(NetworkWorkspace network, string arguments, CancellationToken cancellationToken)
    {
        var (target, payload) = SplitTargetAndText(arguments);
        if (target is null || payload is null)
        {
            return CommandDispatchResult.Failure("Usage: /ctcp <target> <VERSION|TIME|PING> [data]", network.StatusView);
        }

        var parts = payload.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].ToUpperInvariant();
        if (command is not ("VERSION" or "TIME" or "PING"))
        {
            return CommandDispatchResult.Failure("This phase permits CTCP VERSION, TIME, or PING.", network.StatusView);
        }

        var data = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : string.Empty;
        var view = _sessions.EnsureQuery(network.Id, target);
        _sessions.ActivateView(view.Id);
        var message = IrcParticipantCommandBuilder.BuildCtcp(CommandBuilder(network), target, command, data);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        var ctcp = command + (data.Length == 0 ? string.Empty : $" {data}");
        _sessions.AppendLocal(view, IrcEventPresentation.CreateLocalCtcp(command, data));
        return CommandDispatchResult.Success($"CTCP {command} sent to {target}.", view);
    }

    private async ValueTask<CommandDispatchResult> MemberModeAsync(
        NetworkWorkspace network,
        WorkspaceView activeView,
        string[] parts,
        char fallbackMode,
        bool adding,
        CancellationToken cancellationToken)
    {
        if (activeView is not ChannelView channel || parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Select a channel and provide a nickname.", activeView);
        }

        var grammar = network.Snapshot.Features.Prefix;
        var mode = grammar?.Modes.Contains(fallbackMode) == true ? fallbackMode : default;
        if (mode == default)
        {
            return CommandDispatchResult.Failure("The server did not advertise a suitable member mode.", channel);
        }

        var message = IrcParticipantCommandBuilder.BuildMemberMode(CommandBuilder(network), channel.Channel, mode, adding, parts[0]);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Member mode {(adding ? "added" : "removed")} for {parts[0]}.", channel);
    }

    private async ValueTask<CommandDispatchResult> KickAsync(NetworkWorkspace network, WorkspaceView activeView, string arguments, string[] parts, CancellationToken cancellationToken)
    {
        if (activeView is not ChannelView channel || parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Select a channel and provide a nickname.", activeView);
        }

        var reason = arguments.Length > parts[0].Length ? arguments[parts[0].Length..].Trim() : string.Empty;
        var message = IrcParticipantCommandBuilder.BuildKick(CommandBuilder(network), channel.Channel, parts[0], reason);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Kick requested for {parts[0]}.", channel);
    }

    private async ValueTask<CommandDispatchResult> BanAsync(NetworkWorkspace network, WorkspaceView activeView, string[] parts, CancellationToken cancellationToken)
    {
        if (activeView is not ChannelView channel || parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /ban <mask>", activeView);
        }

        var mask = parts[0];
        if (!ParticipantActionService.IsValidBanMask(mask))
        {
            return CommandDispatchResult.Failure("Enter a non-empty ban mask without whitespace or control characters.", channel);
        }

        var mode = network.Snapshot.Features.ChannelModes?.ListModes.FirstOrDefault() ?? 'b';
        var message = IrcParticipantCommandBuilder.BuildBan(CommandBuilder(network), channel.Channel, mask, mode);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Ban requested for {mask}.", channel);
    }

    private async ValueTask<CommandDispatchResult> UnbanAsync(NetworkWorkspace network, WorkspaceView activeView, string[] parts, CancellationToken cancellationToken)
    {
        if (activeView is not ChannelView channel || parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Usage: /unban <mask>", activeView);
        }

        var mask = parts[0];
        if (!ParticipantActionService.IsValidBanMask(mask))
        {
            return CommandDispatchResult.Failure("Enter a non-empty ban mask without whitespace or control characters.", channel);
        }

        var mode = network.Snapshot.Features.ChannelModes?.ListModes.FirstOrDefault() ?? 'b';
        var message = IrcParticipantCommandBuilder.BuildUnban(CommandBuilder(network), channel.Channel, mask, mode);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Unban requested for {mask}.", channel);
    }

    private async ValueTask<CommandDispatchResult> InviteAsync(NetworkWorkspace network, WorkspaceView activeView, string[] parts, CancellationToken cancellationToken)
    {
        if (activeView is not ChannelView channel || parts.Length == 0 || !channel.IsJoined)
        {
            return CommandDispatchResult.Failure("Usage: /invite <nickname> from a joined channel.", activeView);
        }

        var targetChannel = parts.Length > 1 ? parts[1] : channel.Channel;
        var message = IrcParticipantCommandBuilder.BuildInvite(CommandBuilder(network), parts[0], targetChannel);
        await network.Session.SendCommandAsync(message, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Invite requested for {parts[0]} to {targetChannel}.", channel);
    }

    private async ValueTask<CommandDispatchResult> ModeAsync(NetworkWorkspace network, WorkspaceView activeView, string[] parts, CancellationToken cancellationToken)
    {
        var channel = activeView as ChannelView ?? (parts.Length > 0 ? network.Channels.FirstOrDefault(item => IrcIdentity.Equals(item.Channel, parts[0], network.Snapshot.Features.CaseMapping)) : null);
        if (channel is null)
        {
            return CommandDispatchResult.Failure("Usage: /mode [#channel] [modes]", activeView);
        }

        var parameters = parts.Length > 0 && IrcIdentity.Equals(parts[0], channel.Channel, network.Snapshot.Features.CaseMapping)
            ? parts.Skip(1).ToArray()
            : parts;
        if (parameters.Length == 0)
        {
            return await new ChannelActionService(_sessions).RequestCurrentModesAsync(network, channel, cancellationToken).ConfigureAwait(false);
        }

        if (parameters[0].Length == 2 && parameters[0][0] is '+' or '-' && char.IsLetter(parameters[0][1]))
        {
            var mode = parameters[0][1];
            var adding = parameters[0][0] == '+';
            var grammar = network.Snapshot.Features.ChannelModes ?? IrcChannelModeGrammar.Default;
            var kind = grammar.ListModes.Contains(mode) ? IrcChannelModeKind.List
                : grammar.ParameterAlwaysModes.Contains(mode) ? IrcChannelModeKind.ParameterAlways
                : grammar.ParameterWhenSetModes.Contains(mode) ? IrcChannelModeKind.ParameterWhenSet
                : grammar.NoParameterModes.Contains(mode) ? IrcChannelModeKind.NoParameter
                : IrcChannelModeKind.Unknown;
            if (kind == IrcChannelModeKind.NoParameter)
            {
                return await new ChannelActionService(_sessions).SetFlagModeAsync(network, channel, mode, adding, cancellationToken).ConfigureAwait(false);
            }

            if (kind is IrcChannelModeKind.ParameterAlways or IrcChannelModeKind.ParameterWhenSet)
            {
                return await new ChannelActionService(_sessions).SetParameterizedModeAsync(network, channel, mode, adding, parameters.Skip(1).FirstOrDefault(), cancellationToken).ConfigureAwait(false);
            }
        }

        return CommandDispatchResult.Failure("Only one safely modeled channel mode may be changed from this command surface.", channel);
    }

    private async ValueTask<CommandDispatchResult> TopicAsync(NetworkWorkspace network, WorkspaceView activeView, string arguments, string[] parts, CancellationToken cancellationToken)
    {
        var channel = activeView as ChannelView ?? (parts.Length > 0 ? network.Channels.FirstOrDefault(item => IrcIdentity.Equals(item.Channel, parts[0], network.Snapshot.Features.CaseMapping)) : null);
        if (channel is null)
        {
            return CommandDispatchResult.Failure("Usage: /topic [#channel]", activeView);
        }

        var channelToken = parts.FirstOrDefault(item => IrcIdentity.Equals(item, channel.Channel, network.Snapshot.Features.CaseMapping));
        var topic = channelToken is not null && parts.Length > 1
            ? arguments[(arguments.IndexOf(channelToken, StringComparison.Ordinal) + channelToken.Length)..].TrimStart()
            : channelToken is null && activeView is ChannelView && parts.Length > 0
                ? arguments
                : null;
        if (topic?.StartsWith(':') == true)
        {
            topic = topic[1..];
        }

        if (topic is null)
        {
            await network.Session.SendCommandAsync("TOPIC", [channel.Channel], cancellationToken: cancellationToken).ConfigureAwait(false);
            return CommandDispatchResult.Success($"Topic requested for {channel.Channel}.", channel);
        }

        return await new ChannelActionService(_sessions).EditTopicAsync(network, channel, topic, cancellationToken).ConfigureAwait(false);
    }

    private static (string? Target, string? Text) SplitTargetAndText(string arguments)
    {
        var separator = arguments.IndexOfAny([' ', '\t']);
        if (separator <= 0 || separator == arguments.Length - 1)
        {
            return (null, null);
        }

        var target = arguments[..separator];
        var text = arguments[(separator + 1)..].Trim();
        return string.IsNullOrWhiteSpace(text) ? (null, null) : (target, text);
    }

    private static IrcCommandBuilder CommandBuilder(NetworkWorkspace network)
    {
        var maximum = Math.Min(network.Session.MaximumOutboundLineBytes, network.Snapshot.Features.LineLength);
        return new IrcCommandBuilder(Math.Max(3, maximum));
    }

    private CommandDispatchResult Help(NetworkWorkspace network, WorkspaceView activeView)
    {
        var aliases = _sessions.Configuration?.Aliases.Where(alias => alias.IsEnabled).OrderBy(alias => alias.Name, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var text = aliases.Length == 0
            ? "Built-in commands: " + string.Join(", ", SupportedCommands.Select(command => "/" + command))
            : "Built-in commands: " + string.Join(", ", SupportedCommands.Select(command => "/" + command))
                + " · User aliases: " + string.Join(", ", aliases.Select(alias => $"/{alias.Name} → {alias.Expansion}"));
        _sessions.AppendLocal(network.StatusView, IrcEventPresentation.CreateLocalCommand(text));
        return CommandDispatchResult.Success(text, activeView);
    }
}
