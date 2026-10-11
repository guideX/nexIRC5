using System.Globalization;
using System.Text;
using nexIRC.Core.State;

namespace nexIRC.Core.Protocol;

public sealed record NexIrcConversationPin(
    string ConversationKey,
    string DisplayName,
    long Revision,
    bool IsPinned,
    long OrderSequence);

public sealed record NexIrcPinStateSnapshot(
    long Generation,
    long CatalogRevision,
    IReadOnlyList<NexIrcConversationPin> Entries);

/// <summary>Bounded ordered account pins carried by the separately negotiated nexirc/pins=1 capability.</summary>
public static class NexIrcPinStateProtocol
{
    public const string CapabilityName = IrcCapabilityCatalog.NexIrcPins;
    public const string CapabilityVersion = "1";
    public const int MaximumConversationKeyBytes = 512;
    public const int MaximumDisplayNameBytes = 128;
    public const int MaximumPinnedConversations = 64;
    public const int MaximumTrackedEntries = 256;
    public const int MaximumSnapshotBytes = 256 * 1024;
    public const int MaximumWireLineBytes = 4096;
    public const int MutationIdLength = 32;

    public static bool IsSupported(CapabilitySnapshot capabilities) =>
        IsVersionEnabled(capabilities, CapabilityName)
        && IsVersionEnabled(capabilities, IrcCapabilityCatalog.NexIrcState)
        && IsVersionEnabled(capabilities, IrcCapabilityCatalog.NexIrcResume);

    public static bool IsValidMutationId(string? value) => value is { Length: MutationIdLength }
        && value.All(static character => char.IsAsciiHexDigit(character));

    public static string EncodeField(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static bool TryDecodeField(string encoded, int maximumBytes, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(encoded) || maximumBytes < 1 || encoded.Length > (maximumBytes * 4 / 3) + 4
            || encoded.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var bytes = Convert.FromBase64String(padded);
            if (bytes.Length > maximumBytes) return false;
            value = new UTF8Encoding(false, true).GetString(bytes);
            return !value.Any(char.IsControl)
                && string.Equals(EncodeField(value), encoded, StringComparison.Ordinal);
        }
        catch (FormatException) { return false; }
        catch (DecoderFallbackException) { return false; }
    }

    public static string BuildChannelKey(string networkIdentity, string channel, IrcCaseMapping mapping) =>
        $"channel:v1:{EncodeField(networkIdentity)}:{EncodeField(IrcCaseMappingComparer.Fold(channel, mapping))}";

    public static string BuildAccountQueryKey(string networkIdentity, string peerAccount) =>
        $"query-account:v1:{EncodeField(networkIdentity)}:{EncodeField(peerAccount)}";

    public static bool IsValidConversationKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > MaximumConversationKeyBytes || key.Any(char.IsControl))
            return false;
        var parts = key.Split(':');
        if (parts.Length != 4 || parts[1] != "v1" || parts[0] is not ("channel" or "query-account"))
            return false;
        if (!TryDecodeField(parts[2], 256, out var networkIdentity) || networkIdentity.Length == 0
            || !TryDecodeField(parts[3], 128, out var destination) || destination.Length == 0)
            return false;
        return parts[0] != "channel" || destination[0] is '#' or '&';
    }

    public static bool IsValidDisplayName(string? value) => !string.IsNullOrWhiteSpace(value)
        && Encoding.UTF8.GetByteCount(value) <= MaximumDisplayNameBytes && !value.Any(char.IsControl);

    public static bool TryParseRevision(string value, bool allowZero, out long revision)
    {
        revision = 0;
        return value.Length is > 0 and <= 19
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)
            && (allowZero || revision > 0)
            && revision.ToString(CultureInfo.InvariantCulture) == value;
    }

    public static bool TryCreatePin(IReadOnlyList<string> parameters, int offset, out NexIrcConversationPin? pin)
    {
        pin = null;
        if (parameters.Count < offset + 5
            || !TryDecodeField(parameters[offset], MaximumConversationKeyBytes, out var key)
            || !IsValidConversationKey(key)
            || !TryParseRevision(parameters[offset + 1], allowZero: false, out var revision)
            || parameters[offset + 2] is not ("0" or "1")
            || !TryParseRevision(parameters[offset + 3], allowZero: true, out var orderSequence)
            || (parameters[offset + 2] == "1") != (orderSequence > 0)
            || !TryDecodeField(parameters[offset + 4], MaximumDisplayNameBytes, out var displayName)
            || !IsValidDisplayName(displayName))
            return false;
        pin = new NexIrcConversationPin(key, displayName, revision, parameters[offset + 2] == "1", orderSequence);
        return true;
    }

    private static bool IsVersionEnabled(CapabilitySnapshot capabilities, string name) =>
        capabilities.IsEnabled(name)
        && capabilities.Available.TryGetValue(name, out var capability)
        && string.Equals(capability.Value, CapabilityVersion, StringComparison.Ordinal);
}
