using System.Globalization;
using System.Text;
using nexIRC.Core.State;

namespace nexIRC.Core.Protocol;

public sealed record NexIrcReadMarker(string ConversationKey, long Sequence, string MessageId, long Revision, int SessionEpoch);

public sealed record NexIrcReadStateSnapshot(int SessionEpoch, IReadOnlyList<NexIrcReadMarker> ReadMarkers);

public static class NexIrcReadStateProtocol
{
    public const string CapabilityName = IrcCapabilityCatalog.NexIrcState;
    public const string CapabilityVersion = "1";
    public const int MaximumConversationKeyLength = 128;
    public const int MaximumMessageIdLength = 128;
    public const int MaximumEntries = 512;

    public static bool IsSupported(CapabilitySnapshot capabilities) =>
        capabilities.IsEnabled(CapabilityName)
        && capabilities.Available.TryGetValue(CapabilityName, out var capability)
        && string.Equals(capability.Value ?? CapabilityVersion, CapabilityVersion, StringComparison.Ordinal);

    public static string EncodeField(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecodeField(string encoded, int maximumLength, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > (maximumLength * 4 / 3) + 4
            || encoded.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length > maximumLength) return false;
            value = new UTF8Encoding(false, true).GetString(bytes);
            return !value.Any(char.IsControl);
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    public static bool TryParseSequence(string value, out long sequence)
    {
        sequence = 0;
        return value.Length > 1 && value[0] == 's'
            && long.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
            && sequence > 0;
    }

    public static bool TryCreateMarker(IReadOnlyList<string> parameters, int offset, out NexIrcReadMarker? marker)
    {
        marker = null;
        if (parameters.Count < offset + 5
            || !TryDecodeField(parameters[offset], MaximumConversationKeyLength, out var key)
            || string.IsNullOrWhiteSpace(key)
            || !TryParseSequence(parameters[offset + 1], out var sequence)
            || !TryDecodeField(parameters[offset + 2], MaximumMessageIdLength, out var messageId)
            || string.IsNullOrWhiteSpace(messageId)
            || !long.TryParse(parameters[offset + 3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision < 1
            || !int.TryParse(parameters[offset + 4], NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
            || epoch < 1)
            return false;
        marker = new NexIrcReadMarker(key, sequence, messageId, revision, epoch);
        return true;
    }
}
