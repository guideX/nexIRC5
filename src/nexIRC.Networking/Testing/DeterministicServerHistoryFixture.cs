using System.Globalization;
using System.Text;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;

namespace nexIRC.Networking.Testing;

public enum DeterministicReplayProfile
{
    FullIrcv3Replay,
    NoHistory,
    AdvertisedHistoryFails,
    BoundedPartialReplay,
    ExactGap,
    MissingParentRecoverable,
    MissingParentUnrecoverable,
    NativeResume,
    NativeResumeRejects,
    NativeResumeExpired,
    NativeResumeAccountMismatch,
    NativeResumeBoundaryTooOld,
    NativeResumeInvalidated,
    NativeResumeServerRestarted,
    NativeResumeOutOfOrder,
    NativeResumeMalformedCompletion,
    NativeResumeDisconnectDuringReplay,
    NativeResumeConcurrentReject
}

public enum DeterministicHistoryResponseMode
{
    Complete,
    Empty,
    BoundedPartial,
    Fail,
    DisconnectDuringResponse,
    Malformed
}

/// <summary>
/// Canonical, opaque server history input for deterministic replay tests.
/// The fixture never orders or interprets message ids numerically.
/// </summary>
public sealed record DeterministicServerHistoryEvent(
    string ConversationTarget,
    string Sender,
    string CanonicalMessageId,
    DateTimeOffset ServerTime,
    string Command = "PRIVMSG",
    string? Body = null,
    string? Account = null,
    IReadOnlyDictionary<string, string?>? Tags = null,
    string? RelationshipMetadata = null,
    string? ResumeSequence = null,
    string? ResumePreviousSequence = null,
    string? ResumeConversation = null)
{
    public string EffectiveBody => Body ?? string.Empty;
}

public sealed record DeterministicHistoryFixtureOptions
{
    public required IrcEndpoint Endpoint { get; init; }

    public string ServerName { get; init; } = "deterministic.fixture";

    public string Nickname { get; init; } = "nex";

    public DeterministicReplayProfile Profile { get; init; } = DeterministicReplayProfile.FullIrcv3Replay;

    public DeterministicHistoryResponseMode ResponseMode { get; init; } = DeterministicHistoryResponseMode.Complete;

    public IReadOnlyList<DeterministicServerHistoryEvent> History { get; init; } = Array.Empty<DeterministicServerHistoryEvent>();

    public IReadOnlySet<string> AdditionalCapabilities { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public string NativeResumeToken { get; init; } = "fixture-resume-token";

    public string NativeResumeInitialBoundary { get; init; } = "resume-0";
}

/// <summary>
/// In-process IRC behavior simulator for recovery tests. It covers only the
/// negotiated capabilities, registration, CHATHISTORY forms, BATCH framing,
/// live events, and controlled failure/disconnect behavior needed by nexIRC.
/// It is not a production IRC server.
/// </summary>
public sealed class DeterministicServerHistoryFixture : IAsyncDisposable
{
    private readonly DeterministicHistoryFixtureOptions _options;
    private readonly List<DeterministicServerHistoryEvent> _history;
    private readonly List<string> _historyRequests = [];
    private readonly List<string> _nativeResumeRequests = [];
    private readonly Queue<DeterministicServerHistoryEvent> _heldLiveEvents = [];
    private readonly Queue<DeterministicServerHistoryEvent> _scheduledLiveDuringReplay = [];
    private int _batchSequence;
    private int _resumeSequence;
    private bool _welcomeSent;
    private bool _nativeClientEnabled;
    private bool _nativeReplayInProgress;
    private bool _resumeStateAvailable = true;
    private bool _resumeOwnerActive;
    private string? _serverBoundary;

    public DeterministicServerHistoryFixture(DeterministicHistoryFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _history = options.History.ToList();
        _serverBoundary = options.NativeResumeInitialBoundary;
        Transport = new FakeIrcTransport(options.Endpoint);
        Transport.OutboundLineWritten += OnOutboundLineWritten;
    }

