using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking;

namespace nexIRC.Headless;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var arguments = Arguments.Parse(args);
        if (arguments.TranscriptPath is not null)
        {
            return await ReplayTranscriptAsync(arguments.TranscriptPath).ConfigureAwait(false);
        }

        if (arguments.Server is null)
        {
            Console.Error.WriteLine("Usage: nexIRC.Headless --server <host> [--port <port>] [--no-tls] [--nick <nick>] [--whois-self] [--transcript <path>]");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var endpoint = new IrcEndpoint(arguments.Server, arguments.Port, arguments.UseTls);
        var options = new ServerSessionOptions
        {
            Endpoint = endpoint,
            Nickname = arguments.Nickname,
            Username = arguments.Nickname,
            RealName = "nexIRC 5 headless diagnostic harness",
            RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1V,
            Reconnect = new ReconnectPolicy(Enabled: false)
        };
        await using var session = new ServerSession(options, new TcpTlsIrcTransportFactory());
        var registered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var whoisComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.StateChanged += (_, change) => Console.WriteLine($"STATE {change.Previous} -> {change.Current} (generation {change.ConnectionGeneration})");
        session.StateChanged += (_, change) =>
        {
            if (change.Current == ServerSessionState.Registered)
            {
                registered.TrySetResult(true);
            }
        };
        session.SemanticEventReceived += (_, item) =>
        {
            Console.WriteLine($"EVENT {item.Event.GetType().Name}: {IrcSensitiveData.RedactLine(item.Event.Message.RawLine)}");
            if (item.Event is IrcWhoisEvent { Numeric: 318 } whois
                && string.Equals(whois.Nickname, arguments.Nickname, StringComparison.OrdinalIgnoreCase))
            {
                whoisComplete.TrySetResult(true);
            }
        };

        var rawTask = PrintRawAsync(session, cancellation.Token);
        var outboundTask = PrintOutboundAsync(session, cancellation.Token);
        var runTask = session.RunAsync(cancellation.Token);
        try
        {
            if (arguments.WhoisSelf)
            {
                var startup = await Task.WhenAny(registered.Task, runTask).ConfigureAwait(false);
                if (startup == registered.Task)
                {
                    await session.SendCommandAsync("WHOIS", [arguments.Nickname], cancellationToken: cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine($"WHOIS sent for temporary nickname {arguments.Nickname}; waiting for 318.");
                    await whoisComplete.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token).ConfigureAwait(false);
                    Console.WriteLine("WHOIS self proof received (numeric 318).");
                    await session.DisconnectAsync("nexIRC Phase 1F smoke test").ConfigureAwait(false);
                }
                else
                {
                    Console.WriteLine("WHOIS self proof was not attempted because registration ended first.");
                }
            }

            await runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }

        await rawTask.ConfigureAwait(false);
        await outboundTask.ConfigureAwait(false);
        PrintDiagnostics(session.Snapshot);
        return session.Snapshot.State == ServerSessionState.Failed ? 1 : 0;
    }

    private static async Task<int> ReplayTranscriptAsync(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Transcript not found: {path}");
            return 2;
        }

        var framer = new IrcLineFramer();
        var summary = new TranscriptReplaySummary();
        try
        {
            var firstContent = (await File.ReadAllTextAsync(path).ConfigureAwait(false)).TrimStart();
            if (firstContent.StartsWith('{'))
            {
                var entries = await IrcTranscriptFile.ReadAllAsync(path).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    summary.Apply(entry);
                    if (entry.Direction == IrcTranscriptDirection.Inbound)
                    {
                        PrintFrames(framer.Push(EnsureLineEnding(entry.RawBytes.Span).Span));
                    }
                }
            }
            else
            {
                PrintFrames(framer.Push(await File.ReadAllBytesAsync(path).ConfigureAwait(false)));
            }
        }
        catch (IrcLineTooLongException exception)
        {
            Console.Error.WriteLine($"TRANSCRIPT-ERROR {exception.Message}");
            return 1;
        }
        catch (FormatException exception)
        {
            Console.Error.WriteLine($"TRANSCRIPT-ERROR {exception.Message}");
            return 1;
        }

        var incomplete = framer.Disconnect();
        if (incomplete.HasIncompleteLine)
        {
            Console.WriteLine($"INCOMPLETE {incomplete.IncompleteText}");
        }

        Console.WriteLine("--- replay summary ---");
        Console.WriteLine(summary.Format());

        return 0;

        static ReadOnlyMemory<byte> EnsureLineEnding(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length >= 2 && bytes[^2] == '\r' && bytes[^1] == '\n')
            {
                return bytes.ToArray();
            }

            var framed = new byte[bytes.Length + 2];
            bytes.CopyTo(framed);
            framed[^2] = (byte)'\r';
            framed[^1] = (byte)'\n';
            return framed;
        }

        void PrintFrames(IReadOnlyList<IrcLineFrame> frames)
        {
            foreach (var frame in frames)
            {
                var parsed = IrcMessageParser.Parse(frame.Text);
                Console.WriteLine($"RAW  {IrcSensitiveData.RedactLine(frame.Text)}");
                if (parsed.Success)
                {
                    var message = parsed.Message!;
                    summary.ApplyInbound(message);
                    Console.WriteLine($"PARSED command={message.Command} numeric={message.NumericCommand?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"} params={message.Parameters.Count}");
                }
                else
                {
                    Console.WriteLine($"PARSE-ERROR {parsed.Error}");
                }
            }
        }
    }

    private static async Task PrintRawAsync(ServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in session.ReadRawEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                Console.WriteLine($"RAW  {item.RawLine}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task PrintOutboundAsync(ServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in session.ReadOutboundEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                var line = item.RawLine.TrimStart();
                var separator = line.IndexOfAny([' ', '\t']);
                var command = separator < 0 ? line : line[..separator];
                Console.WriteLine($"OUTBOUND command={command} generation={item.ConnectionGeneration}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void PrintDiagnostics(ServerSessionSnapshot snapshot)
    {
        Console.WriteLine("--- diagnostics ---");
        Console.WriteLine($"Connection state: {snapshot.State}");
        Console.WriteLine($"Network: {snapshot.Features.NetworkName ?? "unknown"}");
        Console.WriteLine($"Detected IRCd: {snapshot.Identity.ProbableIrcd} ({snapshot.Identity.Confidence:P0})");
        Console.WriteLine($"CAP enabled: {string.Join(' ', snapshot.Capabilities.Enabled.OrderBy(static value => value, StringComparer.Ordinal))}");
        Console.WriteLine($"ISUPPORT: {string.Join(' ', snapshot.ISupport.Tokens.Keys.OrderBy(static value => value, StringComparer.Ordinal))}");
        Console.WriteLine($"PREFIX: {FormatPrefix(snapshot.Features.Prefix)}");
        Console.WriteLine($"CHANTYPES: {new string(snapshot.Features.ChannelTypes.OrderBy(static value => value).ToArray())}");
        Console.WriteLine($"CHANMODES: {snapshot.Features.ChannelModes?.RawValue ?? "unknown"}");
        Console.WriteLine($"Channels: {snapshot.Channels.Count}; queries: {snapshot.Queries.Count}");
    }

    private static string FormatPrefix(IrcPrefixGrammar? prefix) => prefix is null ? "unknown" : $"({new string(prefix.Modes.ToArray())}){new string(prefix.Prefixes.ToArray())}";

    private sealed class Arguments
    {
        public string? Server { get; private set; }
        public string? TranscriptPath { get; private set; }
        public int Port { get; private set; } = 6697;
        public bool UseTls { get; private set; } = true;
        public string Nickname { get; private set; } = "nexIRC5";
        public bool WhoisSelf { get; private set; }

        public static Arguments Parse(string[] args)
        {
            var result = new Arguments();
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--server" when index + 1 < args.Length:
                        result.Server = args[++index];
                        break;
                    case "--port" when index + 1 < args.Length && int.TryParse(args[++index], out var port):
                        result.Port = port;
                        break;
                    case "--nick" when index + 1 < args.Length:
                        result.Nickname = args[++index];
                        break;
                    case "--transcript" when index + 1 < args.Length:
                        result.TranscriptPath = args[++index];
                        break;
                    case "--whois-self":
                        result.WhoisSelf = true;
                        break;
                    case "--no-tls":
                        result.UseTls = false;
                        break;
                }
            }

            return result;
        }
    }
}
