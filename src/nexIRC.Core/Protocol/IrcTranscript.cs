using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace nexIRC.Core.Protocol;

public enum IrcTranscriptDirection
{
    Inbound,
    Outbound
}

/// <summary>
/// One JSON-lines transcript record. Raw bytes are stored as base64 so a
/// transcript remains lossless even when the IRC payload is not valid UTF-8.
/// </summary>
public sealed record IrcTranscriptEntry(
    DateTimeOffset Timestamp,
    IrcTranscriptDirection Direction,
    string RawLine,
    string RawBytesBase64,
    int ConnectionGeneration)
{
    public ReadOnlyMemory<byte> RawBytes => Convert.FromBase64String(RawBytesBase64);

    public static IrcTranscriptEntry FromInbound(
        DateTimeOffset timestamp,
        ReadOnlyMemory<byte> bytes,
        int connectionGeneration) =>
        CreateInbound(timestamp, bytes, connectionGeneration);

    private static IrcTranscriptEntry CreateInbound(
        DateTimeOffset timestamp,
        ReadOnlyMemory<byte> bytes,
        int connectionGeneration)
    {
        var decodedLine = DecodeLine(bytes.Span);
        var redactedLine = IrcSensitiveData.RedactLine(decodedLine);
        var storedBytes = string.Equals(decodedLine, redactedLine, StringComparison.Ordinal)
            ? bytes.ToArray()
            : System.Text.Encoding.UTF8.GetBytes(redactedLine + "\r\n");
        return new(timestamp, IrcTranscriptDirection.Inbound, redactedLine, Convert.ToBase64String(storedBytes), connectionGeneration);
    }

    public static IrcTranscriptEntry FromOutbound(
        DateTimeOffset timestamp,
        ReadOnlyMemory<byte> bytes,
        int connectionGeneration)
    {
        var redactedLine = IrcSensitiveData.RedactLine(DecodeLine(bytes.Span));
        var redactedBytes = System.Text.Encoding.UTF8.GetBytes(redactedLine + "\r\n");
        return new(timestamp, IrcTranscriptDirection.Outbound, redactedLine, Convert.ToBase64String(redactedBytes), connectionGeneration);
    }

    private static string DecodeLine(ReadOnlySpan<byte> bytes) =>
        System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n');
}

/// <summary>
/// Sanitizes protocol records before they can reach diagnostics, transcript
/// persistence, or public outbound-event consumers. Authentication and PASS
/// payloads are intentionally not lossless: preserving credentials is never a
/// valid transcript requirement.
/// </summary>
public static class IrcSensitiveData
{
    public static bool IsSensitiveCommandLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var withoutLineEnd = line.TrimStart();
        var commandStart = withoutLineEnd.StartsWith(':')
            ? withoutLineEnd.IndexOfAny([' ', '\t']) + 1
            : 0;
        if (commandStart <= 0 || commandStart >= withoutLineEnd.Length)
        {
            commandStart = 0;
        }