    public FakeIrcTransport Transport { get; }

    public IReadOnlyList<DeterministicServerHistoryEvent> History => _history;

    public IReadOnlyList<string> HistoryRequests => _historyRequests;

    public IReadOnlyList<string> NativeResumeRequests => _nativeResumeRequests;

    public string NativeResumeToken => _options.NativeResumeToken;

    public string CurrentResumeBoundary => _serverBoundary ?? _options.NativeResumeInitialBoundary;

    public bool NativeReplayInProgress => _nativeReplayInProgress;

    public void LoseNativeResumeState() => _resumeStateAvailable = false;

    public void ExpireNativeResumeToken() => _resumeStateAvailable = false;

    public void InvalidateNativeResumeOwner() => _resumeOwnerActive = false;

    public void AddHistory(params DeterministicServerHistoryEvent[] events)
    {
        ArgumentNullException.ThrowIfNull(events);
        foreach (var item in events)
        {
            var prepared = IsNativeResumeProfile() ? PrepareResumeEvent(item) : item;
            _history.Add(prepared);
        }
    }

    public void EnqueueLive(DeterministicServerHistoryEvent item)
    {
        var prepared = IsNativeResumeProfile() ? PrepareResumeEvent(item) : item;
        if (IsNativeResumeProfile())
        {
            _history.Add(prepared);
        }
        if (_nativeReplayInProgress)
        {
            _heldLiveEvents.Enqueue(prepared);
            return;
        }

        Transport.EnqueueInboundLine(RenderEvent(prepared, batchId: null));
    }

    public void EnqueueLiveDuringNextNativeReplay(DeterministicServerHistoryEvent item) =>
        _scheduledLiveDuringReplay.Enqueue(item);

    public void EnqueueDisconnect() => Transport.EnqueueRemoteDisconnect();

    public void EnqueueMalformedHistoryLine(string line) => Transport.EnqueueInboundLine(line);

    public void EnqueueNativeReplayCompletion(string boundary) =>
        Transport.EnqueueInboundLine($":{_options.ServerName} NEXIRC RESUME COMPLETE {boundary}");

    public async ValueTask DisposeAsync()
    {
        Transport.OutboundLineWritten -= OnOutboundLineWritten;
        await Transport.DisposeAsync().ConfigureAwait(false);
    }

    private void OnOutboundLineWritten(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return;
        }

