using nexIRC.Application;

namespace nexIRC.Application.Tests;

public sealed class Phase25ReplyRelationshipTests
{
    [Fact]
    public void ReplyParentSurvivesDurableProjectionAndResolvesByExactCaseSensitiveId()
    {
        var parent = new ConversationLogRecord
        {
            NetworkId = Guid.NewGuid(),
            ScopeId = Guid.NewGuid(),
            ConversationKind = LogConversationKind.Channel,
            ConversationName = "#room",
            ConversationKey = "Channel:#room",
            ServerMessageId = "Parent.ID",
            Sender = "alice",
            Text = "A parent message",
            MessageKind = LogMessageKind.Message,
            Direction = LogDirection.Incoming,
            Timestamp = DateTimeOffset.UtcNow
        };
        var child = parent with
        {
            ServerMessageId = "child.ID",
            Sender = "bob",
            Text = "A reply",
            ReplyParentMessageId = "Parent.ID"
        };

        var projected = ConversationHistoryProjection.ToTranscriptEntry(child);
        Assert.Equal("Parent.ID", projected.ReplyParentMessageId);
        Assert.Equal("Parent.ID", ReplyRelationship.FromRecord(child).ParentMessageId);
        Assert.NotEqual("parent.id", projected.ReplyParentMessageId);
    }

    [Fact]
    public void ReplyPreviewIsBoundedWithoutSplittingUtf16SurrogatePairs()
    {
        var preview = ReplyText.BoundedPreview("😀" + new string('x', 200), 4);

        Assert.Equal("😀x…", preview);
        Assert.DoesNotContain('\uFFFD', preview);
    }

}
