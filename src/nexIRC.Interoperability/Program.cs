using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking;

namespace nexIRC.Interoperability;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Phase27Options options;
        try
        {
            options = Phase27Options.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Phase 27 argument error: {exception.Message}");
            Console.Error.WriteLine(Phase27Options.Usage);
            return 2;
        }

        var result = await new Phase27Harness(options).RunAsync().ConfigureAwait(false);
        Console.WriteLine($"Phase 27 outcome: {result.Outcome}");
        Console.WriteLine($"Result: {result.ResultPath}");
        if (result.TranscriptPath is not null)
        {
            Console.WriteLine($"Transcript: {result.TranscriptPath}");
        }

        return result.ExitCode;
    }
}

internal sealed record Phase27Options(
    string Server,
    int Port,
    bool UseTls,
    TimeSpan Timeout,
    string ResultPath,
    string? TranscriptPath)
{
    public const string Usage = "Usage: dotnet run --project src/nexIRC.Interoperability -- --server <host> [--port <port>] [--no-tls] [--timeout-seconds <n>] [--output <json>] [--transcript <jsonl>]";

    public static Phase27Options Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var server = "testnet.ergo.chat";
        var port = 6697;
        var useTls = true;
        var timeoutSeconds = 90;
        string? output = null;
        string? transcript = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--server" when index + 1 < args.Length:
                    server = args[++index];
                    break;
                case "--port" when index + 1 < args.Length && int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPort):
                    port = parsedPort;
                    break;
                case "--no-tls":
                    useTls = false;
                    break;
                case "--tls":
                    useTls = true;
                    break;
                case "--timeout-seconds" when index + 1 < args.Length && int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTimeout):
                    timeoutSeconds = parsedTimeout;
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--transcript" when index + 1 < args.Length:
                    transcript = args[++index];
                    break;
                case "--help":
                    throw new ArgumentException(Usage);
                default:
                    throw new ArgumentException($"Unknown or incomplete argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(server))
        {
            throw new ArgumentException("The server hostname is required.");
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "The port must be between 1 and 65535.");
        }

        if (timeoutSeconds is < 15 or > 600)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "The timeout must be between 15 and 600 seconds.");
        }

        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        output ??= Path.Combine("artifacts", "phase27", $"phase27-result-{stamp}.json");
        return new Phase27Options(server, port, useTls, TimeSpan.FromSeconds(timeoutSeconds), output, transcript);
    }
}

internal sealed class Phase27Harness
{
    private static readonly string[] RequestedCapabilities =
    [
        IrcCapabilityCatalog.MessageTags,
        IrcCapabilityCatalog.EchoMessage,
        IrcCapabilityCatalog.ServerTime,
        IrcCapabilityCatalog.Batch,
        IrcCapabilityCatalog.Chathistory,
        IrcCapabilityCatalog.EventPlayback,
        IrcCapabilityCatalog.AwayNotify,
        IrcCapabilityCatalog.ExtendedJoin,
        IrcCapabilityCatalog.MultiPrefix,
        IrcCapabilityCatalog.AccountNotify,
        IrcCapabilityCatalog.AccountTag,
        IrcCapabilityCatalog.LabeledResponse
    ];

    private readonly Phase27Options _options;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly List<string> _notes = [];
    private Phase27Client? _clientA;
    private Phase27Client? _clientB;
    private string? _parentMessageId;
    private string? _resultPath;
    private string? _transcriptPath;
    private bool _durableEvidence;

    public Phase27Harness(Phase27Options options) => _options = options;

    public async Task<Phase27RunResult> RunAsync()
    {
        var result = new Phase27RunResult
        {
            SchemaVersion = "nexIRC-phase27-v1",
            StartedAt = _startedAt,
            Endpoint = new Phase27EndpointEvidence(_options.Server, _options.Port, _options.UseTls, ClassifyEndpoint(_options.Server)),
            Outcome = "HARNESS_COMPLETE_SERVER_BLOCKED"
        };

        using var timeout = new CancellationTokenSource(_options.Timeout);
        var channel = $"#nexirc27-{RandomToken(8)}";
        var nickA = $"n27a{RandomToken(8)}";
        var nickB = $"n27b{RandomToken(8)}";
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"nexirc-phase27-{Guid.NewGuid():N}");
        Directory.CreateDirectory(evidenceRoot);

        try
        {
            _clientA = CreateClient("A", nickA, channel, Path.Combine(evidenceRoot, "a"));
            _clientB = CreateClient("B", nickB, channel, Path.Combine(evidenceRoot, "b"));
            _clientA.AttachTrace();
            _clientB.AttachTrace();
            await _clientA.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await _clientB.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await RunScenariosAsync(channel, timeout.Token).ConfigureAwait(false);

            result = result with
            {
                Outcome = "REAL_SERVER_INTEROPERABILITY_VERIFIED",
                Success = true,
                Server = BuildServerEvidence(_clientA, _clientB),
                Scenarios = BuildScenarioEvidence(),
                Notes = _notes.ToArray()
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            result = result with
            {
                Failure = $"The bounded Phase 27 run exceeded {_options.Timeout.TotalSeconds:0} seconds.",
                Notes = _notes.Append("No live success is claimed for a timed-out run.").ToArray(),
                Server = BuildServerEvidence(_clientA, _clientB),
                Scenarios = BuildScenarioEvidence()
            };
        }
        catch (Exception exception)
        {
            result = result with
            {
                Failure = $"{exception.GetType().Name}: {exception.Message}",
                Notes = _notes.Append("No live success is claimed for a failed run.").ToArray(),
                Server = BuildServerEvidence(_clientA, _clientB),
                Scenarios = BuildScenarioEvidence()
            };
        }
        finally
        {
            result = result with { Cleanup = await CleanupAsync(channel).ConfigureAwait(false) };
            try
            {
                (_resultPath, _transcriptPath) = await WriteEvidenceAsync(result, channel).ConfigureAwait(false);
                result = result with
                {
                    Evidence = new Phase27EvidencePaths(_resultPath, _transcriptPath!, channel),
                    ResultPath = _resultPath,
                    TranscriptPath = _transcriptPath,
                    FinishedAt = DateTimeOffset.UtcNow
                };
                await WriteJsonAsync(_resultPath, result).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = result with
                {
                    Success = false,
                    Failure = string.IsNullOrWhiteSpace(result.Failure)
                        ? $"Evidence write failed: {exception.Message}"
                        : $"{result.Failure}; evidence write failed: {exception.Message}"
                };
            }

            TryDeleteDirectory(evidenceRoot);
        }

        return result with
        {
            FinishedAt = result.FinishedAt == default ? DateTimeOffset.UtcNow : result.FinishedAt,
            ResultPath = _resultPath,
            TranscriptPath = _transcriptPath
        };
    }