        var separator = withoutLineEnd.IndexOfAny([' ', '\t'], commandStart);
        var command = separator < 0 ? withoutLineEnd[commandStart..] : withoutLineEnd[commandStart..separator];
        return command.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            || command.Equals("AUTHENTICATE", StringComparison.OrdinalIgnoreCase)
            || command.Equals("NEXIRC", StringComparison.OrdinalIgnoreCase);
    }

    public static string RedactLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var withoutLineEnd = line.TrimEnd('\r', '\n');
        var normalized = withoutLineEnd.TrimStart();
        var commandStart = normalized.StartsWith(':')
            ? normalized.IndexOfAny([' ', '\t']) + 1
            : 0;
        if (commandStart <= 0 || commandStart >= normalized.Length)
        {
            commandStart = 0;
        }

        var separator = normalized.IndexOfAny([' ', '\t'], commandStart);
        var command = separator < 0 ? normalized[commandStart..] : normalized[commandStart..separator];
        if (command.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return "PASS :<redacted>";
        }

        if (command.Equals("NEXIRC", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(" NEXIRC ", StringComparison.OrdinalIgnoreCase))
        {
            return RedactNexIrcResumeLine(withoutLineEnd);
        }

        if (!command.Equals("AUTHENTICATE", StringComparison.OrdinalIgnoreCase))
        {
            return withoutLineEnd;
        }

        var payload = separator < 0 ? string.Empty : normalized[(separator + 1)..].TrimStart();
        if (payload.Equals("+", StringComparison.Ordinal))
        {
            return "AUTHENTICATE +";
        }

        if (payload.Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
        {
            return "AUTHENTICATE PLAIN";
        }

        return "AUTHENTICATE <redacted>";
    }

    /// <summary>
    /// Parsed protocol messages are public diagnostics too. Reparse a
    /// redacted representation before publishing PASS/AUTHENTICATE/NEXIRC so
    /// a bearer credential cannot escape through the parsed-event channel.
    /// The protocol loop continues using its private original message.
    /// </summary>
    public static IrcMessage RedactParsedMessage(IrcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsSensitiveCommandLine(message.RawLine))
        {
            return message;
        }

        var parsed = IrcMessageParser.Parse(RedactLine(message.RawLine));
        return parsed.Message ?? message;
    }

    private static string RedactNexIrcResumeLine(string line)
    {
        var commandIndex = line.IndexOf(" NEXIRC ", StringComparison.OrdinalIgnoreCase);
        var commandText = commandIndex >= 0 ? line[(commandIndex + 1)..] : line;
        var tokens = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var prefix = commandIndex >= 0 ? line[..(commandIndex + 1)] : string.Empty;

        if (tokens.Length >= 2 && tokens[1].Equals("STATE", StringComparison.OrdinalIgnoreCase))
        {
            return tokens.Length >= 4 && tokens[2].Equals("ACK", StringComparison.OrdinalIgnoreCase)
                ? prefix + $"NEXIRC STATE ACK {tokens[3]}"
                : prefix + "NEXIRC STATE <redacted>";
        }

        if (tokens.Length >= 4 && tokens[1].Equals("PAIR", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens[2].Equals("CREATED", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 5)
            {
                tokens[3] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[3])}>";
                return prefix + string.Join(' ', tokens);
            }

            if ((tokens[2].Equals("USE", StringComparison.OrdinalIgnoreCase)
                    || tokens[2].Equals("REVOKE", StringComparison.OrdinalIgnoreCase))
                && tokens.Length >= (tokens[2].Equals("USE", StringComparison.OrdinalIgnoreCase) ? 5 : 4))
            {
                tokens[3] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[3])}>";
                if (tokens[2].Equals("USE", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 6)
                {
                    tokens[5] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[5])}>";
                }

                return prefix + string.Join(' ', tokens);
            }

            if ((tokens[2].Equals(NexIrcResumeProtocol.PairRecoverSubcommand, StringComparison.OrdinalIgnoreCase)
                    || tokens[2].Equals(NexIrcResumeProtocol.PairCustodySubcommand, StringComparison.OrdinalIgnoreCase))
                && tokens.Length >= 4
                && !tokens[3].Equals("OK", StringComparison.OrdinalIgnoreCase)
                && !tokens[3].Equals("REJECT", StringComparison.OrdinalIgnoreCase))
            {
                tokens[3] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[3])}>";
                return prefix + string.Join(' ', tokens);
            }
        }

        if (tokens.Length >= 4 && tokens[1].Equals("ATTACH", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens[2].Equals("ACCEPT", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 7)
            {
                tokens[4] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[4])}>";
                if (tokens.Length >= 8)
                {
                    tokens[7] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[7])}>";
                }

                return prefix + string.Join(' ', tokens);
            }

            if (!tokens[2].Equals("REJECT", StringComparison.OrdinalIgnoreCase)
                && !tokens[2].Equals("ACCEPT", StringComparison.OrdinalIgnoreCase))
            {
                tokens[2] = $"<redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[2])}>";
                return prefix + string.Join(' ', tokens);
            }
        }

        if (tokens.Length >= 4
            && tokens[1].Equals("RESUME", StringComparison.OrdinalIgnoreCase)
            && !tokens[2].Equals("ACCEPT", StringComparison.OrdinalIgnoreCase)
            && !tokens[2].Equals("COMPLETE", StringComparison.OrdinalIgnoreCase)
            && !tokens[2].Equals("REJECT", StringComparison.OrdinalIgnoreCase))
        {
            return prefix + $"NEXIRC RESUME <redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[2])}> {tokens[3]}";
        }

        if (tokens.Length >= 4
            && tokens[1].Equals("SESSION", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length >= 6
                && tokens[2].Equals(NexIrcResumeProtocol.SessionRotateSubcommand, StringComparison.OrdinalIgnoreCase))
            {
                return prefix + $"NEXIRC SESSION ROTATE <redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[3])}> {tokens[4]} {tokens[5]}";
            }

            if (tokens[2].Equals("KEY", StringComparison.OrdinalIgnoreCase))
            {
                return prefix + $"NEXIRC SESSION KEY <redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[3])}>";
            }

            if (tokens[2].Equals("ATTACHMENT", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }

            return prefix + $"NEXIRC SESSION <redacted:{NexIrcResumeProtocol.FingerprintToken(tokens[2])}> {tokens[3]}";
        }

        return line;
    }

    public static byte[] RedactFramedBytes(ReadOnlySpan<byte> bytes)
    {
        var redactedLine = RedactLine(System.Text.Encoding.UTF8.GetString(bytes));
        return System.Text.Encoding.UTF8.GetBytes(redactedLine + "\r\n");
    }
}

/// <summary>
/// Bounded, deterministic JSON-lines transcript persistence for protocol
/// fixtures and diagnostics. It deliberately stores inbound and outbound data
/// separately instead of pretending a transcript is a server implementation.
/// </summary>
public static class IrcTranscriptFile
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task WriteAsync(
        string path,
        IEnumerable<IrcTranscriptEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(entries);

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
        await using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions).AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async IAsyncEnumerable<IrcTranscriptEntry> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            IrcTranscriptEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<IrcTranscriptEntry>(line, JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new FormatException($"Transcript line {lineNumber} is not valid JSON.", exception);
            }

            if (entry is null || string.IsNullOrWhiteSpace(entry.RawBytesBase64))
            {
                throw new FormatException($"Transcript line {lineNumber} did not contain a complete record.");
            }

            try
            {
                _ = entry.RawBytes;
            }
            catch (FormatException exception)
            {
                throw new FormatException($"Transcript line {lineNumber} contained invalid base64 bytes.", exception);
            }

            yield return entry;
        }
    }

    public static async Task<IReadOnlyList<IrcTranscriptEntry>> ReadAllAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var entries = new List<IrcTranscriptEntry>();
        await foreach (var entry in ReadAsync(path, cancellationToken).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        return entries;
    }
}
