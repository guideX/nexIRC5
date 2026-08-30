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
        var beta = sessions.Add(Options("BetaNet", _beta.Endpoint, "nexBeta", "#lounge"));
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
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG #alpha :nexAlpha, this is an important highlight.");
        _alpha.EnqueueInboundLine(":Mira!m@alpha PRIVMSG nexAlpha :This private query is intentionally highlighted.");
        _alpha.EnqueueInboundLine(":Rook!r@alpha NICK RookAway");
        _alpha.EnqueueInboundLine(":alpha.server 311 nexAlpha Mira mira alpha.example * :Mira Demo User");
        _alpha.EnqueueInboundLine(":alpha.server 312 nexAlpha Mira alpha.server :AlphaNet IRC services");
        _alpha.EnqueueInboundLine(":alpha.server 313 nexAlpha Mira :is an IRC operator");
        _alpha.EnqueueInboundLine(":alpha.server 317 nexAlpha Mira 42 1735689600 :seconds idle, signon time");
        _alpha.EnqueueInboundLine(":alpha.server 319 nexAlpha Mira :@#alpha +#lounge");
        _alpha.EnqueueInboundLine(":alpha.server 330 nexAlpha Mira mira-account :is logged in as");
        _alpha.EnqueueInboundLine(":alpha.server 338 nexAlpha Mira :is using a secure connection");
        _alpha.EnqueueInboundLine(":alpha.server 318 nexAlpha Mira :End of WHOIS list");
        _alpha.EnqueueInboundLine(":alpha.server 321 nexAlpha Channel :Users Name");
        _alpha.EnqueueInboundLine(":alpha.server 322 nexAlpha #alpha 12 :A calm place for testing");
        _alpha.EnqueueInboundLine(":alpha.server 322 nexAlpha #lounge 7 :Shared channel name on AlphaNet");
        _alpha.EnqueueInboundLine(":alpha.server 322 nexAlpha #random 0 :");
        _alpha.EnqueueInboundLine(":alpha.server 323 nexAlpha :End of LIST");

        _beta.EnqueueInboundLine(":nexBeta!demo@beta JOIN #lounge");
        _beta.EnqueueInboundLine(":beta.server 353 nexBeta = #lounge :@nexBeta +Mira");
        _beta.EnqueueInboundLine(":beta.server 366 nexBeta #lounge :End of names");
        _beta.EnqueueInboundLine(":beta.server 332 nexBeta #lounge :Beta network topic");
        _beta.EnqueueInboundLine(":Mira!m@beta PRIVMSG #lounge :Events from BetaNet stay in BetaNet.");
        _beta.EnqueueInboundLine(":beta.server NOTICE nexBeta :Status notices are rendered in the server view.");

        await WaitForAsync(() => alpha.State == NetworkDisplayState.Registered
            && beta.State == NetworkDisplayState.Registered
            && alpha.Channels.Any(channel => channel.Channel == "#alpha")
            && beta.Channels.Any(channel => channel.Channel == "#lounge")).ConfigureAwait(true);

        var dispatcher = new IrcCommandDispatcher(sessions);
        var alphaChannel = alpha.Channels.First(channel => channel.Channel == "#alpha");
        await dispatcher.DispatchAsync(alpha, alphaChannel, "Local AlphaNet message").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alphaChannel, "/me demonstrates a local action").ConfigureAwait(true);
        await dispatcher.DispatchAsync(alpha, alphaChannel, "/msg Mira A local private message").ConfigureAwait(true);

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
