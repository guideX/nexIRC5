using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Desktop;

public sealed class DemoScenario
{
    private readonly FakeIrcTransport _alpha;
    private readonly FakeIrcTransport _beta;

    private DemoScenario(FakeIrcTransport alpha, FakeIrcTransport beta)
    {
        _alpha = alpha;
        _beta = beta;
    }

    public static IIrcTransportFactory CreateFactory(out DemoScenario scenario)
    {
        var factory = new FakeIrcTransportFactory();
        var alpha = new FakeIrcTransport(new IrcEndpoint("demo.alpha.invalid", 6667, false));
        var beta = new FakeIrcTransport(new IrcEndpoint("demo.beta.invalid", 6667, false));
        factory.Add(alpha);
        factory.Add(beta);
        scenario = new DemoScenario(alpha, beta);
        return factory;
    }

    public async Task SeedAsync(NetworkSessionManager sessions)
    {
        var alpha = sessions.Add(Options("AlphaNet", _alpha.Endpoint, "nexAlpha", "#alpha", "#lounge"));
        var beta = sessions.Add(Options("BetaNet", _beta.Endpoint, "nexBeta", "#beta"));
        await sessions.ConnectAsync(alpha.Id).ConfigureAwait(true);
        await sessions.ConnectAsync(beta.Id).ConfigureAwait(true);
        await WaitForAsync(() => _alpha.ConnectCount == 1 && _beta.ConnectCount == 1).ConfigureAwait(true);

        Register(_alpha, "alpha.server", "nexAlpha", "AlphaNet", "(qaohv)~&@%+");
        Register(_beta, "beta.server", "nexBeta", "BetaNet", "(ov)@+");

        _alpha.EnqueueInboundLine(":nexAlpha!demo@alpha JOIN #alpha");
        _alpha.EnqueueInboundLine(":nexAlpha!demo@alpha JOIN #lounge");
        _alpha.EnqueueInboundLine(":alpha.server 353 nexAlpha = #alpha :~nexAlpha @Mira +Rook");
        _alpha.EnqueueInboundLine(":alpha.server 366 nexAlpha #alpha :End of names");
        _alpha.EnqueueInboundLine(":alpha.server 332 nexAlpha #alpha :A calm place for testing the nexIRC shell");
        _alpha.EnqueueInboundLine(":alpha.server MODE #alpha +nt");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG #alpha :Welcome to the Alpha network.");
        _alpha.EnqueueInboundLine(":Rook!r@alpha PRIVMSG #alpha :Try /join, /me, or /query in the command line.");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG nexAlpha :This private query is intentionally highlighted.");
        _alpha.EnqueueInboundLine(":Rook!r@alpha NICK RookAway");

        _beta.EnqueueInboundLine(":nexBeta!demo@beta JOIN #beta");
        _beta.EnqueueInboundLine(":beta.server 353 nexBeta = #beta :@nexBeta +Sable");
        _beta.EnqueueInboundLine(":beta.server 366 nexBeta #beta :End of names");
        _beta.EnqueueInboundLine(":beta.server 332 nexBeta #beta :Beta network topic");
        _beta.EnqueueInboundLine(":Sable!s@beta PRIVMSG #beta :Events from BetaNet stay in BetaNet.");
        _beta.EnqueueInboundLine(":beta.server NOTICE nexBeta :Status notices are rendered in the server view.");

        sessions.ActivateView(alpha.StatusView.Id);
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, params string[] channels) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "nexIRC 5 deterministic demo",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = channels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string server, string nickname, string network, string prefix)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        transport.EnqueueInboundLine($":{server} 005 {nickname} NETWORK={network} PREFIX={prefix} CHANTYPES=#&+! :demo features");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to the nexIRC 5 demo workspace");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The deterministic desktop demo did not start in time.");
            }

            await Task.Delay(10).ConfigureAwait(true);
        }
    }
}
