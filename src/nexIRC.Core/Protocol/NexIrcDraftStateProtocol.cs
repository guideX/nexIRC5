using System.Globalization;
using System.Text;
using nexIRC.Core.State;

namespace nexIRC.Core.Protocol;

public sealed record NexIrcDraft(string ConversationKey, string Text, long Revision, string? MutationId = null);

public sealed record NexIrcDraftStateSnapshot(IReadOnlyList<NexIrcDraft> Drafts);

/// <summary>Bounded conflict-aware account draft operations carried by nexirc/state=1.</summary>
public static class NexIrcDraftStateProtocol
{
    public const int MaximumConversationKeyBytes = 128;
    public const int MaximumDraftUtf8Bytes = 4096;
    public const int MaximumEntries = 1024;
    public const int MaximumSnapshotBytes = 4 * 1024 * 1024;
    public const int MaximumWireLineBytes = 8192;
    public const int MutationIdLength = 32;
    public const string EmptyPayload = "~";

    public static bool IsSupported(CapabilitySnapshot capabilities) => NexIrcReadStateProtocol.IsSupported(capabilities);

    public static string EncodePayload(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryEncodePayload(text, out var payload))
            throw new ArgumentOutOfRangeException(nameof(text), "Draft text exceeds the UTF-8 byte limit.");
        return payload;
    }

    public static bool TryEncodePayload(string text, out string payload)
    {
        payload = string.Empty;
        if (text is null || !TryGetByteCount(text, out var byteCount) || byteCount > MaximumDraftUtf8Bytes)
            return false;
        payload = text.Length == 0
            ? EmptyPayload
            : Convert.ToBase64String(new UTF8Encoding(false, true).GetBytes(text))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return true;
    }

    public static bool TryDecodePayload(string value, out string text)
    {
        text = string.Empty;
        if (value == EmptyPayload) return true;
        if (string.IsNullOrEmpty(value) || value.Length > (MaximumDraftUtf8Bytes * 4 / 3) + 4
            || value.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length > MaximumDraftUtf8Bytes) return false;
            if (!string.Equals(Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'), value, StringComparison.Ordinal))
                return false;
            text = new UTF8Encoding(false, true).GetString(bytes);
            return TryGetByteCount(text, out var byteCount) && byteCount == bytes.Length
                && byteCount <= MaximumDraftUtf8Bytes;
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    public static bool TryParseRevision(string value, bool allowZero, out long revision)
    {
        revision = 0;
        return value.Length is > 0 and <= 19
            && (value.Length == 1 || value[0] != '0')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && revision >= (allowZero ? 0 : 1)
            && revision.ToString(CultureInfo.InvariantCulture) == value;
    }

    public static bool IsValidConversationKey(string? value) => !string.IsNullOrWhiteSpace(value)
        && TryGetByteCount(value, out var bytes) && bytes <= MaximumConversationKeyBytes
        && !value.Any(char.IsControl)
        && (value.StartsWith("channel:", StringComparison.Ordinal) && value.Length > "channel:".Length
            || value.StartsWith("query-account:", StringComparison.Ordinal) && value.Length > "query-account:".Length);

    public static bool TryDecodeConversationKey(string encoded, out string key)
    {
        key = string.Empty;
        return NexIrcReadStateProtocol.TryDecodeField(encoded, MaximumConversationKeyBytes, out key)
            && IsValidConversationKey(key)
            && string.Equals(NexIrcReadStateProtocol.EncodeField(key), encoded, StringComparison.Ordinal);
    }

    public static bool IsValidMutationId(string? value) => value is { Length: MutationIdLength }
        && value.All(static character => char.IsAsciiHexDigit(character));

    public static bool TryCreateDraft(IReadOnlyList<string> parameters, int offset, out NexIrcDraft? draft)
    {
        draft = null;
        if (parameters.Count < offset + 3
            || !TryDecodeConversationKey(parameters[offset], out var key)
            || !TryParseRevision(parameters[offset + 1], allowZero: false, out var revision)
            || !TryDecodePayload(parameters[offset + 2], out var text))
            return false;
        draft = new NexIrcDraft(key, text, revision);
        return true;
    }

    private static bool TryGetByteCount(string value, out int byteCount)
    {
        try
        {
            byteCount = new UTF8Encoding(false, true).GetByteCount(value);
            return !value.Contains('\0');
        }
        catch (EncoderFallbackException)
        {
            byteCount = 0;
            return false;
        }
    }
}
