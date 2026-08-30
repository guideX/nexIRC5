using System.Text;

namespace nexIRC.Core.Protocol;

public sealed class IrcOutboundMessage
{
    internal IrcOutboundMessage(string line, byte[] framedBytes)
    {
        Line = line;
        FramedBytes = framedBytes;
    }

    public string Line { get; }

    public ReadOnlyMemory<byte> FramedBytes { get; }
}

public sealed class IrcCommandBuilder
{
    public const int DefaultMaximumLineBytes = 512;

    private readonly int _maximumLineBytes;

    public IrcCommandBuilder(int maximumLineBytes = DefaultMaximumLineBytes)
    {
        if (maximumLineBytes < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLineBytes), maximumLineBytes, "The outbound line limit must allow at least a command and CRLF.");
        }

        _maximumLineBytes = maximumLineBytes;
    }

    public int MaximumLineBytes => _maximumLineBytes;

    public IrcOutboundMessage Build(string command, IReadOnlyList<string>? middleParameters = null, string? trailingParameter = null)
    {
        ValidateToken(command, nameof(command), allowLeadingColon: false);
        var builder = new StringBuilder(command);
        if (middleParameters is not null)
        {
            foreach (var parameter in middleParameters)
            {
                ValidateMiddleParameter(parameter);
                builder.Append(' ').Append(parameter);
            }
        }

        if (trailingParameter is not null)
        {
            ValidateNoLineBreak(trailingParameter, nameof(trailingParameter));
            builder.Append(" :").Append(trailingParameter);
        }

        return Frame(builder.ToString());
    }

    /// <summary>
    /// Builds a caller-authorized raw IRC command while retaining line-size and injection checks.
    /// </summary>
    public IrcOutboundMessage BuildRaw(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
        {
            throw new ArgumentException("A raw IRC command cannot be empty.", nameof(rawLine));
        }

        ValidateNoLineBreak(rawLine, nameof(rawLine));
        return Frame(rawLine);
    }

    private IrcOutboundMessage Frame(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length + 2 > _maximumLineBytes)
        {
            throw new InvalidOperationException($"The IRC command is {bytes.Length + 2} bytes with CRLF, exceeding the configured maximum of {_maximumLineBytes} bytes.");
        }

        var framed = new byte[bytes.Length + 2];
        bytes.CopyTo(framed, 0);
        framed[^2] = (byte)'\r';
        framed[^1] = (byte)'\n';
        return new IrcOutboundMessage(line, framed);
    }

    private static void ValidateToken(string value, string parameterName, bool allowLeadingColon)
    {
        if (string.IsNullOrEmpty(value) || (!allowLeadingColon && value[0] == ':') || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("The IRC token must be non-empty and contain no whitespace.", parameterName);
        }

        ValidateNoLineBreak(value, parameterName);
    }

    private static void ValidateMiddleParameter(string value)
    {
        if (string.IsNullOrEmpty(value) || value[0] == ':' || value.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("A middle IRC parameter cannot be empty, start with ':', or contain whitespace.", nameof(value));
        }

        ValidateNoLineBreak(value, nameof(value));
    }

    private static void ValidateNoLineBreak(string value, string parameterName)
    {
        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException("IRC command values cannot contain CR or LF.", parameterName);
        }
    }
}
