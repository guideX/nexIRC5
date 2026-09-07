namespace nexIRC.Core.Protocol;

public static class IrcMessageParser
{
    public static IrcParseResult Parse(string rawLine)
    {
        if (rawLine is null)
        {
            return IrcParseResult.FromError("The IRC line was null.");
        }

        try
        {
            var index = 0;
            IrcPrefix? prefix = null;
            var tags = new List<IrcMessageTag>();

            if (index < rawLine.Length && rawLine[index] == '@')
            {
                var tagEnd = rawLine.IndexOf(' ', index);
                if (tagEnd < 0)
                {
                    return IrcParseResult.FromError("A tag section must be followed by a command.");
                }

                if (!TryParseTags(rawLine[(index + 1)..tagEnd], tags, out var tagError))
                {
                    return IrcParseResult.FromError(tagError ?? "The IRC tag section was malformed.");
                }
                index = tagEnd;
            }

            SkipSpaces(rawLine, ref index);
            if (index < rawLine.Length && rawLine[index] == ':')
            {
                var prefixEnd = rawLine.IndexOf(' ', index);
                if (prefixEnd < 0)
                {
                    return IrcParseResult.FromError("A prefix must be followed by a command.");
                }

                prefix = IrcPrefix.Parse(rawLine[(index + 1)..prefixEnd]);
                index = prefixEnd;
                SkipSpaces(rawLine, ref index);
            }

            var commandStart = index;
            while (index < rawLine.Length && rawLine[index] != ' ')
            {
                index++;
            }

            if (index == commandStart)
            {
                return IrcParseResult.FromError("The IRC line did not contain a command.");
            }

            var rawCommand = rawLine[commandStart..index];
            var middle = new List<string>();
            var hasTrailing = false;
            string? trailing = null;
            while (index < rawLine.Length)
            {
                SkipSpaces(rawLine, ref index);
                if (index >= rawLine.Length)
                {
                    break;
                }

                if (rawLine[index] == ':')
                {
                    hasTrailing = true;
                    trailing = rawLine[(index + 1)..];
                    break;
                }

                var parameterStart = index;
                while (index < rawLine.Length && rawLine[index] != ' ')
                {
                    index++;
                }

                middle.Add(rawLine[parameterStart..index]);
            }

            return IrcParseResult.FromMessage(new IrcMessage(
                rawLine,
                tags.AsReadOnly(),
                prefix,
                rawCommand,
                middle.AsReadOnly(),
                hasTrailing,
                trailing));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return IrcParseResult.FromError(exception.Message);
        }
    }

    private static bool TryParseTags(string rawTags, List<IrcMessageTag> tags, out string? error)
    {
        error = null;
        if (rawTags.Length == 0)
        {
            error = "The IRC tag section was empty.";
            return false;
        }

        foreach (var rawTag in rawTags.Split(';'))
        {
            if (rawTag.Length == 0)
            {
                error = "The IRC tag section contained an empty tag.";
                return false;
            }

            var equals = rawTag.IndexOf('=');
            var key = equals >= 0 ? rawTag[..equals] : rawTag;
            if (key.Length == 0 || key.Any(static character => character is ';' or ' ' or '\r' or '\n' or '\0'))
            {
                error = "An IRC message tag had an invalid key.";
                return false;
            }

            var rawValue = equals >= 0 ? rawTag[(equals + 1)..] : null;
            tags.Add(new IrcMessageTag(rawTag, key, rawValue, Unescape(rawValue)));
        }

        return true;
    }

    private static string? Unescape(string? value)
    {
        if (value is null || !value.Contains('\\'))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            var escaped = value[++index];
            switch (escaped)
            {
                case ':':
                    builder.Append(';');
                    break;
                case 's':
                    builder.Append(' ');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case '\\':
                    builder.Append('\\');
                    break;
                default:
                    // Preserve an unknown escape literally.  This keeps a
                    // future tag value round-trippable and, importantly,
                    // prevents malformed metadata from shifting the command.
                    builder.Append('\\');
                    builder.Append(escaped);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void SkipSpaces(string input, ref int index)
    {
        while (index < input.Length && input[index] == ' ')
        {
            index++;
        }
    }
}
