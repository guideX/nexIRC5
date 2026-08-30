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
                _sessions.AppendLocal(activeView, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, input));
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

        var reason = arguments.StartsWith(channel, StringComparison.OrdinalIgnoreCase)
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
        _sessions.AppendLocal(view, IrcEventPresentation.CreateLocalMessage(network.Session.Snapshot.Nickname, text));
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

        await network.Session.SendCommandAsync("WHOIS", [parts[0]], cancellationToken: cancellationToken).ConfigureAwait(false);
        return CommandDispatchResult.Success($"WHOIS requested for {parts[0]}.", network.StatusView);
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
