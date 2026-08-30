using nexIRC.Core.Protocol;
using nexIRC.Core.Session;

namespace nexIRC.Core.Tests;

public sealed class DispatcherTests
{
    [Fact]
    public void CustomHandlersAddSemanticEventsWithoutReplacingRawMessageModel()
    {
        var dispatcher = new IrcEventDispatcher();
        dispatcher.RegisterNumeric(742, message => new IrcNumericEvent(message, 742));
        var message = IrcMessageParser.Parse(":srv 742 nick :future").Message!;

        Assert.True(dispatcher.TryDispatch(message, out var semantic));
        Assert.IsType<IrcNumericEvent>(semantic);
        Assert.Equal(742, message.NumericCommand);
    }
}
