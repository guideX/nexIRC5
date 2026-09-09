using System.Text;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Tests;

public sealed class Phase26ReactionProtocolTests
{
    [Fact]
    public void ParsesExactCaseReactAndUnreactTagsWithCanonicalReplyParent()
    {
        var react = IrcMessageParser.Parse(
            "@+reply=Parent.ID;+draft/react=heart\\:blue;msgid=Event.1 :bob!u@h TAGMSG #room").Message!;
        var unreact = IrcMessageParser.Parse(
            "@+reply=Parent.ID;+draft/unreact=heart\\:blue;msgid=Event.2 :bob!u@h TAGMSG #room").Message!;

        Assert.NotNull(react.Reaction);
        Assert.Equal(IrcReactionOperation.React, react.Reaction!.Operation);
        Assert.Equal("heart;blue", react.Reaction.Value);
        Assert.Equal("Parent.ID", react.Reaction.ParentMessageId);
        Assert.Equal("Event.1", react.Reaction.EventMessageId);
        Assert.Equal(IrcReactionOperation.Unreact, unreact.Reaction!.Operation);
    }

    [Fact]
    public void ReactionMetadataFailsClosedWithoutDiscardingTheContainingMessage()
    {
        var missingParent = IrcMessageParser.Parse("@+draft/react=heart TAGMSG #room").Message!;
        var bothOperations = IrcMessageParser.Parse("@+reply=P;+draft/react=heart;+draft/unreact=heart TAGMSG #room").Message!;
        var wrongCase = IrcMessageParser.Parse("@+reply=P;+Draft/react=heart TAGMSG #room").Message!;

        Assert.Null(missingParent.Reaction);
        Assert.Null(bothOperations.Reaction);
        Assert.Null(wrongCase.Reaction);
        Assert.Equal("TAGMSG", missingParent.Command);
        Assert.Equal("#room", missingParent.MiddleParameters[0]);
    }

    [Fact]
    public void DuplicateTagKeysKeepTheExistingLastValueSemantics()
    {
        var message = IrcMessageParser.Parse(
            "@+reply=Old;+reply=New;+draft/react=heart TAGMSG #room").Message!;

        Assert.Equal("New", message.TagValues["+reply"]);
        Assert.Equal("New", message.Reaction!.ParentMessageId);
    }

    [Fact]
    public void BuildsDeterministicReactionTagmsgAndEscapesTheValue()
    {
        var message = IrcReactionCommandBuilder.Build(
            new IrcCommandBuilder(),
            "#room",
            "heart;blue",
            IrcReactionOperation.React,
            "Parent.ID");

        Assert.Equal("@+reply=Parent.ID;+draft/react=heart\\:blue TAGMSG #room", message.Line);
        Assert.Equal(Encoding.UTF8.GetByteCount(message.Line) + 2, message.FramedBytes.Length);
    }

    [Fact]
    public void RejectsUnsafeReactionValuesAndInvalidParents()
    {
        Assert.Throws<ArgumentException>(() => IrcReactionCommandBuilder.Build(new IrcCommandBuilder(), "#room", " ", IrcReactionOperation.React, "Parent.ID"));
        Assert.Throws<ArgumentException>(() => IrcReactionCommandBuilder.Build(new IrcCommandBuilder(), "#room", "bad\nvalue", IrcReactionOperation.React, "Parent.ID"));
        Assert.Throws<ArgumentException>(() => IrcReactionCommandBuilder.Build(new IrcCommandBuilder(), "#room", new string('x', 129), IrcReactionOperation.React, "Parent.ID"));
        Assert.Throws<ArgumentException>(() => IrcReactionCommandBuilder.Build(new IrcCommandBuilder(), "#room", "heart", IrcReactionOperation.React, "bad parent"));
        Assert.Throws<ArgumentException>(() => IrcReactionCommandBuilder.Build(new IrcCommandBuilder(), "bad target", "heart", IrcReactionOperation.React, "Parent.ID"));
    }

    [Fact]
    public void ClientTagDenySupportsExactWildcardAndNegativeExceptions()
    {
        var snapshot = new ISupportState().Apply(
            IrcMessageParser.Parse(":srv 005 me CLIENTTAGDENY=*,-+reply,-+draft/unreact :supported").Message!);

        Assert.True(snapshot.IsClientTagDenied("+draft/react"));
        Assert.False(snapshot.IsClientTagDenied("+reply"));
        Assert.False(snapshot.IsClientTagDenied("+draft/unreact"));
        Assert.True(snapshot.IsClientTagDenied("+draft/React"));
    }

    [Fact]
    public void ClientTagDenyAllowsUndeclaredTagsAndHonorsExactDenials()
    {
        var snapshot = new ISupportState().Apply(
            IrcMessageParser.Parse(":srv 005 me CLIENTTAGDENY=+draft/react :supported").Message!);

        Assert.True(snapshot.IsClientTagDenied("+draft/react"));
        Assert.False(snapshot.IsClientTagDenied("+draft/unreact"));
        Assert.False(snapshot.IsClientTagDenied("+reply"));
    }
}
