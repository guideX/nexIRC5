using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking;

namespace nexIRC.Desktop;

/// <summary>
/// Bounded real-Application acceptance control for ordinary IRC fallback.
/// TLS uses the platform certificate policy because no validation override is
/// supplied. Output contains capability flags, command classifications, and
/// hashed view identities only.
/// </summary>
internal static class Phase54R3OrdinaryIrcHarness
{
    private const string Flag = "--phase54r3-ordinary-irc";

    public static bool IsRequested(string[] args) => args.Any(argument =>
        string.Equals(argument, Flag, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The ordinary IRC desktop process fixture requires Windows.");

        Console.SetIn(new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 1024, leaveOpen: true));
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true });
        Emit("PHASE54R3_ORDINARY_HARNESS_STARTED");

        var options = Options.Parse(args);
        var configuration = new ConfigurationService(new InMemoryConfigurationStore());
        await configuration.LoadAsync().ConfigureAwait(true);
        configuration.SetPreferences(new ApplicationPreferences
        {
            ConversationLoggingEnabled = false,
            PrivateMessageLoggingEnabled = false,
            StatusLoggingEnabled = false
        });
        var credentials = new ProfileCredentialService(new InMemoryProfileCredentialStore());
        var window = new MainWindow(new TcpTlsIrcTransportFactory(new TcpTlsIrcTransportOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(12)
        }), configuration, credentials);
        window.Title = "nexIRC 5 Phase 54R3 ordinary IRC fixture";
        window.Show();

        var workspace = window.ViewModel.Sessions.Add(new NetworkConnectionOptions
        {
            DisplayName = "InspIRCd ordinary IRC acceptance",
            Endpoint = new IrcEndpoint(options.Host, options.Port, useTls: true),
            Nickname = options.Nickname,
            Username = "nexirc",
            RealName = "nexIRC Phase 54R3 ordinary IRC acceptance",
            DesiredChannels = new HashSet<string>(StringComparer.Ordinal),
            RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1Y,
            SaslPolicy = SaslAuthenticationPolicy.Disabled,
            Reconnect = new ReconnectPolicy(Enabled: false)
        });

        var outbound = new OutboundTrace();
        workspace.Session.OutboundCommandSent += (_, item) => outbound.Record(item.RawLine);
        workspace.Session.RawLineReceived += (_, item) => outbound.RecordInbound(item.RawLine);

        Emit("PHASE54R3_TLS_POLICY=platform-default");
        Emit("PHASE54R3_CONNECTING");
        _ = ObserveConnectAsync(window.ViewModel.Sessions.ConnectAsync(workspace.Id).AsTask());
        var until = DateTime.UtcNow.AddSeconds(35);
        while (DateTime.UtcNow < until)
        {
            if (workspace.Session.Snapshot.Registration == RegistrationState.Registered
                && workspace.Session.Snapshot.State is ServerSessionState.Connected or ServerSessionState.Registered)
                break;
            if (workspace.Session.Snapshot.State == ServerSessionState.Failed)
            {
                var failure = workspace.Session.Snapshot.LastFailure;
                var safe = (failure?.Message ?? "unknown failure").Replace('\r', ' ').Replace('\n', ' ');
                throw new InvalidOperationException($"Ordinary IRC process connection failed ({failure?.Kind.ToString() ?? "unknown"}: {safe}).");
            }
            await Task.Delay(30).ConfigureAwait(true);
        }
        if (workspace.Session.Snapshot.Registration != RegistrationState.Registered)
            throw new TimeoutException("The ordinary IRC connection did not complete registration within 35 seconds.");

        EmitStatus(workspace, window.ViewModel, outbound, "ready");
        var commandCount = 0;
        while (await Task.Run(() => Console.In.ReadLine()).ConfigureAwait(true) is { } command)
        {
            if (command.Length > 512)
            {
                Emit("PHASE54R3_COMMAND outcome=too-large");
                continue;
            }
            if (++commandCount > 64)
            {
                Emit("PHASE54R3_COMMAND outcome=limit-reached");
                break;
            }

            if (command.Equals("STATUS", StringComparison.OrdinalIgnoreCase))
            {
                EmitStatus(workspace, window.ViewModel, outbound, "status");
            }
            else if (command.StartsWith("JOIN ", StringComparison.OrdinalIgnoreCase))
            {
                var channelName = command[5..].Trim();
                if (!IsValidChannelName(channelName))
                {
                    Emit("PHASE54R3_JOIN outcome=invalid");
                    continue;
                }
                await workspace.Session.JoinChannelAsync(channelName).ConfigureAwait(true);
                var joinUntil = DateTime.UtcNow.AddSeconds(12);
                ChannelView? joined = null;
                while (DateTime.UtcNow < joinUntil)
                {
                    joined = workspace.Channels.FirstOrDefault(candidate => candidate.IsJoined
                        && candidate.Channel.Equals(channelName, StringComparison.OrdinalIgnoreCase));
                    if (joined is not null) break;
                    await Task.Delay(50).ConfigureAwait(true);
                }
                Emit($"PHASE54R3_JOIN outcome={(joined is null ? "timeout" : "joined")} view={Fingerprint(channelName)}");
            }
            else if (command.StartsWith("PIN ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("UNPIN ", StringComparison.OrdinalIgnoreCase))
            {
                var pin = command.StartsWith("PIN ", StringComparison.OrdinalIgnoreCase);
                var name = command[(pin ? 4 : 6)..].Trim();
                var channel = FindChannel(workspace, name);
                window.ViewModel.SelectView(channel);
                var changed = await window.ViewModel.SetConversationPinnedAsync(channel, pin).ConfigureAwait(true);
                var projection = window.ViewModel.PinnedConversations.Any(item => ReferenceEquals(item.View, channel));
                Emit($"PHASE54R3_PIN operation={(pin ? "PIN" : "UNPIN")} accepted={changed.ToString().ToLowerInvariant()} projected={projection.ToString().ToLowerInvariant()} view={Fingerprint(name)}");
            }
            else if (command.StartsWith("QUERY_PIN ", StringComparison.OrdinalIgnoreCase)
                || command.StartsWith("QUERY_UNPIN ", StringComparison.OrdinalIgnoreCase))
            {
                var pin = command.StartsWith("QUERY_PIN ", StringComparison.OrdinalIgnoreCase);
                var prefixLength = pin ? "QUERY_PIN ".Length : "QUERY_UNPIN ".Length;
                var nickname = command[prefixLength..].Trim();
                if (!IsSafeNickname(nickname))
                {
                    Emit("PHASE54R3_QUERY_PIN outcome=invalid");
                    continue;
                }
                var query = window.ViewModel.Sessions.EnsureQuery(workspace.Id, nickname);
                window.ViewModel.SelectView(query);
                var changed = await window.ViewModel.SetConversationPinnedAsync(query, pin).ConfigureAwait(true);
                var identityKey = NetworkSessionManager.SynchronizedPinConversationKey(query, workspace.Snapshot);
                var projected = window.ViewModel.PinnedConversations.Any(item => ReferenceEquals(item.View, query));
                Emit($"PHASE54R3_QUERY_PIN operation={(pin ? "PIN" : "UNPIN")} accepted={changed.ToString().ToLowerInvariant()} projected={projected.ToString().ToLowerInvariant()} accountKey={(identityKey is null ? "absent" : "present")} view={Fingerprint(query.Id.ToString("N"))}");
            }
            else if (command.StartsWith("CLOSE_QUERY ", StringComparison.OrdinalIgnoreCase))
            {
                var nickname = command[12..].Trim();
                var query = workspace.Queries.FirstOrDefault(candidate => candidate.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase));
                var closed = query is not null && window.ViewModel.Sessions.CloseView(query.Id);
                Emit($"PHASE54R3_QUERY_CLOSE closed={closed.ToString().ToLowerInvariant()} view={(query is null ? "none" : Fingerprint(query.Id.ToString("N")))}");
            }
            else if (command.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
            {
                EmitTrace(outbound);
            }
            else if (command.Equals("EXIT", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            else
            {
                Emit("PHASE54R3_COMMAND outcome=unknown");
            }
        }

        await window.CloseAfterSmokeAsync().ConfigureAwait(true);
        Emit("PHASE54R3_ORDINARY_HARNESS_STOPPED");
        return 0;
    }

    private static async Task ObserveConnectAsync(Task connect)
    {
        try { await connect.ConfigureAwait(true); }
        catch (Exception exception)
        {
            Emit($"PHASE54R3_CONNECT_TASK_ERROR type={exception.GetType().Name}");
        }
    }

    private static void EmitStatus(NetworkWorkspace workspace, MainWindowViewModel viewModel, OutboundTrace trace, string kind)
    {
        var snapshot = workspace.Session.Snapshot;
        var capabilities = snapshot.Capabilities;
        var localPinCount = viewModel.PinnedConversations.Count(item => item.Network.Id == workspace.Id && !item.IsSynchronized);
        Emit($"PHASE54R3_STATUS kind={kind} state={snapshot.State} registration={snapshot.Registration} tls={snapshot.Endpoint.UseTls.ToString().ToLowerInvariant()} ircd={snapshot.Identity.ProbableIrcd} pinsAdvertised={capabilities.IsAvailable(IrcCapabilityCatalog.NexIrcPins).ToString().ToLowerInvariant()} pinsEnabled={capabilities.IsEnabled(IrcCapabilityCatalog.NexIrcPins).ToString().ToLowerInvariant()} chathistory={capabilities.GetStatus(IrcCapabilityCatalog.Chathistory)} channels={workspace.Channels.Count.ToString(CultureInfo.InvariantCulture)} localPins={localPinCount.ToString(CultureInfo.InvariantCulture)}");
        EmitTrace(trace);
    }

    private static void EmitTrace(OutboundTrace trace) =>
        Emit($"PHASE54R3_TRACE commands={trace.CommandSummary()} pinNegotiationRequests={trace.PinNegotiationRequests.ToString(CultureInfo.InvariantCulture)} nexircCommands={trace.NexIrcCommands.ToString(CultureInfo.InvariantCulture)} inboundPings={trace.InboundPings.ToString(CultureInfo.InvariantCulture)}");

    private static ChannelView FindChannel(NetworkWorkspace workspace, string channelName) =>
        workspace.Channels.FirstOrDefault(candidate => candidate.Channel.Equals(channelName, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("The requested channel view is not present in this test-owned workspace.");

    private static bool IsValidChannelName(string name) => name.Length is > 1 and <= 128 && name[0] is '#' or '&'
        && !name.Any(char.IsControl) && !name.Contains(' ');

    private static bool IsSafeNickname(string name) => name.Length is > 0 and <= 64
        && !name.Any(char.IsControl) && !name.Contains(' ');

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();

    private static void Emit(string value) => Console.WriteLine(value);

    private sealed record Options(string Host, int Port, string Nickname)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < args.Length; index++)
            {
                var key = args[index];
                if (string.Equals(key, Flag, StringComparison.OrdinalIgnoreCase)) continue;
                if (!key.StartsWith("--", StringComparison.Ordinal)) continue;
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Missing value for {key}.");
                values[key] = args[++index];
            }

            string Required(string key) => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value : throw new ArgumentException($"Missing {key}.");
            var host = Required("--host");
            if (!int.TryParse(Required("--port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                throw new ArgumentException("--port must be a valid TCP port.");
            var nickname = Required("--nickname");
            if (!IsSafeNickname(nickname)) throw new ArgumentException("--nickname is invalid.");
            return new Options(host, port, nickname);
        }
    }

    private sealed class OutboundTrace
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _commands = new(StringComparer.OrdinalIgnoreCase);
        private int _pinNegotiationRequests;
        private int _nexIrcCommands;
        private int _inboundPings;

        public int PinNegotiationRequests => Volatile.Read(ref _pinNegotiationRequests);
        public int NexIrcCommands => Volatile.Read(ref _nexIrcCommands);
        public int InboundPings => Volatile.Read(ref _inboundPings);

        public void Record(string rawLine)
        {
            var command = GetCommand(rawLine);
            if (command.Length == 0) return;
            lock (_gate) _commands[command] = _commands.GetValueOrDefault(command) + 1;
            if (command.Equals("NEXIRC", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _nexIrcCommands);
            if (command.Equals("CAP", StringComparison.OrdinalIgnoreCase)
                && rawLine.Contains("nexirc/pins", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _pinNegotiationRequests);
        }

        public void RecordInbound(string rawLine)
        {
            if (GetCommand(rawLine).Equals("PING", StringComparison.OrdinalIgnoreCase))
                Interlocked.Increment(ref _inboundPings);
        }

        public string CommandSummary()
        {
            lock (_gate)
                return string.Join(',', _commands.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(static pair => $"{pair.Key}:{pair.Value.ToString(CultureInfo.InvariantCulture)}"));
        }

        private static string GetCommand(string line)
        {
            var span = line.AsSpan().TrimStart();
            if (span.IsEmpty) return string.Empty;
            if (span[0] == ':')
            {
                var prefixEnd = span.IndexOf(' ');
                if (prefixEnd < 0) return string.Empty;
                span = span[(prefixEnd + 1)..].TrimStart();
            }
            var end = span.IndexOfAny(' ', '\t');
            return (end < 0 ? span : span[..end]).ToString();
        }
    }
}
