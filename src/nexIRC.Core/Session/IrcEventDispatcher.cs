using nexIRC.Core.Protocol;

namespace nexIRC.Core.Session;

public sealed class IrcEventDispatcher
{
    private readonly Dictionary<string, Func<IrcMessage, IrcSemanticEvent>> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Func<IrcMessage, IrcSemanticEvent>> _numerics = [];

    public void RegisterCommand(string command, Func<IrcMessage, IrcSemanticEvent> handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentNullException.ThrowIfNull(handler);
        _commands[command.ToUpperInvariant()] = handler;
    }

    public void RegisterNumeric(int numeric, Func<IrcMessage, IrcSemanticEvent> handler)
    {
        if (numeric is < 0 or > 999)
        {
            throw new ArgumentOutOfRangeException(nameof(numeric));
        }

        ArgumentNullException.ThrowIfNull(handler);
        _numerics[numeric] = handler;
    }

    public IrcSemanticEvent Dispatch(IrcMessage message)
    {
        if (message.NumericCommand is int numeric)
        {
            return _numerics.TryGetValue(numeric, out var numericHandler)
                ? numericHandler(message)
                : new IrcUnknownNumericEvent(message, numeric);
        }

        return _commands.TryGetValue(message.Command, out var commandHandler)
            ? commandHandler(message)
            : new IrcUnknownCommandEvent(message);
    }

    public bool TryDispatch(IrcMessage message, out IrcSemanticEvent? semanticEvent)
    {
        if (message.NumericCommand is int numeric && _numerics.TryGetValue(numeric, out var numericHandler))
        {
            semanticEvent = numericHandler(message);
            return true;
        }

        if (message.NumericCommand is null && _commands.TryGetValue(message.Command, out var commandHandler))
        {
            semanticEvent = commandHandler(message);
            return true;
        }

        semanticEvent = null;
        return false;
    }
}
