using System.Text;
using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class FramingTests
{
    [Fact]
    public void EmitsOneCompleteFrame()
    {
        var framer = new IrcLineFramer();

        var frames = framer.Push(Encoding.UTF8.GetBytes("PING :token\r\n"));

        var frame = Assert.Single(frames);
        Assert.Equal("PING :token", frame.Text);
        Assert.Equal("PING :token", Encoding.UTF8.GetString(frame.Bytes.Span));
    }

    [Fact]
    public void PreservesByteByByteFragmentation()
    {
        var framer = new IrcLineFramer();
        var frames = new List<IrcLineFrame>();

        foreach (var value in Encoding.UTF8.GetBytes(":srv 001 nick :Welcome\r\n"))
        {
            frames.AddRange(framer.Push([value]));
        }

        Assert.Equal(":srv 001 nick :Welcome", Assert.Single(frames).Text);
    }

    [Fact]
    public void HandlesSplitCarriageReturnAndLineFeed()
    {
        var framer = new IrcLineFramer();

        Assert.Empty(framer.Push(Encoding.UTF8.GetBytes("PONG\r")));
        var frames = framer.Push([(byte)'\n']);

        Assert.Equal("PONG", Assert.Single(frames).Text);
    }

    [Fact]
    public void EmitsSeveralMessagesFromOneChunk()
    {
        var framer = new IrcLineFramer();

        var frames = framer.Push(Encoding.UTF8.GetBytes("PING :one\r\nPONG :two\r\nNOTICE :three\r\n"));

        Assert.Equal(["PING :one", "PONG :two", "NOTICE :three"], frames.Select(frame => frame.Text));
    }

    [Fact]
    public void RetainsPartialTrailingMessage()
    {
        var framer = new IrcLineFramer();

        var frames = framer.Push(Encoding.UTF8.GetBytes("PING :complete\r\nNOTICE :partial"));
        var disconnect = framer.Disconnect();

        Assert.Single(frames);
        Assert.True(disconnect.HasIncompleteLine);
        Assert.Equal("NOTICE :partial", disconnect.IncompleteText);
    }

    [Fact]
    public void EmptyReadsAreNoOp()
    {
        var framer = new IrcLineFramer();

        Assert.Empty(framer.Push(ReadOnlySpan<byte>.Empty));
        Assert.False(framer.Disconnect().HasIncompleteLine);
    }

    [Fact]
    public void OversizedLineIsRejectedAndDoesNotGrowWithoutBound()
    {
        var framer = new IrcLineFramer(4);

        Assert.Throws<IrcLineTooLongException>(() => framer.Push(Encoding.UTF8.GetBytes("12345")));
        Assert.False(framer.Disconnect().HasIncompleteLine);
    }

    [Fact]
    public void DisconnectReportsIncompleteCarriageReturnAfterMaximumContent()
    {
        var framer = new IrcLineFramer(4);
        Assert.Empty(framer.Push(Encoding.UTF8.GetBytes("1234\r")));

        var result = framer.Disconnect();

        Assert.Equal("1234\r", result.IncompleteText);
    }
}
