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
        new(timestamp, IrcTranscriptDirection.Inbound, DecodeLine(bytes.Span), Convert.ToBase64String(bytes.Span), connectionGeneration);

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
    public static string RedactLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var withoutLineEnd = line.TrimEnd('\r', '\n');
        var separator = withoutLineEnd.IndexOf(' ');
        var command = separator < 0 ? withoutLineEnd : withoutLineEnd[..separator];
        if (command.Equals("PASS", StringComparison.OrdinalIgnoreCase))
        {
            return "PASS :<redacted>";
        }

        if (!command.Equals("AUTHENTICATE", StringComparison.OrdinalIgnoreCase))
        {
            return withoutLineEnd;
        }

        var payload = separator < 0 ? string.Empty : withoutLineEnd[(separator + 1)..].TrimStart();
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
