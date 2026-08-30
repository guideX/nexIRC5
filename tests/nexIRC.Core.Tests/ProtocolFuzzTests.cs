using nexIRC.Core.Protocol;

namespace nexIRC.Core.Tests;

public sealed class ProtocolFuzzTests
{
    [Fact]
    public void ParserAndFramerRemainBoundedForDeterministicArbitraryInputs()
    {
        var random = new Random(0x1B5EED);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var length = random.Next(0, 256);
            var bytes = new byte[length];
            random.NextBytes(bytes);

            var parsed = IrcMessageParser.Parse(System.Text.Encoding.UTF8.GetString(bytes));
            Assert.True(parsed.Success || parsed.Error is not null);

            var framer = new IrcLineFramer(512);
            var split = random.Next(0, length + 1);
            framer.Push(bytes.AsSpan(0, split));
            framer.Push(bytes.AsSpan(split));
            var incomplete = framer.Disconnect();
            Assert.True(incomplete.IncompleteBytes.Length <= length);
        }
    }
}
