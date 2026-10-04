using System.Text;
using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class Phase52DraftStateProtocolTests
{
    [Fact]
    public void DraftPayloadRoundTripsUnicodeFormattingAndMultilineTextWithinUtf8ByteBound()
    {
        const string text = "ascii café 日本語 🌍\n\u0002bold\u0002 e\u0301";
        var encoded = NexIrcDraftStateProtocol.EncodePayload(text);
        Assert.DoesNotContain('=', encoded);
        Assert.True(NexIrcDraftStateProtocol.TryDecodePayload(encoded, out var decoded));
        Assert.Equal(text, decoded);

        var exactLimit = new string('界', (NexIrcDraftStateProtocol.MaximumDraftUtf8Bytes - 1) / 3) + "a";
        Assert.Equal(NexIrcDraftStateProtocol.MaximumDraftUtf8Bytes, Encoding.UTF8.GetByteCount(exactLimit));
        Assert.True(NexIrcDraftStateProtocol.TryEncodePayload(exactLimit, out var exactPayload));
        Assert.True(NexIrcDraftStateProtocol.TryDecodePayload(exactPayload, out decoded));
        Assert.Equal(exactLimit, decoded);
        Assert.False(NexIrcDraftStateProtocol.TryEncodePayload(exactLimit + "a", out _));
        Assert.Equal(NexIrcDraftStateProtocol.EmptyPayload, NexIrcDraftStateProtocol.EncodePayload(string.Empty));
        Assert.True(NexIrcDraftStateProtocol.TryDecodePayload(NexIrcDraftStateProtocol.EmptyPayload, out decoded));
        Assert.Empty(decoded);
    }

    [Fact]
    public void DraftPayloadEnforcesExactUtf8BoundaryWithoutTruncatingScalars()
    {
        var belowLimit = new string('x', NexIrcDraftStateProtocol.MaximumDraftUtf8Bytes - 1);
        Assert.Equal(4095, Encoding.UTF8.GetByteCount(belowLimit));
        Assert.True(NexIrcDraftStateProtocol.TryEncodePayload(belowLimit, out var belowPayload));
        Assert.True(NexIrcDraftStateProtocol.TryDecodePayload(belowPayload, out var decoded));
        Assert.Equal(belowLimit, decoded);

        var exactLimit = new string('x', NexIrcDraftStateProtocol.MaximumDraftUtf8Bytes);
        Assert.True(NexIrcDraftStateProtocol.TryEncodePayload(exactLimit, out var exactPayload));
        Assert.True(NexIrcDraftStateProtocol.TryDecodePayload(exactPayload, out decoded));
        Assert.Equal(exactLimit, decoded);

        Assert.False(NexIrcDraftStateProtocol.TryEncodePayload(exactLimit + "x", out _));
        Assert.False(NexIrcDraftStateProtocol.TryEncodePayload(new string('x', 4095) + "🌍", out _));

        var oversizedBytes = Encoding.UTF8.GetBytes(exactLimit + "x");
        var oversizedPayload = Convert.ToBase64String(oversizedBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.False(NexIrcDraftStateProtocol.TryDecodePayload(oversizedPayload, out _));
        Assert.False(NexIrcDraftStateProtocol.TryDecodePayload(string.Empty, out _));
    }

    [Fact]
    public void DraftProtocolRejectsMalformedUtf8NoncanonicalBase64AndInvalidRevisions()
    {
        Assert.False(NexIrcDraftStateProtocol.TryDecodePayload("_w", out _));
        Assert.False(NexIrcDraftStateProtocol.TryDecodePayload("AB", out _));
        Assert.False(NexIrcDraftStateProtocol.TryDecodePayload(new string('a', 6000), out _));
        Assert.False(NexIrcDraftStateProtocol.TryEncodePayload("\uD800", out _));
        Assert.False(NexIrcDraftStateProtocol.TryEncodePayload("\0", out _));
        Assert.False(NexIrcDraftStateProtocol.TryParseRevision("-1", allowZero: true, out _));
        Assert.False(NexIrcDraftStateProtocol.TryParseRevision("01", allowZero: true, out _));
        Assert.False(NexIrcDraftStateProtocol.TryParseRevision("9223372036854775808", allowZero: true, out _));
        Assert.True(NexIrcDraftStateProtocol.TryParseRevision("0", allowZero: true, out var zero));
        Assert.Equal(0, zero);
    }

    [Fact]
    public void DraftSnapshotEntryRequiresCanonicalBoundedConversationIdentity()
    {
        var key = NexIrcReadStateProtocol.EncodeField("channel:#alpha");
        var payload = NexIrcDraftStateProtocol.EncodePayload("draft");
        Assert.True(NexIrcDraftStateProtocol.TryCreateDraft(["STATE", "DRAFT", "ENTRY", key, "7", payload], 3, out var draft));
        Assert.Equal(new NexIrcDraft("channel:#alpha", "draft", 7), draft);

        Assert.False(NexIrcDraftStateProtocol.TryCreateDraft(["STATE", "DRAFT", "ENTRY", key, "-1", payload], 3, out _));
        Assert.False(NexIrcDraftStateProtocol.TryCreateDraft(["STATE", "DRAFT", "ENTRY", key, "7", "%%%"], 3, out _));
        Assert.False(NexIrcDraftStateProtocol.IsValidConversationKey("query-nick:alice"));
        Assert.False(NexIrcDraftStateProtocol.IsValidConversationKey("channel:" + new string('x', 128)));
    }
}
