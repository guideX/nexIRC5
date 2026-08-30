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

                ParseTags(rawLine[(index + 1)..tagEnd], tags);
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

    private static void ParseTags(string rawTags, List<IrcMessageTag> tags)
    {
        foreach (var rawTag in rawTags.Split(';'))
        {
            if (rawTag.Length == 0)
            {
                throw new FormatException("The IRC tag section contained an empty tag.");
            }

            var equals = rawTag.IndexOf('=');
            var key = equals >= 0 ? rawTag[..equals] : rawTag;
            if (key.Length == 0)
            {
                throw new FormatException("An IRC message tag had an empty key.");
            }

            var rawValue = equals >= 0 ? rawTag[(equals + 1)..] : null;
            tags.Add(new IrcMessageTag(rawTag, key, rawValue, Unescape(rawValue)));
        }
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

            builder.Append(value[++index] switch
            {
                ':' => ';',
                's' => ' ',
                'r' => '\r',
                'n' => '\n',
                '\\' => '\\',
                _ => value[index]
            });
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
