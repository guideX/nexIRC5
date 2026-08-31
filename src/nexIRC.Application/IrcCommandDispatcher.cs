using nexIRC.Core.Networking;
using nexIRC.Core.Session;

namespace nexIRC.Application;

public sealed record CommandDispatchResult(bool Succeeded, string Message, WorkspaceView? View = null)
{
    public static CommandDispatchResult Success(string message, WorkspaceView? view = null) => new(true, message, view);

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
        "server", "join", "part", "msg", "query", "q", "nick", "me", "quit",
        "disconnect", "whois", "list", "notice", "ctcp", "op", "deop", "voice",
        "devoice", "kick", "mode", "topic", "clear", "close", "raw", "quote"
    ];

    private readonly NetworkSessionManager _sessions;

    public IrcCommandDispatcher(NetworkSessionManager sessions)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    public async ValueTask<CommandDispatchResult> DispatchAsync(
        NetworkWorkspace? network,
        WorkspaceView? activeView,
        string input,
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

        try
        {
            switch (command)
            {
                case "SERVER":
                    return await ConnectServerAsync(network, parts, cancellationToken).ConfigureAwait(false);
                case "JOIN":
                    return await JoinAsync(network, parts, cancellationToken).ConfigureAwait(false);
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
                case "MODE":
                    return await ModeAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "TOPIC":
                    return await TopicAsync(network, activeView, parts, cancellationToken).ConfigureAwait(false);
                case "CLEAR":
                    activeView.ClearEntries();
                    return CommandDispatchResult.Success("Local view cleared.", activeView);
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
                    _sessions.AppendLocal(network.StatusView, IrcEventPresentation.CreateLocalCommand($"> {arguments}"));
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
        var channel = activeView is ChannelView activeChannel ? activeChannel.Channel : parts.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(channel))
        {
            return CommandDispatchResult.Failure("Usage: /part [#channel] [reason]", activeView);
        }

        var typedChannel = arguments.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        var reason = typedChannel is not null
            && IrcIdentity.Equals(typedChannel, channel, network.Snapshot.Features.CaseMapping)
            ? arguments[channel.Length..].Trim()
            : string.Empty;
        await network.Session.PartChannelAsync(channel, string.IsNullOrWhiteSpace(reason) ? null : reason, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Leaving {channel}.", activeView);
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

        var view = _sessions.EnsureQuery(network.Id, target);
        _sessions.ActivateView(view.Id);
        await network.Session.SendCommandAsync("NOTICE", [target], text, cancellationToken).ConfigureAwait(false);
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
        var ctcp = command + (data.Length == 0 ? string.Empty : $" {data}");
        await network.Session.SendCommandAsync("PRIVMSG", [target], $"\u0001{ctcp}\u0001", cancellationToken).ConfigureAwait(false);
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
        var mode = fallbackMode == 'v'
            ? grammar is { Modes.Count: > 0 } ? grammar.Modes[^1] : fallbackMode
            : grammar?.Modes.FirstOrDefault(candidate => candidate == 'o') ?? (grammar is { Modes.Count: > 1 } ? grammar.Modes.Take(grammar.Modes.Count - 1).FirstOrDefault() : default);
        if (mode == default)
        {
            return CommandDispatchResult.Failure("The server did not advertise a suitable member mode.", channel);
        }

        await network.Session.SendCommandAsync("MODE", [channel.Channel, $"{(adding ? '+' : '-')}{mode}", parts[0]], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Member mode {(adding ? "added" : "removed")} for {parts[0]}.", channel);
    }

    private async ValueTask<CommandDispatchResult> KickAsync(NetworkWorkspace network, WorkspaceView activeView, string arguments, string[] parts, CancellationToken cancellationToken)
    {
        if (activeView is not ChannelView channel || parts.Length == 0)
        {
            return CommandDispatchResult.Failure("Select a channel and provide a nickname.", activeView);
        }

        var reason = arguments.Length > parts[0].Length ? arguments[parts[0].Length..].Trim() : string.Empty;
        await network.Session.SendCommandAsync("KICK", [channel.Channel, parts[0]], string.IsNullOrWhiteSpace(reason) ? null : reason, cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Kick requested for {parts[0]}.", channel);
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
        await network.Session.SendCommandAsync("MODE", new[] { channel.Channel }.Concat(parameters).ToArray(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Mode requested for {channel.Channel}.", channel);
    }

    private async ValueTask<CommandDispatchResult> TopicAsync(NetworkWorkspace network, WorkspaceView activeView, string[] parts, CancellationToken cancellationToken)
    {
        var channel = activeView as ChannelView ?? (parts.Length > 0 ? network.Channels.FirstOrDefault(item => IrcIdentity.Equals(item.Channel, parts[0], network.Snapshot.Features.CaseMapping)) : null);
        if (channel is null)
        {
            return CommandDispatchResult.Failure("Usage: /topic [#channel]", activeView);
        }

        await network.Session.SendCommandAsync("TOPIC", [channel.Channel], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"Topic requested for {channel.Channel}.", channel);
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
}
