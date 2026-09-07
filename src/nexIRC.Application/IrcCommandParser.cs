namespace nexIRC.Application;

/// <summary>
/// Parsed slash-command input.  The raw argument tail is retained so command
/// handlers can preserve trailing text instead of reconstructing it from
/// whitespace-split tokens.
/// </summary>
public sealed record ParsedIrcCommand(
    string Name,
    string Arguments,
    IReadOnlyList<string> Tokens)
{
    public bool TryGetToken(int index, out string? token)
    {
        if (index >= 0 && index < Tokens.Count)
        {
            token = Tokens[index];
            return true;
        }

        token = null;
        return false;
    }

    public string? RemainderAfterToken(int tokenCount) =>
        IrcCommandParser.RemainderAfterTokens(Arguments, tokenCount);
}

/// <summary>
/// Small, UI-neutral slash-command parser.  It understands IRC-style
/// optional colon trailing arguments while also supporting the conventional
/// client spelling where the final argument is simply the remaining text.
/// </summary>
public static class IrcCommandParser
{
    public static bool TryParse(string? input, out ParsedIrcCommand? command, out string error)
    {
        command = null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter an IRC command after '/'.";
            return false;
        }

        var line = input.TrimStart();
        if (line[0] != '/')
        {
            error = "IRC commands must start with '/'.";
            return false;
        }

        var commandLine = line[1..].TrimStart();
        if (commandLine.Length == 0)
        {
            error = "Enter an IRC command after '/'.";
            return false;
        }

        var separator = commandLine.IndexOfAny([' ', '\t']);
        var name = (separator < 0 ? commandLine : commandLine[..separator]).Trim();
        if (name.Length == 0 || name.Any(char.IsControl))
        {
            error = "The IRC command name is invalid.";
            return false;
        }

        var arguments = separator < 0 ? string.Empty : commandLine[(separator + 1)..].TrimStart();
        command = new ParsedIrcCommand(name.ToUpperInvariant(), arguments, Tokenize(arguments));
        return true;
    }

    public static ParsedIrcCommand Parse(string input)
    {
        if (!TryParse(input, out var command, out var error))
        {
            throw new ArgumentException(error, nameof(input));
        }

        return command!;
    }

    public static (string? Target, string? Text) SplitTargetAndText(string? arguments)
    {
        var remaining = arguments?.TrimStart() ?? string.Empty;
        if (!TryReadToken(remaining, out var target, out remaining))
        {
            return (null, null);
        }

        remaining = RemoveOptionalColon(remaining.TrimStart());
        return remaining.Length == 0 ? (null, null) : (target, remaining);
    }

    public static string? RemainderAfterTokens(string? arguments, int tokenCount)
    {
        if (tokenCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tokenCount));
        }

        var remaining = arguments?.TrimStart() ?? string.Empty;
        for (var index = 0; index < tokenCount; index++)
        {
            if (!TryReadToken(remaining, out _, out remaining))
            {
                return null;
            }
        }

        remaining = remaining.TrimStart();
        return remaining.Length == 0 ? string.Empty : RemoveOptionalColon(remaining);
    }

    private static List<string> Tokenize(string arguments)
    {
        var tokens = new List<string>();
        var remaining = arguments;
        while (TryReadToken(remaining, out var token, out remaining))
        {
            tokens.Add(token!);
            if (remaining.TrimStart().StartsWith(':'))
            {
                remaining = remaining.TrimStart();
                tokens.Add(RemoveOptionalColon(remaining));
                break;
            }
        }

        return tokens;
    }

    private static bool TryReadToken(string input, out string? token, out string remainder)
    {
        var remaining = input.TrimStart();
        if (remaining.Length == 0)
        {
            token = null;
            remainder = string.Empty;
            return false;
        }

        if (remaining[0] == ':')
        {
            token = RemoveOptionalColon(remaining);
            remainder = string.Empty;
            return true;
        }

        var separator = remaining.IndexOfAny([' ', '\t']);
        if (separator < 0)
        {
            token = remaining;
            remainder = string.Empty;
            return true;
        }

        token = remaining[..separator];
        remainder = remaining[(separator + 1)..];
        return true;
    }

    private static string RemoveOptionalColon(string value) =>
        value.StartsWith(':') ? value[1..] : value;
}