        switch (parts[0].ToUpperInvariant())
        {
            case "CAP" when parts.Length > 1 && parts[1].Equals("LS", StringComparison.OrdinalIgnoreCase):
                Transport.EnqueueInboundLine($":{_options.ServerName} CAP * LS :{string.Join(' ', AdvertisedCapabilities())}");
                break;
            case "CAP" when parts.Length > 1 && parts[1].Equals("REQ", StringComparison.OrdinalIgnoreCase):
                var requested = line.Contains(" :", StringComparison.Ordinal)
                    ? line[(line.IndexOf(" :", StringComparison.Ordinal) + 2)..]
                    : string.Empty;
                var acknowledged = requested.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(item => AdvertisedCapabilities().Any(candidate =>
                        candidate.Split('=', 2)[0].Equals(item.TrimStart('-'), StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                _nativeClientEnabled = acknowledged.Any(item => item.Equals(NexIrcResumeProtocol.CapabilityName, StringComparison.OrdinalIgnoreCase));
                if (acknowledged.Length > 0)
                {
                    Transport.EnqueueInboundLine($":{_options.ServerName} CAP * ACK :{string.Join(' ', acknowledged)}");
                }
                else
                {
                    Transport.EnqueueInboundLine($":{_options.ServerName} CAP * NAK :{requested}");
                }

                break;
            case "CAP" when parts.Length > 1 && parts[1].Equals("END", StringComparison.OrdinalIgnoreCase):
                SendWelcome();
                break;
            case "NICK":
            case "USER":
                SendWelcome();
                break;
            case "CHATHISTORY":
                HandleHistoryRequest(line, parts);
                break;
            case "NEXIRC":
                HandleNativeResumeRequest(line, parts);
                break;
        }
    }

    private string[] AdvertisedCapabilities()
    {
        var capabilities = new HashSet<string>(_options.AdditionalCapabilities, StringComparer.OrdinalIgnoreCase)
        {
            "message-tags",
            "echo-message",
            "server-time",
            "account-tag",
            "batch",
            "labeled-response"
        };
        if (_options.Profile != DeterministicReplayProfile.NoHistory)
        {
            capabilities.Add("draft/chathistory");
        }

        if (IsNativeResumeProfile())
        {
            capabilities.Add($"{NexIrcResumeProtocol.CapabilityName}={NexIrcResumeProtocol.CapabilityVersion}");
        }

        return capabilities.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private void SendWelcome()
    {
        if (_welcomeSent)
        {
            return;
        }

        _welcomeSent = true;
        var chathistory = _options.Profile == DeterministicReplayProfile.NoHistory
            ? string.Empty
            : " CHATHISTORY=50 MSGREFTYPES=msgid,timestamp";
        Transport.EnqueueInboundLine($":{_options.ServerName} 005 {_options.Nickname}{chathistory} CHANTYPES=# CASEMAPPING=rfc1459 :supported");
        Transport.EnqueueInboundLine($":{_options.ServerName} 001 {_options.Nickname} :Welcome");
        if (_nativeClientEnabled && _resumeStateAvailable)
        {
            Transport.EnqueueInboundLine($":{_options.ServerName} NEXIRC SESSION {_options.NativeResumeToken} {CurrentResumeBoundary}");
        }
    }

    private void HandleHistoryRequest(string line, string[] parts)
    {
        _historyRequests.Add(line);
        if (_options.Profile == DeterministicReplayProfile.AdvertisedHistoryFails
            || _options.ResponseMode == DeterministicHistoryResponseMode.Fail)
        {
            Transport.EnqueueInboundLine($":{_options.ServerName} FAIL CHATHISTORY * :deterministic history failure");
            return;
        }

        if (_options.ResponseMode == DeterministicHistoryResponseMode.DisconnectDuringResponse)
        {
            Transport.EnqueueRemoteDisconnect();
            return;
        }

        if (_options.ResponseMode == DeterministicHistoryResponseMode.Malformed)
        {
            Transport.EnqueueInboundLine($":{_options.ServerName} BATCH +broken unexpected #room");
            return;
        }

        if (parts.Length < 2)
        {
            return;
        }

        var operation = parts[1].ToUpperInvariant();
        if (operation == "TARGETS")
        {
            SendTargets();
            return;
        }

        var target = parts.Length > 2 ? parts[2] : string.Empty;
        var referenceParts = parts.Length <= 3
            ? Array.Empty<string>()
            : parts[3..Math.Min(parts.Length, 5)];
        var replay = SelectHistory(operation, target, referenceParts);
        if (_options.ResponseMode is DeterministicHistoryResponseMode.Empty
            || replay.Length == 0)
        {
            SendBatch(target, Array.Empty<DeterministicServerHistoryEvent>());
            return;
        }

        if (_options.Profile == DeterministicReplayProfile.BoundedPartialReplay
            || _options.ResponseMode == DeterministicHistoryResponseMode.BoundedPartial)
        {
            replay = replay[..Math.Max(1, (replay.Length + 1) / 2)];
        }

        SendBatch(target, replay);
    }

    private void HandleNativeResumeRequest(string line, string[] parts)
    {
        if (parts.Length < 4 || !parts[1].Equals(NexIrcResumeProtocol.ResumeSubcommand, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _nativeResumeRequests.Add(line);
        var token = parts[2];
        var boundary = parts[3];
        var rejection = _options.Profile switch
        {
            DeterministicReplayProfile.NativeResumeRejects => "UNKNOWN_TOKEN",
            DeterministicReplayProfile.NativeResumeExpired => "EXPIRED_TOKEN",
            DeterministicReplayProfile.NativeResumeAccountMismatch => "ACCOUNT_MISMATCH",
            DeterministicReplayProfile.NativeResumeBoundaryTooOld => "BOUNDARY_TOO_OLD",
            DeterministicReplayProfile.NativeResumeInvalidated => "SESSION_INVALIDATED",
            DeterministicReplayProfile.NativeResumeServerRestarted => "SERVER_RESTARTED",
            _ when !_resumeStateAvailable => "UNKNOWN_TOKEN",
            _ when !_nativeClientEnabled => "UNSUPPORTED",
            _ when !string.Equals(token, _options.NativeResumeToken, StringComparison.Ordinal) => "UNKNOWN_TOKEN",
            _ when !IsKnownResumeBoundary(boundary) => "BOUNDARY_TOO_OLD",
            _ when _resumeOwnerActive && _options.Profile == DeterministicReplayProfile.NativeResumeConcurrentReject => "SESSION_INVALIDATED",
            _ => null
        };
        if (rejection is not null)
        {
            Transport.EnqueueInboundLine($":{_options.ServerName} NEXIRC RESUME REJECT {rejection}");
            return;
        }

        _resumeOwnerActive = true;
        _nativeReplayInProgress = true;
        var ordered = PrepareHistoryForResume();
        while (_scheduledLiveDuringReplay.TryDequeue(out var scheduledLive))
        {
            EnqueueLive(scheduledLive);
        }
        Transport.EnqueueInboundLine($":{_options.ServerName} NEXIRC RESUME ACCEPT {boundary}");
        SendNativeResumeReplay(boundary, ordered);
    }

    private void SendNativeResumeReplay(string boundary, IReadOnlyList<DeterministicServerHistoryEvent> ordered)
    {
        var orderedItems = ordered.ToArray();
        var boundaryIndex = Array.FindIndex(orderedItems, item => string.Equals(item.ResumeSequence, boundary, StringComparison.Ordinal));
        // Version 1 intentionally permits at-least-once delivery by replaying
        // the accepted boundary event itself when the fixture retained it.
        var replay = boundaryIndex < 0 ? orderedItems : orderedItems[boundaryIndex..];
        var replayBoundary = replay.Length == 0 ? boundary : replay[^1].ResumeSequence!;
        var batch = NextBatchId("resume");
        Transport.EnqueueInboundLine($":{_options.ServerName} BATCH +{batch} {NexIrcResumeProtocol.BatchType} {replay.FirstOrDefault()?.ResumeConversation ?? "Channel:#room"}");
        if (_options.Profile == DeterministicReplayProfile.NativeResumeDisconnectDuringReplay)
        {
            foreach (var item in replay.Take(Math.Max(1, replay.Length / 2)))
            {
                Transport.EnqueueInboundLine(RenderEvent(item, batchId: batch));
            }

            Transport.EnqueueRemoteDisconnect();
            _nativeReplayInProgress = false;
            return;
        }

        foreach (var item in replay)
        {
            var rendered = item;
            if (_options.Profile == DeterministicReplayProfile.NativeResumeOutOfOrder && replay.Length > 1 && item == replay[0])
            {
                rendered = replay[1];
            }

            Transport.EnqueueInboundLine(RenderEvent(rendered, batchId: batch));
        }

        Transport.EnqueueInboundLine($":{_options.ServerName} BATCH -{batch}");
        var completionBoundary = _options.Profile == DeterministicReplayProfile.NativeResumeMalformedCompletion
            ? "resume-invalid-completion"
            : replayBoundary;
        Transport.EnqueueInboundLine($":{_options.ServerName} NEXIRC RESUME COMPLETE {completionBoundary}");
        _nativeReplayInProgress = false;
        while (_heldLiveEvents.TryDequeue(out var live))
        {
            Transport.EnqueueInboundLine(RenderEvent(live, batchId: null));
        }
    }

    private bool IsNativeResumeProfile() => _options.Profile is
        DeterministicReplayProfile.NativeResume
        or DeterministicReplayProfile.NativeResumeRejects
        or DeterministicReplayProfile.NativeResumeExpired
        or DeterministicReplayProfile.NativeResumeAccountMismatch
        or DeterministicReplayProfile.NativeResumeBoundaryTooOld
        or DeterministicReplayProfile.NativeResumeInvalidated
        or DeterministicReplayProfile.NativeResumeServerRestarted
        or DeterministicReplayProfile.NativeResumeOutOfOrder
        or DeterministicReplayProfile.NativeResumeMalformedCompletion
        or DeterministicReplayProfile.NativeResumeDisconnectDuringReplay
        or DeterministicReplayProfile.NativeResumeConcurrentReject;

    private bool IsKnownResumeBoundary(string boundary) =>
        string.Equals(boundary, _options.NativeResumeInitialBoundary, StringComparison.Ordinal)
        || _history.Any(item => string.Equals(item.ResumeSequence, boundary, StringComparison.Ordinal));

    private DeterministicServerHistoryEvent[] PrepareHistoryForResume()
    {
        var previous = _options.NativeResumeInitialBoundary;
        var prepared = new List<DeterministicServerHistoryEvent>(_history.Count);
        foreach (var item in _history)
        {
            var next = item.ResumeSequence ?? $"resume-{++_resumeSequence}";
            var withSequence = item with
            {
                ResumeSequence = next,
                ResumePreviousSequence = item.ResumePreviousSequence ?? previous,
                ResumeConversation = item.ResumeConversation ?? $"Channel:{item.ConversationTarget}"
            };
            previous = next;
            _serverBoundary = next;
            prepared.Add(withSequence);
        }

        _history.Clear();
        _history.AddRange(prepared);
        return prepared.ToArray();
    }

    private DeterministicServerHistoryEvent PrepareResumeEvent(DeterministicServerHistoryEvent item)
    {
        var previous = CurrentResumeBoundary;
        var next = item.ResumeSequence ?? $"resume-{++_resumeSequence}";
        var prepared = item with
        {
            ResumeSequence = next,
            ResumePreviousSequence = item.ResumePreviousSequence ?? previous,
            ResumeConversation = item.ResumeConversation ?? $"Channel:{item.ConversationTarget}"
        };
        _serverBoundary = next;
        return prepared;
    }

    private DeterministicServerHistoryEvent[] SelectHistory(
        string operation,
        string target,
        string[] references)
    {
        var events = _history
            .Where(item => string.Equals(item.ConversationTarget, target, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (_options.Profile == DeterministicReplayProfile.MissingParentUnrecoverable)
        {
            events = events.Where(item => !string.Equals(item.RelationshipMetadata, "parent", StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        if (operation == "LATEST" || operation == "AFTER")
        {
            var index = FindIndex(events, references.Length == 0 ? null : references[0]);
            return index < 0 ? events : events[(index + 1)..];
        }

        if (operation == "BEFORE")
        {
            var index = FindIndex(events, references.Length == 0 ? null : references[0]);
            return index <= 0 ? Array.Empty<DeterministicServerHistoryEvent>() : events[..index];
        }

        if (operation == "BETWEEN" && references.Length > 1)
        {
            var first = FindIndex(events, references[0]);
            var second = FindIndex(events, references[1]);
            if (first >= 0 && second >= 0)
            {
                var start = Math.Min(first, second) + 1;
                var count = Math.Abs(second - first) - 1;
                return count <= 0 ? Array.Empty<DeterministicServerHistoryEvent>() : events[start..Math.Min(events.Length, start + count)];
            }
        }

        if (operation == "AROUND")
        {
            var index = FindIndex(events, references.Length == 0 ? null : references[0]);
            var start = index < 0 ? 0 : Math.Max(0, index - 1);
            return events[start..Math.Min(events.Length, start + 3)];
        }

        return events;
    }

    private void SendTargets()
    {
        var batch = NextBatchId("targets");
        Transport.EnqueueInboundLine($":{_options.ServerName} BATCH +{batch} draft/chathistory-targets");
        foreach (var group in _history.GroupBy(item => item.ConversationTarget, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.Max(item => item.ServerTime).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            Transport.EnqueueInboundLine($"@batch={batch} :{_options.ServerName} CHATHISTORY TARGETS {group.Key} {latest}");
        }

        Transport.EnqueueInboundLine($":{_options.ServerName} BATCH -{batch}");
    }

    private void SendBatch(string target, IReadOnlyList<DeterministicServerHistoryEvent> events)
    {
        var batch = NextBatchId("history");
        Transport.EnqueueInboundLine($":{_options.ServerName} BATCH +{batch} chathistory {target}");
        foreach (var item in events)
        {
            Transport.EnqueueInboundLine(RenderEvent(item, batch));
        }

        Transport.EnqueueInboundLine($":{_options.ServerName} BATCH -{batch}");
    }

    private string NextBatchId(string prefix) => $"{prefix}{Interlocked.Increment(ref _batchSequence)}";

    private static int FindIndex(DeterministicServerHistoryEvent[] events, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return -1;
        }

        var value = reference.StartsWith("msgid=", StringComparison.OrdinalIgnoreCase)
            ? reference["msgid=".Length..]
            : reference.StartsWith("timestamp=", StringComparison.OrdinalIgnoreCase)
                ? reference["timestamp=".Length..]
                : reference;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp))
        {
            for (var index = 0; index < events.Length; index++)
            {
                if (events[index].ServerTime >= timestamp)
                {
                    return index;
                }
            }

            return -1;
        }

        for (var index = 0; index < events.Length; index++)
        {
            if (string.Equals(events[index].CanonicalMessageId, value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string RenderEvent(DeterministicServerHistoryEvent item, string? batchId)
    {
        var tags = new List<string>();
        if (batchId is not null)
        {
            tags.Add($"batch={batchId}");
        }

        tags.Add($"msgid={EscapeTag(item.CanonicalMessageId)}");
        tags.Add($"time={item.ServerTime.ToUniversalTime():O}");
        if (item.ResumeSequence is not null)
        {
            tags.Add($"{NexIrcResumeProtocol.ResumeSequenceTag}={EscapeTag(item.ResumeSequence)}");
            tags.Add($"{NexIrcResumeProtocol.ResumePreviousSequenceTag}={EscapeTag(item.ResumePreviousSequence ?? string.Empty)}");
        }
        if (item.Account is not null)
        {
            tags.Add($"account={EscapeTag(item.Account)}");
        }

        if (item.Tags is not null)
        {
            tags.AddRange(item.Tags.Select(pair => pair.Value is null
                ? pair.Key
                : $"{pair.Key}={EscapeTag(pair.Value)}"));
        }

        if (!string.IsNullOrWhiteSpace(item.RelationshipMetadata))
        {
            tags.Add($"relationship={EscapeTag(item.RelationshipMetadata)}");
        }

        var tagPrefix = tags.Count == 0 ? string.Empty : $"@{string.Join(';', tags)} ";
        var prefix = $":{item.Sender}!fixture@deterministic";
        var command = item.Command.ToUpperInvariant();
        return command is "TAGMSG" or "JOIN" or "PART" or "QUIT"
            ? $"{tagPrefix}{prefix} {command} {item.ConversationTarget}{(string.IsNullOrEmpty(item.EffectiveBody) ? string.Empty : $" :{item.EffectiveBody}")}"
            : $"{tagPrefix}{prefix} {command} {item.ConversationTarget} :{item.EffectiveBody}";
    }

    private static string EscapeTag(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace(";", "\\:", StringComparison.Ordinal)
        .Replace(" ", "\\s", StringComparison.Ordinal);
}