    private Phase27Client CreateClient(string label, string nickname, string channel, string logRoot) =>
        new(
            label,
            nickname,
            channel,
            new IrcEndpoint(_options.Server, _options.Port, _options.UseTls),
            logRoot,
            RequestedCapabilities);

    private async Task RunScenariosAsync(string channel, CancellationToken cancellationToken)
    {
        var a = _clientA ?? throw new InvalidOperationException("Client A was not created.");
        var b = _clientB ?? throw new InvalidOperationException("Client B was not created.");

        await WaitForAsync(() => a.Network.Snapshot.Registration == RegistrationState.Registered, "client A registration", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => b.Network.Snapshot.Registration == RegistrationState.Registered, "client B registration", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => a.Channel.IsJoined, "client A channel join", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => b.Channel.IsJoined, "client B channel join", cancellationToken).ConfigureAwait(false);

        _notes.Add($"Temporary channel: {channel}; nicknames were randomized for this run.");
        await CaptureParentAndReplyAsync(channel, cancellationToken).ConfigureAwait(false);
        await CaptureReactionLifecycleAsync(channel, cancellationToken).ConfigureAwait(false);
        await CaptureAggregationAndMultipleValuesAsync(channel, cancellationToken).ConfigureAwait(false);
        await CaptureUnknownClientTagAsync(channel, cancellationToken).ConfigureAwait(false);
        await CaptureServerErrorAsync(channel, cancellationToken).ConfigureAwait(false);
        await CaptureReconnectAndHistoryAsync(channel, cancellationToken).ConfigureAwait(false);
        await CaptureQueryScenarioAsync(cancellationToken).ConfigureAwait(false);
        await CaptureDurableReopenAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureParentAndReplyAsync(string channel, CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        var parentText = $"phase27-parent-{RandomToken(10)}";
        var parentOutboundBefore = a.OutboundCount(line => IsPrivmsg(line, channel, parentText));
        var send = await a.Router.SendMessageAsync(a.Network, channel, parentText, a.Channel, cancellationToken).ConfigureAwait(false);
        Require(send.Succeeded, $"parent send failed: {send.Message}");
        await WaitForAsync(() => a.Channel.EntriesSnapshot.Any(entry => entry.Text == parentText && entry.ServerMessageId is not null), "canonical parent echo", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => b.Channel.EntriesSnapshot.Any(entry => entry.Text == parentText && entry.ServerMessageId is not null), "canonical parent relay", cancellationToken).ConfigureAwait(false);
        Require(a.OutboundCount(line => IsPrivmsg(line, channel, parentText)) == parentOutboundBefore + 1, "the canonical parent did not produce exactly one outbound PRIVMSG");

        var parentA = a.Channel.EntriesSnapshot.Single(entry => entry.Text == parentText && entry.ServerMessageId is not null);
        var parentB = b.Channel.EntriesSnapshot.Single(entry => entry.Text == parentText && entry.ServerMessageId is not null);
        Require(string.Equals(parentA.ServerMessageId, parentB.ServerMessageId, StringComparison.Ordinal), "the two clients observed different canonical parent msgids");
        _parentMessageId = parentA.ServerMessageId;

        var replyText = $"phase27-reply-{RandomToken(10)}";
        Require(b.Router.TryCreateReplyComposer(b.Network, b.Channel, parentB, out var composer, out var reason), reason ?? "reply composer unavailable");
        var replyOutboundBefore = b.OutboundCount(line => IsReplyPrivmsg(line, channel, _parentMessageId!, replyText));
        var reply = await b.Router.SendReplyAsync(b.Network, b.Channel, composer!, replyText, cancellationToken).ConfigureAwait(false);
        Require(reply.Succeeded, $"reply send failed: {reply.Message}");
        await WaitForAsync(() => b.OutboundCount(line => IsReplyPrivmsg(line, channel, _parentMessageId!, replyText)) == replyOutboundBefore + 1, "one tagged reply PRIVMSG", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => a.Channel.EntriesSnapshot.Any(entry => entry.Text == replyText && entry.ReplyParentMessageId == _parentMessageId && entry.ServerMessageId is not null), "reply relay and parent resolution", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => b.Channel.EntriesSnapshot.Count(entry => entry.Text == replyText && entry.ReplyParentMessageId == _parentMessageId) == 1, "reply own echo deduplication", cancellationToken).ConfigureAwait(false);
        Require(a.Channel.EntriesSnapshot.Count(entry => entry.Text == replyText && entry.ReplyParentMessageId == _parentMessageId) == 1, "reply relay produced duplicate projected rows");
    }

    private async Task CaptureReactionLifecycleAsync(string channel, CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        var parentId = _parentMessageId!;
        var parentA = FindEntry(a.Channel, parentId);
        var parentB = FindEntry(b.Channel, parentId);
        var beforeA = a.Channel.EntryCount;
        var beforeB = b.Channel.EntryCount;

        var outboundBefore = b.OutboundCount(line => IsReaction(line, channel, parentId, IrcReaction.ReactTag, "👍"));
        var reaction = await b.Router.SendReactionAsync(b.Network, b.Channel, parentB, "👍", cancellationToken: cancellationToken).ConfigureAwait(false);
        Require(reaction.Succeeded, $"reaction send failed: {reaction.Message}");
        await WaitForAsync(() => b.OutboundCount(line => IsReaction(line, channel, parentId, IrcReaction.ReactTag, "👍")) == outboundBefore + 1, "one tagged reaction TAGMSG", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 1 && ReactionCount(b.Channel, parentId, "👍") == 1, "reaction relay and own echo", cancellationToken).ConfigureAwait(false);
        Require(a.Channel.EntryCount == beforeA && b.Channel.EntryCount == beforeB, "TAGMSG reaction created a blank transcript row");
        Require(a.InboundCount(line => IsReaction(line, channel, parentId, IrcReaction.ReactTag, "👍")) > 0, "client A did not observe the relayed reaction TAGMSG");
        Require(b.InboundCount(line => IsReaction(line, channel, parentId, IrcReaction.ReactTag, "👍")) > 0, "client B did not observe its reaction echo");
        var reactionSummary = FindSummary(a.Channel, parentId, "👍");
        Require(reactionSummary is not null && reactionSummary.ActorNames.Any(name => string.Equals(name, b.Nickname, StringComparison.Ordinal)), "reaction attribution did not identify client B");

        var unreactionOutboundBefore = b.OutboundCount(line => IsReaction(line, channel, parentId, IrcReaction.UnreactTag, "👍"));
        var unreaction = await b.Router.SendReactionAsync(b.Network, b.Channel, parentB, "👍", unreaction: true, cancellationToken).ConfigureAwait(false);
        Require(unreaction.Succeeded, $"unreaction send failed: {unreaction.Message}");
        await WaitForAsync(() => b.OutboundCount(line => IsReaction(line, channel, parentId, IrcReaction.UnreactTag, "👍")) == unreactionOutboundBefore + 1, "one tagged unreaction TAGMSG", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 0 && ReactionCount(b.Channel, parentId, "👍") == 0, "unreaction relay and own echo", cancellationToken).ConfigureAwait(false);
        Require(a.Channel.EntryCount == beforeA && b.Channel.EntryCount == beforeB, "TAGMSG unreaction created a transcript row");
        Require(a.InboundCount(line => IsReaction(line, channel, parentId, IrcReaction.UnreactTag, "👍")) > 0, "client A did not observe the relayed unreaction TAGMSG");

        _notes.Add($"Canonical parent msgid: {parentId}; reply and reaction operations used the application router.");
    }

    private async Task CaptureAggregationAndMultipleValuesAsync(string channel, CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        var parentId = _parentMessageId!;
        var parentA = FindEntry(a.Channel, parentId);
        var parentB = FindEntry(b.Channel, parentId);

        Require((await a.Router.SendReactionAsync(a.Network, a.Channel, parentA, "👍", cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "client A aggregation reaction failed");
        Require((await b.Router.SendReactionAsync(b.Network, b.Channel, parentB, "👍", cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "client B aggregation reaction failed");
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 2 && ReactionCount(b.Channel, parentId, "👍") == 2, "two-actor reaction aggregation", cancellationToken).ConfigureAwait(false);
        Require((await a.Router.SendReactionAsync(a.Network, a.Channel, parentA, "👍", unreaction: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "client A aggregation unreaction failed");
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 1 && ReactionCount(b.Channel, parentId, "👍") == 1, "first actor unreaction", cancellationToken).ConfigureAwait(false);
        Require((await b.Router.SendReactionAsync(b.Network, b.Channel, parentB, "👍", unreaction: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "client B aggregation unreaction failed");
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 0 && ReactionCount(b.Channel, parentId, "👍") == 0, "second actor unreaction", cancellationToken).ConfigureAwait(false);

        Require((await a.Router.SendReactionAsync(a.Network, a.Channel, parentA, "👍", cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "multiple-value thumbs-up failed");
        Require((await a.Router.SendReactionAsync(a.Network, a.Channel, parentA, "😂", cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "multiple-value laughter failed");
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 1 && ReactionCount(a.Channel, parentId, "😂") == 1, "independent reaction values", cancellationToken).ConfigureAwait(false);
        var values = FindEntry(a.Channel, parentId).ReactionSummary.Select(item => item.Value).ToArray();
        Require(values.SequenceEqual(["👍", "😂"], StringComparer.Ordinal), "reaction ordering was not deterministic");
        Require((await a.Router.SendReactionAsync(a.Network, a.Channel, parentA, "👍", unreaction: true, cancellationToken: cancellationToken).ConfigureAwait(false)).Succeeded, "multiple-value thumbs-up removal failed");
        await WaitForAsync(() => ReactionCount(a.Channel, parentId, "👍") == 0 && ReactionCount(a.Channel, parentId, "😂") == 1, "independent removal", cancellationToken).ConfigureAwait(false);
        Require((await a.Router.SendReactionAsync(a.Network, a.Channel, parentA, "😂", unreaction: true, cancellationToken).ConfigureAwait(false)).Succeeded, "multiple-value laughter removal failed");
        await WaitForAsync(() => FindEntry(a.Channel, parentId).ReactionSummary.Count == 0, "reaction group removal", cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureUnknownClientTagAsync(string channel, CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        var tag = "+nexirc/phase27-test";
        var before = a.InboundCount(line => HasTag(line, tag));
        await b.Network.Session.SendTaggedCommandAsync(
            new Dictionary<string, string?>(StringComparer.Ordinal) { [tag] = "ok" },
            "TAGMSG",
            [channel],
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => a.InboundCount(line => HasTag(line, tag)) > before, "unknown client-only tag relay", cancellationToken).ConfigureAwait(false);
        _notes.Add("The namespaced +nexirc/phase27-test TAGMSG was relayed, demonstrating generic client-only tag relay independently of reaction parsing.");
    }

    private async Task CaptureServerErrorAsync(string channel, CancellationToken cancellationToken)
    {
        var b = _clientB!;
        var nonexistent = $"#nexirc27-nope-{RandomToken(6)}";
        await b.Network.Session.SendReactionAsync(nonexistent, "👍", _parentMessageId!, cancellationToken: cancellationToken).ConfigureAwait(false);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        var errors = b.InboundLines().Where(IsErrorLine).Select(line => line.RawLine).ToArray();
        _notes.Add(errors.Length == 0
            ? "The server returned no observable numeric/FAIL response for TAGMSG to an inaccessible temporary target."
            : $"Server error observation: {string.Join(" | ", errors.Select(Phase27Sanitizer.Sanitize))}");
    }

    private async Task CaptureReconnectAndHistoryAsync(string channel, CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        var oldGeneration = b.Network.Snapshot.ConnectionGeneration;
        await b.Manager.ReconnectAsync(b.Network.Id, cancellationToken).ConfigureAwait(false);
        b.AttachTrace();
        await WaitForAsync(() => b.Network.Snapshot.Registration == RegistrationState.Registered, "client B reconnect registration", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => b.Network.Channels.Any(item => item.IsJoined), "client B reconnect channel join", cancellationToken).ConfigureAwait(false);
        var currentChannel = b.Network.Channels.First(item => item.IsJoined);
        var parent = FindEntry(currentChannel, _parentMessageId!);
        var reconnectValue = "🎉";
        var outboundBefore = b.OutboundCount(line => IsReaction(line, channel, _parentMessageId!, IrcReaction.ReactTag, reconnectValue));
        var sent = await b.Router.SendReactionAsync(b.Network, currentChannel, parent, reconnectValue, cancellationToken: cancellationToken).ConfigureAwait(false);
        Require(sent.Succeeded, $"post-reconnect reaction failed: {sent.Message}");
        await WaitForAsync(() => b.OutboundCount(line => IsReaction(line, channel, _parentMessageId!, IrcReaction.ReactTag, reconnectValue)) == outboundBefore + 1, "one current-generation post-reconnect reaction", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => ReactionCount(a.Channel, _parentMessageId!, reconnectValue) == 1, "post-reconnect reaction relay", cancellationToken).ConfigureAwait(false);
        _notes.Add($"Reconnect completed; old generation {oldGeneration}, current generation {b.Network.Snapshot.ConnectionGeneration}; one post-reconnect reaction was relayed.");

        await a.Manager.DisconnectAsync(a.Network.Id).ConfigureAwait(false);
        a.AttachTrace();
        var offlineMessage = $"phase27-history-extra-{RandomToken(10)}";
        var send = await b.Router.SendMessageAsync(b.Network, channel, offlineMessage, currentChannel, cancellationToken).ConfigureAwait(false);
        Require(send.Succeeded, $"history extra message failed: {send.Message}");
        await WaitForAsync(() => currentChannel.EntriesSnapshot.Any(entry => entry.Text == offlineMessage && entry.ServerMessageId is not null), "history extra message echo", cancellationToken).ConfigureAwait(false);
        await a.Manager.ReconnectAsync(a.Network.Id, cancellationToken).ConfigureAwait(false);
        a.AttachTrace();
        await WaitForAsync(() => a.Network.Snapshot.Registration == RegistrationState.Registered, "client A reconnect registration", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => a.Network.Channels.Any(item => item.IsJoined), "client A reconnect channel join", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => !a.Network.Session.GetChathistoryState(channel).RequestActive, "automatic reconnect history completion", cancellationToken).ConfigureAwait(false);

        var historyRequest = new ChathistoryRequest
        {
            NetworkId = a.Network.Id,
            ConnectionGeneration = a.Network.Snapshot.ConnectionGeneration,
            Conversation = channel,
            Target = channel,
            Operation = ChathistoryOperation.Latest,
            Reference = ChathistoryReference.Wildcard,
            Limit = 30,
            Purpose = ChathistoryRequestPurpose.LoadContext
        };
        ChathistoryResult history = default!;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                history = await a.Network.Session.RequestHistoryAsync(historyRequest, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("already active", StringComparison.OrdinalIgnoreCase)
                && attempt < 8)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
            }
        }
        Require(history.Succeeded, $"CHATHISTORY request failed: {history.Failure ?? history.Completion.ToString()}");
        var historyHasParent = history.Messages.Any(item => item.Message.ServerMessageId == _parentMessageId);
        var historyHasReaction = history.Messages.Any(item => item is IrcReactionEvent);
        _notes.Add($"CHATHISTORY LATEST returned {history.Messages.Count} semantic events; parent={(historyHasParent ? "present" : "absent")}; reaction={(historyHasReaction ? "present" : "absent")}; explicit-end={history.HistoryEndSignaled}.");
    }

    private async Task CaptureQueryScenarioAsync(CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        var peerA = b.Nickname;
        var peerB = a.Nickname;
        var queryA = a.Router.OpenQuery(a.Network, peerA);
        var dmParentText = $"phase27-dm-parent-{RandomToken(10)}";
        var sent = await a.Router.SendMessageAsync(a.Network, peerA, dmParentText, queryA, cancellationToken).ConfigureAwait(false);
        Require(sent.Succeeded, $"DM parent send failed: {sent.Message}");
        await WaitForAsync(() => queryA.EntriesSnapshot.Any(entry => entry.Text == dmParentText && entry.ServerMessageId is not null), "DM parent own echo", cancellationToken).ConfigureAwait(false);
        await WaitForAsync(() => b.Network.Queries.Any(query => string.Equals(query.Nickname, peerB, StringComparison.OrdinalIgnoreCase)
            && query.EntriesSnapshot.Any(entry => entry.Text == dmParentText && entry.ServerMessageId is not null)), "DM parent relay", cancellationToken).ConfigureAwait(false);
        var queryB = b.Network.Queries.First(query => string.Equals(query.Nickname, peerB, StringComparison.OrdinalIgnoreCase));
        var parentA = queryA.EntriesSnapshot.Single(entry => entry.Text == dmParentText && entry.ServerMessageId is not null);
        var parentB = queryB.EntriesSnapshot.Single(entry => entry.Text == dmParentText && entry.ServerMessageId is not null);
        Require(string.Equals(parentA.ServerMessageId, parentB.ServerMessageId, StringComparison.Ordinal), "DM parent msgid differed between clients");

        Require(b.Router.TryCreateReplyComposer(b.Network, queryB, parentB, out var composer, out var reason), reason ?? "DM reply composer unavailable");
        var dmReplyText = $"phase27-dm-reply-{RandomToken(10)}";
        var reply = await b.Router.SendReplyAsync(b.Network, queryB, composer!, dmReplyText, cancellationToken).ConfigureAwait(false);
        Require(reply.Succeeded, $"DM reply failed: {reply.Message}");
        await WaitForAsync(() => queryA.EntriesSnapshot.Any(entry => entry.Text == dmReplyText && entry.ReplyParentMessageId == parentA.ServerMessageId), "DM reply relay", cancellationToken).ConfigureAwait(false);

        var before = queryA.EntryCount;
        var reaction = await b.Router.SendReactionAsync(b.Network, queryB, parentB, "👍", cancellationToken: cancellationToken).ConfigureAwait(false);
        Require(reaction.Succeeded, $"DM reaction failed: {reaction.Message}");
        await WaitForAsync(() => ReactionCount(queryA, parentA.ServerMessageId!, "👍") == 1, "DM reaction relay", cancellationToken).ConfigureAwait(false);
        Require(queryA.EntryCount == before, "DM reaction created a blank query row");
        var unreaction = await b.Router.SendReactionAsync(b.Network, queryB, parentB, "👍", unreaction: true, cancellationToken).ConfigureAwait(false);
        Require(unreaction.Succeeded, $"DM unreaction failed: {unreaction.Message}");
        await WaitForAsync(() => ReactionCount(queryA, parentA.ServerMessageId!, "👍") == 0, "DM unreaction relay", cancellationToken).ConfigureAwait(false);
        Require(queryA.EntriesSnapshot.All(entry => !entry.Text.StartsWith("phase27-parent-", StringComparison.Ordinal)), "DM query leaked channel content");

        await a.Store.FlushAsync(cancellationToken).ConfigureAwait(false);
        var search = await a.Store.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.AllHistory,
            NetworkId = a.Network.Id,
            ConversationKind = LogConversationKind.PrivateConversation,
            ConversationName = peerA,
            ConversationKey = queryA.HistoryConversationKey,
            Text = dmReplyText,
            MaximumResults = 10
        }, cancellationToken).ConfigureAwait(false);
        var broadSearch = await a.Store.SearchDetailedAsync(new ConversationLogQuery
        {
            Scope = ConversationLogSearchScope.AllHistory,
            NetworkId = a.Network.Id,
            Text = dmReplyText,
            MaximumResults = 10
        }, cancellationToken).ConfigureAwait(false);
        var exact = search.Results.FirstOrDefault(item => item.Record.Text == dmReplyText);
        if (exact is null)
        {
            var broad = broadSearch.Results.FirstOrDefault(item => item.Record.Text == dmReplyText);
            var queryPage = await a.Store.ReadPageWindowAsync(new HistoryPageRequest
            {
                ScopeId = a.Network.Id,
                NetworkId = a.Network.Id,
                ConversationKind = LogConversationKind.PrivateConversation,
                ConversationName = peerA,
                ConversationKey = queryA.HistoryConversationKey,
                PageSize = 100
            }, cancellationToken).ConfigureAwait(false);
            var queryKeys = string.Join(",", a.Network.Queries.Select(query => query.HistoryConversationKey));
            var logFiles = Directory.Exists(a.LogRoot)
                ? Directory.EnumerateFiles(a.LogRoot, "*.jsonl", SearchOption.AllDirectories).ToArray()
                : Array.Empty<string>();
            var rawMatches = logFiles.Sum(path => File.ReadLines(path).Count(line => line.Contains(dmReplyText, StringComparison.Ordinal)));
            _notes.Add($"Durable DM diagnostic: query-entries={queryA.EntryCount}, page-records={queryPage.Records.Count}, broad={broadSearch.Statistics.MatchingRecords}, keys={queryKeys}, raw-files={logFiles.Length}, raw-matches={rawMatches}, log-diagnostic={a.Store.LastDiagnostic ?? "<none>"}.");
            throw new InvalidOperationException(
                $"DM reply was not searchable in its durable query identity; exact={search.Statistics.MatchingRecords}, broad={broadSearch.Statistics.MatchingRecords}, "
                + $"query-key={queryA.HistoryConversationKey}, broad-key={broad?.Location.ConversationKey ?? "<none>"}, broad-name={broad?.Record.ConversationName ?? "<none>"}");
        }

        var navigation = await a.Manager.NavigateToHistorySearchResultAsync(a.Network, queryA, exact, cancellationToken).ConfigureAwait(false);
        Require(navigation.Succeeded, $"DM search/jump failed: {navigation.Message}");
        _notes.Add($"DM query identity {queryA.HistoryConversationKey} retained independent reply/reaction routing and local search navigation.");
    }

    private async Task CaptureDurableReopenAsync(CancellationToken cancellationToken)
    {
        var a = _clientA!;
        var b = _clientB!;
        await a.Store.FlushAsync(cancellationToken).ConfigureAwait(false);
        await b.Store.FlushAsync(cancellationToken).ConfigureAwait(false);
        var aRecords = await a.Store.ReadReactionEventsAsync(
            a.Network.Id,
            a.Network.Id,
            LogConversationKind.Channel,
            a.Channel.Channel,
            ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, a.Channel.Channel),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Require(aRecords.Count > 0, "no channel reaction records were persisted");
        await using (var reopened = new JsonlConversationLogStore(a.Store.RootPath))
        {
            var reopenedRecords = await reopened.ReadReactionEventsAsync(
                a.Network.Id,
                a.Network.Id,
                LogConversationKind.Channel,
                a.Channel.Channel,
                ConversationLoggingService.BuildConversationKey(LogConversationKind.Channel, a.Channel.Channel),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Require(reopenedRecords.Count >= aRecords.Count, "reopened JSONL store did not retain the persisted reaction records");
            _durableEvidence = true;
            _notes.Add($"Reopened JSONL store retained {reopenedRecords.Count} channel reaction records after the original store was flushed; deterministic state hydration tests cover projection reconstruction.");
        }
    }

    private async Task<Phase27CleanupEvidence> CleanupAsync(string channel)
    {
        var clients = new[] { _clientA, _clientB }.OfType<Phase27Client>().ToArray();
        var partCount = 0;
        var disposedCount = 0;
        try
        {
            foreach (var client in clients)
            {
                try
                {
                    if (client.Network.Snapshot.Registration == RegistrationState.Registered && client.Channel.IsJoined)
                    {
                        await client.Network.Session.PartChannelAsync(channel).ConfigureAwait(false);
                        partCount++;
                    }
                }
                catch
                {
                    // QUIT/disposal remains the cleanup boundary if PART races shutdown.
                }
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                    disposedCount++;
                }
                catch (Exception exception)
                {
                    _notes.Add($"Cleanup warning for client {client.Label}: {exception.Message}");
                }
            }
        }

        return new Phase27CleanupEvidence(partCount == clients.Length, disposedCount == clients.Length, disposedCount == clients.Length);
    }

    private async Task<(string ResultPath, string? TranscriptPath)> WriteEvidenceAsync(Phase27RunResult result, string channel)
    {
        var resultPath = Path.GetFullPath(_options.ResultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var transcriptPath = _options.TranscriptPath is null
            ? Path.ChangeExtension(resultPath, ".transcript.jsonl")
            : Path.GetFullPath(_options.TranscriptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        var entries = (_clientA?.Trace.Entries ?? []).Concat(_clientB?.Trace.Entries ?? [])
            .Where(IsRelevantTrace)
            .OrderBy(item => item.Timestamp)
            .Select(item => item.ToTranscriptEntry())
            .ToArray();
        await IrcTranscriptFile.WriteAsync(transcriptPath, entries).ConfigureAwait(false);
        var finalized = result with { Evidence = new Phase27EvidencePaths(resultPath, transcriptPath, channel) };
        await WriteJsonAsync(resultPath, finalized).ConfigureAwait(false);
        return (resultPath, transcriptPath);
    }

    private Phase27ServerEvidence? BuildServerEvidence(Phase27Client? a, Phase27Client? b)
    {
        if (a is null || b is null)
        {
            return null;
        }

        var snapshot = a.Network.Snapshot;
        var history = snapshot.Features.Chathistory;
        var deny = snapshot.Features.RuntimeISupport;
        return new Phase27ServerEvidence(
            ParseServerVersion(a, b),
            snapshot.Capabilities.RawAdvertisedTokens.ToArray(),
            snapshot.Capabilities.Enabled.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            snapshot.Capabilities.Rejected.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            snapshot.ISupport.RawTokens.Select(token => token.RawToken).ToArray(),
            ClassifyClientTagDeny(deny),
            deny.ClientTagDenyEntries.ToArray(),
            new Phase27HistoryEvidence(
                history.CapabilityEnabled,
                history.BatchEnabled,
                history.ServerTimeEnabled,
                history.MessageTagsEnabled,
                history.EventPlaybackEnabled,
                snapshot.ISupport.ChathistoryLimit,
                history.SupportedReferenceTypes.Select(value => value.ToString()).ToArray()),
            snapshot.Identity.ProbableIrcd.ToString(),
            ClassifyReplySupport(a, b, deny),
            ClassifyReactionSupport(a, b, deny));
    }

    private Phase27ScenarioEvidence BuildScenarioEvidence() => new(
        _parentMessageId,
        _clientA is not null && _clientB is not null && _parentMessageId is not null,
        _clientB?.OutboundCount(line => line.RawLine.Contains("PRIVMSG", StringComparison.Ordinal) && line.RawLine.Contains("+reply=", StringComparison.Ordinal)) > 0,
        _clientA?.InboundCount(line => line.RawLine.Contains("PRIVMSG", StringComparison.Ordinal) && line.RawLine.Contains("+reply=", StringComparison.Ordinal)) > 0,
        _clientA?.InboundCount(line => line.RawLine.Contains(IrcReaction.ReactTag, StringComparison.Ordinal)) > 0,
        _clientB?.InboundCount(line => line.RawLine.Contains(IrcReaction.ReactTag, StringComparison.Ordinal)) > 0,
        _clientA?.InboundCount(line => line.RawLine.Contains(IrcReaction.UnreactTag, StringComparison.Ordinal)) > 0,
        _clientA?.Channel.EntryCount > 0,
        _clientA?.Network.Queries.Any(query => query.EntriesSnapshot.Any(entry => entry.ReplyParentMessageId is not null)) == true,
        _clientA?.Network.Queries.Any(query => query.EntriesSnapshot.Any(entry => entry.Text.Contains("phase27-dm-parent", StringComparison.Ordinal))) == true,
        _clientA?.Network.Snapshot.State == ServerSessionState.Registered,
        _durableEvidence,
        "Unavailable: a safe live missing-parent projection was not reproducible; deterministic Phase 25/26 recovery tests remain authoritative.");

    private static Phase27ClientTagDenyKind ClassifyClientTagDeny(ISupportSnapshot snapshot)
    {
        if (!snapshot.HasClientTagDeny)
        {
            return Phase27ClientTagDenyKind.Absent;
        }

        if (snapshot.ClientTagDenyEntries.Count == 0)
        {
            return Phase27ClientTagDenyKind.Empty;
        }

        if (snapshot.ClientTagDenyEntries.Contains("*", StringComparer.Ordinal))
        {
            return snapshot.ClientTagDenyEntries.Any(entry => entry.Length > 0 && entry[0] == '-')
                ? Phase27ClientTagDenyKind.WildcardWithExceptions
                : Phase27ClientTagDenyKind.Wildcard;
        }

        return Phase27ClientTagDenyKind.ExplicitList;
    }

    private static string ClassifyReplySupport(Phase27Client a, Phase27Client b, ISupportSnapshot deny)
    {
        if (!a.Network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.MessageTags))
        {
            return "blocked-by-capability";
        }

        if (deny.IsClientTagDenied("+reply") || deny.IsClientTagDenied("reply"))
        {
            return "blocked-by-clienttagdeny";
        }

        var outbound = b.OutboundCount(line => line.RawLine.Contains("PRIVMSG", StringComparison.Ordinal)
            && line.RawLine.Contains("+reply=", StringComparison.Ordinal));
        var relay = a.InboundCount(line => line.RawLine.Contains("PRIVMSG", StringComparison.Ordinal)
            && line.RawLine.Contains("+reply=", StringComparison.Ordinal));
        return outbound > 0 && relay > 0
            ? "outbound-supported-and-relayed"
            : outbound > 0 ? "outbound-observed-relay-not-observed" : "indeterminate";
    }

    private static string ClassifyReactionSupport(Phase27Client a, Phase27Client b, ISupportSnapshot deny)
    {
        if (!a.Network.Snapshot.Capabilities.IsEnabled(IrcCapabilityCatalog.MessageTags))
        {
            return "blocked-by-capability";
        }

        if (deny.IsClientTagDenied("+reply")
            || deny.IsClientTagDenied("reply")
            || deny.IsClientTagDenied(IrcReaction.ReactTag)
            || deny.IsClientTagDenied(IrcReaction.UnreactTag)
            || deny.IsClientTagDenied("draft/react")
            || deny.IsClientTagDenied("draft/unreact"))
        {
            return "blocked-by-clienttagdeny";
        }

        var outbound = b.OutboundCount(line => line.RawLine.Contains("TAGMSG", StringComparison.Ordinal)
            && line.RawLine.Contains(IrcReaction.ReactTag, StringComparison.Ordinal));
        var relay = a.InboundCount(line => line.RawLine.Contains("TAGMSG", StringComparison.Ordinal)
            && line.RawLine.Contains(IrcReaction.ReactTag, StringComparison.Ordinal));
        return outbound > 0 && relay > 0
            ? "outbound-supported-and-relayed"
            : outbound > 0 ? "outbound-observed-relay-not-observed" : "indeterminate";
    }

    private static string ParseServerVersion(Phase27Client a, Phase27Client b)
    {
        foreach (var line in a.InboundLines().Concat(b.InboundLines()))
        {
            var parsed = IrcMessageParser.Parse(line.RawLine);
            if (parsed.Success && parsed.Message?.NumericCommand == 2 && parsed.Message.TrailingParameter is { } text)
            {
                var match = Regex.Match(text, @"running version\s+(?<version>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (match.Success)
                {
                    return match.Groups["version"].Value;
                }
            }
        }

        return "unknown";
    }

    private static string ClassifyEndpoint(string server) =>
        IPAddress.TryParse(server, out var address) && IPAddress.IsLoopback(address)
            || string.Equals(server, "localhost", StringComparison.OrdinalIgnoreCase)
            ? "local"
            : "official-testnet-or-remote";

    private static bool IsRelevantTrace(Phase27TraceLine line)
    {
        var parsed = IrcMessageParser.Parse(line.RawLine);
        if (!parsed.Success || parsed.Message is null)
        {
            return false;
        }

        var message = parsed.Message;
        return message.Command is "CAP" or "JOIN" or "PART" or "PRIVMSG" or "TAGMSG" or "BATCH" or "CHATHISTORY" or "FAIL"
            || message.NumericCommand == 5
            || message.NumericCommand is 400 or 401 or 402 or 403 or 404 or 405 or 407 or 409 or 411 or 412 or 421 or 461;
    }

    private static bool IsPrivmsg(Phase27TraceLine line, string target, string text)
    {
        var parsed = IrcMessageParser.Parse(line.RawLine);
        return parsed.Success && parsed.Message is { Command: "PRIVMSG" } message
            && line.Direction == IrcTranscriptDirection.Outbound
            && message.Parameters.Count >= 2
            && string.Equals(message.Parameters[0], target, StringComparison.Ordinal)
            && string.Equals(message.TrailingParameter, text, StringComparison.Ordinal);
    }

    private static bool IsReplyPrivmsg(Phase27TraceLine line, string target, string parentId, string text)
    {
        var parsed = IrcMessageParser.Parse(line.RawLine);
        return IsPrivmsg(line, target, text)
            && parsed.Message?.ReplyParentMessageId == parentId;
    }

    private static bool IsReaction(Phase27TraceLine line, string target, string parentId, string tag, string value)
    {
        var parsed = IrcMessageParser.Parse(line.RawLine);
        return parsed.Success && parsed.Message is { Command: "TAGMSG" } message
            && line.Direction is IrcTranscriptDirection.Inbound or IrcTranscriptDirection.Outbound
            && message.Parameters.Count >= 1
            && string.Equals(message.Parameters[0], target, StringComparison.Ordinal)
            && message.ReplyParentMessageId == parentId
            && message.TagValues.TryGetValue(tag, out var actual)
            && string.Equals(actual, value, StringComparison.Ordinal);
    }

    private static bool HasTag(Phase27TraceLine line, string tag)
    {
        var parsed = IrcMessageParser.Parse(line.RawLine);
        return parsed.Success && parsed.Message?.TagValues.ContainsKey(tag) == true;
    }

    private static bool IsErrorLine(Phase27TraceLine line)
    {
        var parsed = IrcMessageParser.Parse(line.RawLine);
        return parsed.Success && (parsed.Message?.Command == "FAIL" || parsed.Message?.NumericCommand is >= 400 and <= 599);
    }

    private static TranscriptEntry FindEntry(WorkspaceView view, string messageId) =>
        view.EntriesSnapshot.Single(entry => string.Equals(entry.ServerMessageId, messageId, StringComparison.Ordinal));

    private static int ReactionCount(WorkspaceView view, string messageId, string value) =>
        FindSummary(view, messageId, value)?.Count ?? 0;

    private static ReactionSummaryItem? FindSummary(WorkspaceView view, string messageId, string value) =>
        FindEntry(view, messageId).ReactionSummary.SingleOrDefault(item => string.Equals(item.Value, value, StringComparison.Ordinal));

    private static async Task WaitForAsync(Func<bool> condition, string description, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException($"Phase 27 condition was not reached: {description}.");
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string RandomToken(int length) =>
        Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..length];

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Evidence is already outside this disposable run directory.
        }
    }

    private static Task WriteJsonAsync(string path, Phase27RunResult value) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Phase27Json.Options));
}

internal sealed class Phase27Client : IAsyncDisposable
{
    private readonly object _traceGate = new();
    private readonly List<ServerSession> _tracedSessions = [];

    public Phase27Client(string label, string nickname, string channel, IrcEndpoint endpoint, string logRoot, IReadOnlyList<string> requestedCapabilities)
    {
        Label = label;
        Nickname = nickname;
        LogRoot = logRoot;
        var configuration = new ConfigurationService(new InMemoryConfigurationStore(new NexIrcConfiguration
        {
            Preferences = new ApplicationPreferences
            {
                ConversationLoggingEnabled = true,
                PrivateMessageLoggingEnabled = true,
                StatusLoggingEnabled = true
            }
        }));
        configuration.SetPreferences(new ApplicationPreferences
        {
            ConversationLoggingEnabled = true,
            PrivateMessageLoggingEnabled = true,
            StatusLoggingEnabled = true
        });
        Store = new JsonlConversationLogStore(logRoot);
        Manager = new NetworkSessionManager(new TcpTlsIrcTransportFactory(), configuration: configuration, logStore: Store);
        Network = Manager.Add(new NetworkConnectionOptions
        {
            DisplayName = $"Phase 27 client {label}",
            Endpoint = endpoint,
            Nickname = nickname,
            Username = nickname,
            RealName = "nexIRC 5 Phase 27 interoperability test",
            RequestedCapabilities = requestedCapabilities,
            DesiredChannels = new HashSet<string>(StringComparer.Ordinal) { channel },
            Reconnect = new ReconnectPolicy(Enabled: false),
            MaximumChathistoryRequestSize = 50,
            ChathistoryRequestTimeout = TimeSpan.FromSeconds(8)
        });
        Router = new WorkspaceActionRouter(Manager);
        Channel = Network.Channels.Single();
        Trace = new Phase27TraceBuffer();
    }

    public string Label { get; }
    public string Nickname { get; }
    public string LogRoot { get; }
    public NetworkSessionManager Manager { get; }
    public NetworkWorkspace Network { get; }
    public WorkspaceActionRouter Router { get; }
    public ChannelView Channel { get; }
    public JsonlConversationLogStore Store { get; }
    public Phase27TraceBuffer Trace { get; }

    public async Task ConnectAsync(CancellationToken cancellationToken) =>
        await Manager.ConnectAsync(Network.Id, cancellationToken).ConfigureAwait(false);

    public void AttachTrace()
    {
        var session = Network.Session;
        lock (_traceGate)
        {
            session.RawLineReceived += OnRawLineReceived;
            session.OutboundCommandSent += OnOutboundCommandSent;
            _tracedSessions.Add(session);
        }
    }

    public IReadOnlyList<Phase27TraceLine> InboundLines() => Trace.Snapshot().Where(item => item.Direction == IrcTranscriptDirection.Inbound).ToArray();
    public int InboundCount(Func<Phase27TraceLine, bool> predicate) => InboundLines().Count(predicate);
    public int OutboundCount(Func<Phase27TraceLine, bool> predicate) => Trace.Snapshot().Count(item => item.Direction == IrcTranscriptDirection.Outbound && predicate(item));

    private void OnRawLineReceived(object? sender, RawIrcLineEvent item)
    {
        Trace.Add(new Phase27TraceLine(Label, IrcTranscriptDirection.Inbound, item.ReceivedAt, item.RawLine, item.RawBytes, item.ConnectionGeneration));
    }

    private void OnOutboundCommandSent(object? sender, OutboundIrcCommandEvent item)
    {
        Trace.Add(new Phase27TraceLine(Label, IrcTranscriptDirection.Outbound, item.SentAt, item.RawLine, item.RawBytes, item.ConnectionGeneration));
    }

    public async ValueTask DisposeAsync()
    {
        lock (_traceGate)
        {
            foreach (var session in _tracedSessions)
            {
                session.RawLineReceived -= OnRawLineReceived;
                session.OutboundCommandSent -= OnOutboundCommandSent;
            }

            _tracedSessions.Clear();
        }

        await Manager.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class Phase27TraceBuffer
{
    private readonly object _gate = new();
    private readonly List<Phase27TraceLine> _entries = [];

    public void Add(Phase27TraceLine entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    public IReadOnlyList<Phase27TraceLine> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public IReadOnlyList<Phase27TraceLine> Entries => Snapshot();
}

internal sealed record Phase27TraceLine(
    string Client,
    IrcTranscriptDirection Direction,
    DateTimeOffset Timestamp,
    string RawLine,
    ReadOnlyMemory<byte> RawBytes,
    int ConnectionGeneration)
{
    public IrcTranscriptEntry ToTranscriptEntry()
    {
        var sanitized = Phase27Sanitizer.Sanitize(RawLine);
        var bytes = System.Text.Encoding.UTF8.GetBytes(sanitized + "\r\n");
        return new IrcTranscriptEntry(Timestamp, Direction, sanitized, Convert.ToBase64String(bytes), ConnectionGeneration);
    }
}

internal static class Phase27Sanitizer
{
    private static readonly Regex IpAddress = new(@"(?<![A-Za-z0-9])(?:\d{1,3}\.){3}\d{1,3}(?![A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VapidKey = new(@"(?<=\bVAPID=)[^\s]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Sanitize(string line)
    {
        var safe = IrcSensitiveData.RedactLine(line);
        safe = IpAddress.Replace(safe, "<ip>");
        return VapidKey.Replace(safe, "<redacted-public-key>");
    }
}

internal sealed record Phase27RunResult
{
    public required string SchemaVersion { get; init; }
    public bool Success { get; init; }
    public required string Outcome { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public required Phase27EndpointEvidence Endpoint { get; init; }
    public Phase27ServerEvidence? Server { get; init; }
    public Phase27ScenarioEvidence? Scenarios { get; init; }
    public Phase27CleanupEvidence Cleanup { get; init; } = new(false, false, false);
    public Phase27EvidencePaths? Evidence { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public string? Failure { get; init; }
    public string? ResultPath { get; init; }
    public string? TranscriptPath { get; init; }

    [JsonIgnore]
    public int ExitCode => Success ? 0 : 1;
}

internal sealed record Phase27EndpointEvidence(string Server, int Port, bool Tls, string Category);
internal sealed record Phase27ServerEvidence(
    string Version,
    IReadOnlyList<string> AdvertisedCapabilities,
    IReadOnlyList<string> EnabledCapabilities,
    IReadOnlyList<string> RejectedCapabilities,
    IReadOnlyList<string> Isupport,
    Phase27ClientTagDenyKind ClientTagDeny,
    IReadOnlyList<string> ClientTagDenyEntries,
    Phase27HistoryEvidence History,
    string ProbableIrcd,
    string ReplySupport,
    string ReactionSupport);
internal sealed record Phase27HistoryEvidence(
    bool CapabilityEnabled,
    bool BatchEnabled,
    bool ServerTimeEnabled,
    bool MessageTagsEnabled,
    bool EventPlaybackEnabled,
    int? ServerLimit,
    IReadOnlyList<string> ReferenceTypes);
internal enum Phase27ClientTagDenyKind
{
    Absent,
    Empty,
    ExplicitList,
    Wildcard,
    WildcardWithExceptions
}
internal sealed record Phase27ScenarioEvidence(
    string? CanonicalParentMessageId,
    bool ChannelParent,
    bool ReplyOutbound,
    bool ReplyRelay,
    bool ReactionRelay,
    bool ReactionEcho,
    bool UnreactionRelay,
    bool NoBlankTagmsgRows,
    bool QueryReply,
    bool QueryReaction,
    bool Reconnect,
    bool DurableEvidence,
    string MissingParent);
internal sealed record Phase27CleanupEvidence(bool PartSent, bool QuitOrDisposeCompleted, bool ClientsDisposed);
internal sealed record Phase27EvidencePaths(string ResultJson, string TranscriptJsonl, string TemporaryChannel);

internal static class Phase27Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}
