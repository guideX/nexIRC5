using System.Threading.Channels;
using System.Runtime.CompilerServices;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.State;

namespace nexIRC.Core.Session;

/// <summary>
/// One completely isolated IRC server connection and its protocol/session state.
/// </summary>
public sealed class ServerSession : IAsyncDisposable
{
    private static int _liveInstanceCount;
    private static readonly HashSet<int> KnownNumerics =
    [
        1, 4, 5, 301, 307, 310, 311, 312, 313, 315, 317, 318, 319, 321, 322, 323, 324, 331, 332, 333,
        330, 335, 338, 341, 352, 353, 366, 367, 368, 369, 372, 375, 376, 378, 379, 401,
        403, 404, 405, 407, 411, 412, 421, 442, 443, 461, 471, 472, 473, 474, 475, 476,
        477, 481, 482, 485, 422,
        671, 900, 903, 904, 905, 906, 907, 908
    ];
    private static readonly HashSet<int> NicknameFailureNumerics = [433, 436, 437];
    private static readonly HashSet<string> KnownCommands =
    [
        "CAP", "PING", "PONG", "PASS", "NICK", "USER", "JOIN", "PART", "QUIT", "PRIVMSG", "NOTICE",
        "TOPIC", "ERROR", "MODE", "KICK", "INVITE", "AWAY", "ACCOUNT", "BATCH", "CHATHISTORY", "TAGMSG", "WALLOPS", "AUTHENTICATE", "FAIL", "WARN", "NOTE"
    ];

    private readonly ServerSessionOptions _options;
    private readonly IIrcTransportFactory _transportFactory;
    private readonly Channel<RawIrcLineEvent> _rawEvents = CreateEventChannel<RawIrcLineEvent>();
    private readonly Channel<ParsedIrcMessageEvent> _parsedEvents = CreateEventChannel<ParsedIrcMessageEvent>();
    private readonly Channel<IrcParseErrorEvent> _parseErrors = CreateEventChannel<IrcParseErrorEvent>();
    private readonly Channel<SessionSemanticEvent> _semanticEvents = CreateEventChannel<SessionSemanticEvent>();
    private readonly Channel<OutboundIrcCommandEvent> _outboundEvents = CreateEventChannel<OutboundIrcCommandEvent>();
    private readonly object _gate = new();
    private readonly string _resumeNetworkIdentity;
    private readonly IResumeStateStore? _resumeStateStore;
    private readonly IResumeSecretProtector? _resumeSecretProtector;
    private readonly SessionStateStore _stateStore;
    private readonly IrcCapabilityNegotiator _capabilities;
    private readonly ISupportState _isupport = new();
    private readonly ServerIdentityDetector _identityDetector = new();
    private readonly ConnectionContinuityStateMachine _continuity = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private ServerFeatureSet _features;
    private ServerSessionState _state = ServerSessionState.Disconnected;
    private RegistrationState _registration = RegistrationState.NotStarted;
    private ConnectionFailure? _lastFailure;
    private Task? _runTask;
    private CancellationTokenSource? _runCts;
    private Channel<IrcOutboundMessage>? _outbound;
    private bool _disconnectRequested;
    private readonly string[] _nicknameCandidates;
    private int _nicknameCandidateIndex;
    private int _connectionGeneration;
    private readonly HashSet<string> _resynchronizationRequested = new(StringComparer.Ordinal);
    private ConnectionEpoch? _activeEpoch;
    private bool _registrationCommandsQueued;
    private SaslAuthenticationState _authenticationState;
    private string? _authenticationMechanism;
    private string? _authenticationFailure;
    private string? _authenticatedAccountHint;
    private SaslCredential? _activeCredential;
    private ISaslMechanism? _activeSaslMechanism;
    private bool _saslResponseSent;
    private bool _disposed;
    private Task? _disposeTask;
    private int _quitSent;
    private readonly TaskCompletionSource _quitWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _historyRequestSequence;
    private PendingChathistoryRequest? _activeHistoryRequest;
    private readonly HashSet<string> _acceptedHistoryBatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChathistoryConversationState> _historyStates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acceptedNativeResumeBatches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _nativeLiveSequenceMessageIds = new(StringComparer.Ordinal);
    private NexIrcResumeSession? _nativeResumeSession;
    private NexIrcResumeSession? _freshSessionAfterRestoredResume;
    private ClientResumeStateRecord? _loadedResumeState;
    private string? _pairingRecoveryCredential;
    private string? _pairingRecoveryBoundary;
    private bool _pairingRecoveryCredentialsDurable;
    private DateTimeOffset? _resumeStateCreatedAt;
    private bool _restoredResumeActive;
    private bool _resumeStateLoadCompleted;
    private NativeResumeAttempt? _nativeResumeAttempt;
    private ReadStateSnapshotBuilder? _readStateSnapshotBuilder;
    private readonly Dictionary<string, NexIrcReadMarker> _pendingReadStateUpdates = new(StringComparer.Ordinal);
    private bool _readStateSnapshotSeen;
    private bool _readStateSnapshotRequested;
    private TaskCompletionSource<NexIrcPairingAuthorization>? _pairingAuthorizationAttempt;
    private TaskCompletionSource<bool>? _pairingRevocationAttempt;
    private string? _nativeLivePendingSequence;
    private string? _nativeLivePendingMessageId;
    private Func<SessionSemanticEvent, CancellationToken, ValueTask>? _nativeEventProjector;

    public ServerSession(ServerSessionOptions options, IIrcTransportFactory transportFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ValidateOptions(options);
        _options = options;
        _transportFactory = transportFactory;
        _resumeNetworkIdentity = string.IsNullOrWhiteSpace(options.ResumeNetworkIdentity)
            ? ResumeStateIdentity.For(options.Endpoint, options.NetworkId)
            : options.ResumeNetworkIdentity!;
        _resumeStateStore = options.ResumeStateStore;
        _resumeSecretProtector = options.ResumeSecretProtector;
        _stateStore = new SessionStateStore(options.Nickname, options.DesiredChannels);
        _nicknameCandidates = BuildNicknameCandidates(options);
        var requestedCapabilityList = options.RequestedCapabilities
            .Where(capability => options.SaslPolicy != SaslAuthenticationPolicy.Disabled || !string.Equals(capability, IrcCapabilityCatalog.Sasl, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.SaslPolicy != SaslAuthenticationPolicy.Disabled &&
            !requestedCapabilityList.Any(capability => string.Equals(capability, IrcCapabilityCatalog.Sasl, StringComparison.OrdinalIgnoreCase)))
        {
            requestedCapabilityList.Add(IrcCapabilityCatalog.Sasl);
        }

        if (requestedCapabilityList.Any(capability => string.Equals(capability, IrcCapabilityCatalog.Chathistory, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var requiredCapability in new[] { IrcCapabilityCatalog.Batch, IrcCapabilityCatalog.ServerTime, IrcCapabilityCatalog.MessageTags })
            {
                if (!requestedCapabilityList.Contains(requiredCapability, StringComparer.OrdinalIgnoreCase))
                {
                    requestedCapabilityList.Add(requiredCapability);
                }
            }
        }

        var requestedCapabilities = requestedCapabilityList.ToArray();
        _capabilities = new IrcCapabilityNegotiator(
            requestedCapabilities,
            options.MaximumOutboundLineBytes,
            options.SaslPolicy == SaslAuthenticationPolicy.Disabled ? Array.Empty<string>() : [IrcCapabilityCatalog.Sasl]);
        _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, options.Profiles, ServerIdentity.Unknown, options.MaximumChathistoryRequestSize);
        _authenticationState = options.SaslPolicy == SaslAuthenticationPolicy.Disabled
            ? SaslAuthenticationState.Disabled
            : SaslAuthenticationState.WaitingForCapability;
        Interlocked.Increment(ref _liveInstanceCount);
    }

    public event EventHandler<SessionStateChangedEvent>? StateChanged;

    public event EventHandler<ConnectionContinuityStateChangedEvent>? ContinuityStateChanged;

    public event EventHandler<SessionSemanticEvent>? SemanticEventReceived;

    /// <summary>
    /// Registers an optional Application projection barrier. Native live and
    /// replay cursors are committed only after this callback completes, so a
    /// durable Application projection cannot be left behind an advanced
    /// protected resume boundary.
    /// </summary>
    public void SetNativeEventProjector(Func<SessionSemanticEvent, CancellationToken, ValueTask> projector)
    {
        ArgumentNullException.ThrowIfNull(projector);
        lock (_gate)
        {
            if (_runTask is not null || _state != ServerSessionState.Disconnected)
            {
                throw new InvalidOperationException("The native event projector must be set before the session starts.");
            }

            _nativeEventProjector = projector;
        }
    }

    /// <summary>
    /// Developer diagnostics tap for the redacted raw receive path.
    /// Subscribers are observational only and never own the bounded event
    /// channel consumed by the application event drainer.
    /// </summary>
    public event EventHandler<RawIrcLineEvent>? RawLineReceived;

    /// <summary>Developer diagnostics tap for sanitized outbound commands.</summary>
    public event EventHandler<OutboundIrcCommandEvent>? OutboundCommandSent;

    public IrcEventDispatcher EventDispatcher { get; } = new();

    public ServerSessionSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return CreateSnapshot();
            }
        }
    }

    public Task Completion => _runTask ?? Task.CompletedTask;

    /// <summary>
    /// Completes when this session's writer has handed QUIT to the transport.
    /// It is useful to verify natural shutdown without competing with the
    /// application event-drain consumer.
    /// </summary>
    public Task QuitWritten => _quitWritten.Task;

    /// <summary>
    /// Product-level continuity state.  This is the authoritative distinction
    /// between a registered transport and a workspace that has finished
    /// reconnect reconciliation.
    /// </summary>
    public ConnectionContinuitySnapshot Continuity => _continuity.Snapshot;

    /// <summary>
    /// Bounded continuity diagnostics for reconstructing recovery episodes.
    /// Entries contain lifecycle metadata only; protocol secrets and raw
    /// authentication payloads are never recorded here.
    /// </summary>
    public ConnectionContinuityDiagnosticsSnapshot ContinuityDiagnostics => _continuity.Diagnostics;

    public void RecordContinuityDiagnostic(
        ContinuityDiagnosticKind kind,
        string? detail = null,
        int? connectionGeneration = null,
        int? relatedGeneration = null)
    {
        var generation = connectionGeneration ?? Continuity.ConnectionGeneration;
        _continuity.RecordDiagnostic(generation, kind, detail, relatedGeneration);
    }

    public void RecordContinuityStrategySelected(
        ConnectionRecoveryStrategyId strategy,
        string reason,
        string? boundary = null,
        int? connectionGeneration = null)
    {
        var generation = connectionGeneration ?? Continuity.ConnectionGeneration;
        _continuity.RecordRecoveryStrategySelected(generation, strategy, reason, boundary);
    }

    public void RecordHistoricalDuplicateSuppressed(int? connectionGeneration = null, string? detail = null)
    {
        var generation = connectionGeneration ?? Continuity.ConnectionGeneration;
        _continuity.RecordHistoricalDuplicateSuppressed(generation, detail);
    }

    /// <summary>
    /// Test and diagnostic visibility for session ownership. This is a count,
    /// not an object registry, so observability cannot retain a session.
    /// </summary>
    public static int LiveInstanceCount => Volatile.Read(ref _liveInstanceCount);

    public int MaximumOutboundLineBytes => _options.MaximumOutboundLineBytes;

    public int MaximumChathistoryRequestSize => Math.Clamp(_options.MaximumChathistoryRequestSize, 1, 10000);

    public ChathistorySupport ChathistorySupport => Snapshot.Features.Chathistory;

    public NexIrcResumeSupport NativeResumeSupport
    {
        get
        {
            lock (_gate)
            {
                var capabilities = _capabilities.Snapshot;
                var version = capabilities.Available.TryGetValue(NexIrcResumeProtocol.CapabilityName, out var capability)
                    ? capability.Value
                    : null;
                return new(
                    capabilities.IsEnabled(NexIrcResumeProtocol.CapabilityName),
                    _nativeResumeSession is not null,
                    version);
            }
        }
    }

    public bool SynchronizedReadStateAvailable
    {
        get { lock (_gate) return NexIrcReadStateProtocol.IsSupported(_capabilities.Snapshot); }
    }

    public async ValueTask<bool> TryRequestSynchronizedReadStateAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!NexIrcReadStateProtocol.IsSupported(_capabilities.Snapshot)
                || !_options.Endpoint.UseTls
                || _authenticationState != SaslAuthenticationState.Succeeded
                || _registration != RegistrationState.Registered
                || _readStateSnapshotRequested)
                return false;
            _readStateSnapshotRequested = true;
        }

        try
        {
            await SendCommandAsync("NEXIRC", ["STATE", "GET"], cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException or IOException)
        {
            lock (_gate) _readStateSnapshotRequested = false;
            return false;
        }
    }

    public async ValueTask<bool> TryAdvanceSynchronizedReadStateAsync(
        string conversationKey,
        long sequence,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationKey) || conversationKey.Length > NexIrcReadStateProtocol.MaximumConversationKeyLength
            || sequence < 1 || string.IsNullOrWhiteSpace(messageId) || messageId.Length > NexIrcReadStateProtocol.MaximumMessageIdLength)
            return false;

        lock (_gate)
        {
            if (!NexIrcReadStateProtocol.IsSupported(_capabilities.Snapshot)
                || !_options.Endpoint.UseTls
                || _authenticationState != SaslAuthenticationState.Succeeded
                || _registration != RegistrationState.Registered)
                return false;
        }

        try
        {
            await SendCommandAsync("NEXIRC", ["STATE", "SET", "READ",
                NexIrcReadStateProtocol.EncodeField(conversationKey), $"s{sequence}",
                NexIrcReadStateProtocol.EncodeField(messageId), "1"], cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException) { return false; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Session-memory identity for the logical resumable session. The token is
    /// opaque and is never included in continuity diagnostics; callers should
    /// use <see cref="NexIrcResumeSession.TokenFingerprint"/> for display.
    /// </summary>
    public NexIrcResumeSession? NativeResumeSession
    {
        get
        {
            lock (_gate)
            {
                return _nativeResumeSession;
            }
        }
    }

    public bool HasPendingNativePairingRecovery
    {
        get
        {
            lock (_gate)
            {
                return _pairingRecoveryCredential is not null && _pairingRecoveryBoundary is not null;
            }
        }
    }

    public bool NativePairingCredentialsAreDurable
    {
        get
        {
            lock (_gate)
            {
                return _pairingRecoveryCredentialsDurable;
            }
        }
    }

    /// <summary>
    /// Starts the bounded native resume handshake for one registered
    /// generation. Protocol parsing completes the task; this method never
    /// transitions continuity lifecycle state.
    /// </summary>
    public async ValueTask<NexIrcResumeExecutionResult> RequestNativeResumeAsync(
        int connectionGeneration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionEpoch epoch;
        NativeResumeAttempt attempt;
        NexIrcResumeSession sessionIdentity;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered
                || _activeEpoch is not { IsActive: true } currentEpoch
                || currentEpoch.Generation != connectionGeneration)
            {
                throw new InvalidOperationException("The native resume request belongs to a non-current IRC generation.");
            }

            if (!NexIrcResumeProtocol.IsSupported(_capabilities.Snapshot)
                || _nativeResumeSession is null)
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "The current generation did not negotiate nexIRC native resume or has no retained logical session.");
            }

            if (_restoredResumeActive && !TryActivateRestoredResumeStateUnsafe())
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "The protected resume record does not match the authenticated account context; the stored bearer credential was not offered.");
            }

            if (_options.SaslPolicy != SaslAuthenticationPolicy.Disabled
                && _authenticationState != SaslAuthenticationState.Succeeded)
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "The authenticated account context was not established; the stored bearer credential was not offered.");
            }

            if (_nativeResumeAttempt is not null)
            {
                throw new InvalidOperationException("A native resume request is already active for this connection generation.");
            }

            epoch = currentEpoch;
            sessionIdentity = _nativeResumeSession;
            attempt = new NativeResumeAttempt(connectionGeneration, sessionIdentity.AuthoritativeBoundary);
            _nativeResumeAttempt = attempt;
        }

        _continuity.RecordDiagnostic(
            connectionGeneration,
            ContinuityDiagnosticKind.RecoveryRequestStarted,
            $"NexIrcResume requested boundary={sessionIdentity.AuthoritativeBoundary}; session={sessionIdentity.TokenFingerprint}");

        try
        {
            var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.ResumeSubcommand, sessionIdentity.Token, sessionIdentity.AuthoritativeBoundary]);
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
            attempt.RequestSent = true;
            var result = await attempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _continuity.RecordDiagnostic(
                connectionGeneration,
                result.Outcome == NexIrcResumeOutcome.Completed
                    ? ContinuityDiagnosticKind.RecoveryRequestCompleted
                    : ContinuityDiagnosticKind.RecoveryRequestFailed,
                $"NexIrcResume {result.Outcome}; requested={result.RequestedBoundary}; final={result.FinalBoundary}; events={result.ReplayedEventCount}; duplicates={result.DuplicateEventsSuppressed}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelNativeResumeAttempt(connectionGeneration, "Native resume was cancelled by the owning generation.");
            _continuity.RecordDiagnostic(
                connectionGeneration,
                ContinuityDiagnosticKind.RecoveryRequestCancelled,
                "NexIrcResume cancellation was fenced to the current generation.");
            throw;
        }
        catch
        {
            FailNativeResumeAttempt(
                connectionGeneration,
                NexIrcResumeOutcome.Failed,
                "The native resume request could not be written.",
                fallbackSafe: false);
            throw;
        }
    }

    /// <summary>
    /// Creates a new physical attachment to the retained logical session.
    /// This is an explicit new-device operation and uses the session-scoped
    /// grant; ordinary reconnects continue to use the attachment token.
    /// </summary>
    public async ValueTask<NexIrcResumeExecutionResult> RequestNativeAttachmentAsync(
        int connectionGeneration,
        CancellationToken cancellationToken = default)
    {
        string? sessionCredential;
        string? boundary;
        lock (_gate)
        {
            sessionCredential = _nativeResumeSession?.SessionCredential;
            boundary = _nativeResumeSession?.AuthoritativeBoundary;
        }

        if (sessionCredential is null || boundary is null)
        {
            return NexIrcResumeExecutionResult.Unsupported(
                "The current generation has no retained session grant for a new attachment.");
        }

        return await RequestNativeAttachmentAsync(
            connectionGeneration,
            sessionCredential,
            boundary,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports a separately transferred attachment grant for this connection.
    /// The grant is sent only over the already-authenticated TLS generation;
    /// only the new attachment credentials returned by the server are saved.
    /// </summary>
    public async ValueTask<NexIrcResumeExecutionResult> RequestNativeAttachmentAsync(
        int connectionGeneration,
        string sessionCredential,
        string authoritativeBoundary,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionCredential);
        ArgumentException.ThrowIfNullOrWhiteSpace(authoritativeBoundary);
        ConnectionEpoch epoch;
        NativeResumeAttempt attempt;
        NexIrcResumeSession sessionIdentity;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered
                || _activeEpoch is not { IsActive: true } currentEpoch
                || currentEpoch.Generation != connectionGeneration)
            {
                throw new InvalidOperationException("The native attachment request belongs to a non-current IRC generation.");
            }

            if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
                || _nativeResumeSession is null
                || !NexIrcResumeProtocol.IsSafeOpaqueValue(sessionCredential)
                || !NexIrcResumeProtocol.IsSafeOpaqueValue(authoritativeBoundary))
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "The current generation did not negotiate native attachments or the imported grant was malformed.");
            }

            if (_restoredResumeActive && !TryActivateRestoredResumeStateUnsafe())
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "The protected attachment record does not match the authenticated account context; the session grant was not offered.");
            }

            if (_options.SaslPolicy != SaslAuthenticationPolicy.Disabled
                && _authenticationState != SaslAuthenticationState.Succeeded)
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "The authenticated account context was not established; the session grant was not offered.");
            }

            if (!_options.Endpoint.UseTls)
            {
                return NexIrcResumeExecutionResult.Unsupported(
                    "Native attachment credentials are only sent over TLS.");
            }

            if (_nativeResumeAttempt is not null)
            {
                throw new InvalidOperationException("A native resume or attachment request is already active for this connection generation.");
            }

            epoch = currentEpoch;
            sessionIdentity = _nativeResumeSession;
            attempt = new NativeResumeAttempt(connectionGeneration, authoritativeBoundary)
            {
                IsAttachmentRequest = true
            };
            _nativeResumeAttempt = attempt;
        }

        _continuity.RecordDiagnostic(
            connectionGeneration,
            ContinuityDiagnosticKind.RecoveryRequestStarted,
            $"NexIrcResume attachment requested boundary={authoritativeBoundary}; session={sessionIdentity.TokenFingerprint}");

        try
        {
            var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.AttachSubcommand, sessionCredential, authoritativeBoundary]);
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
            attempt.RequestSent = true;
            var result = await attempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _continuity.RecordDiagnostic(
                connectionGeneration,
                result.Outcome == NexIrcResumeOutcome.Completed
                    ? ContinuityDiagnosticKind.RecoveryRequestCompleted
                    : ContinuityDiagnosticKind.RecoveryRequestFailed,
                $"NexIrcResume attachment {result.Outcome}; requested={result.RequestedBoundary}; final={result.FinalBoundary}; events={result.ReplayedEventCount}; duplicates={result.DuplicateEventsSuppressed}");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelNativeResumeAttempt(connectionGeneration, "Native attachment was cancelled by the owning generation.");
            throw;
        }
        catch
        {
            FailNativeResumeAttempt(
                connectionGeneration,
                NexIrcResumeOutcome.Failed,
                "The native attachment request could not be written.",
                fallbackSafe: false);
            throw;
        }
    }

    /// <summary>Creates a short-lived pairing authorization on this live attachment.</summary>
    public async ValueTask<NexIrcPairingAuthorization> RequestNativePairingAuthorizationAsync(
        int connectionGeneration,
        CancellationToken cancellationToken = default)
    {
        ConnectionEpoch epoch;
        TaskCompletionSource<NexIrcPairingAuthorization> completion;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered
                || _activeEpoch is not { IsActive: true } currentEpoch
                || currentEpoch.Generation != connectionGeneration)
            {
                throw new InvalidOperationException("The pairing request belongs to a non-current IRC generation.");
            }

            if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
                || _nativeResumeSession?.AttachmentId is null
                || !_options.Endpoint.UseTls
                || _authenticationState != SaslAuthenticationState.Succeeded)
            {
                throw new InvalidOperationException("Pairing requires negotiated attachment support, TLS, an established attachment, and successful SASL authentication.");
            }

            if (_pairingAuthorizationAttempt is not null || _pairingRevocationAttempt is not null || _nativeResumeAttempt is not null)
            {
                throw new InvalidOperationException("A native resume, attachment, or pairing request is already active for this connection generation.");
            }

            epoch = currentEpoch;
            completion = new TaskCompletionSource<NexIrcPairingAuthorization>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pairingAuthorizationAttempt = completion;
        }

        try
        {
            var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.PairSubcommand, NexIrcResumeProtocol.PairCreateSubcommand]);
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pairingAuthorizationAttempt, completion))
                {
                    _pairingAuthorizationAttempt = null;
                }
            }
        }
    }

    /// <summary>
    /// Imports a user supplied pairing value on an authenticated TLS
    /// connection. A protected recovery receipt is persisted before submission;
    /// it is retired only after the resulting attachment credentials are
    /// durably stored and the server confirms custody.
    /// </summary>
    public async ValueTask<NexIrcResumeExecutionResult> RequestNativeAttachmentWithPairingAsync(
        int connectionGeneration,
        string pairingMaterial,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingMaterial);
        var networkIdentity = Snapshot.Features.NetworkName;
        if (!NexIrcResumeProtocol.TryParsePairingMaterial(pairingMaterial, networkIdentity, out var code, out var boundary))
        {
            return NexIrcResumeExecutionResult.Unsupported("The pairing value is malformed or belongs to another network.");
        }

        if (_resumeStateStore is null || _resumeSecretProtector is null)
        {
            return NexIrcResumeExecutionResult.Unsupported("Pairing requires an available protected resume-state store so handoff recovery can survive process death.");
        }

        ConnectionEpoch epoch;
        NativeResumeAttempt attempt;
        NexIrcResumeSession prePairSession;
        string recoveryCredential;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered
                || _activeEpoch is not { IsActive: true } currentEpoch
                || currentEpoch.Generation != connectionGeneration)
            {
                throw new InvalidOperationException("The pairing import belongs to a non-current IRC generation.");
            }

            if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
                || _nativeResumeSession is null
                || !_options.Endpoint.UseTls
                || _authenticationState != SaslAuthenticationState.Succeeded)
            {
                return NexIrcResumeExecutionResult.Unsupported("Pairing requires negotiated attachment support, TLS, and successful SASL authentication.");
            }

            if (_nativeResumeAttempt is not null || _pairingAuthorizationAttempt is not null || _pairingRevocationAttempt is not null)
            {
                throw new InvalidOperationException("A native resume, attachment, or pairing request is already active for this connection generation.");
            }

            if (_nativeResumeSession is not { } currentSession)
            {
                return NexIrcResumeExecutionResult.Unsupported("Pairing requires a current protected attachment state.");
            }

            if (_pairingRecoveryCredential is not null
                && !string.Equals(_pairingRecoveryBoundary, boundary, StringComparison.Ordinal))
            {
                return NexIrcResumeExecutionResult.Unsupported("An earlier pairing handoff is still pending recovery or custody confirmation.");
            }

            recoveryCredential = _pairingRecoveryCredential ?? NexIrcResumeProtocol.CreatePairingRecoveryCredential();
            _pairingRecoveryCredential = recoveryCredential;
            _pairingRecoveryBoundary = boundary;
            _pairingRecoveryCredentialsDurable = false;
            prePairSession = currentSession;
        }

        ConnectionEpoch? submissionEpoch = null;
        NativeResumeAttempt? submissionAttempt = null;
        var sessionToPersist = prePairSession;
        for (var persistenceAttempt = 0; persistenceAttempt < 4; persistenceAttempt++)
        {
            if (!await PersistNativeResumeStateAsync(sessionToPersist).ConfigureAwait(false))
            {
                return NexIrcResumeExecutionResult.Failed("The protected pairing handoff receipt could not be durably stored before submission.");
            }

            lock (_gate)
            {
                if (_registration != RegistrationState.Registered
                    || _activeEpoch is not { IsActive: true } currentEpoch
                    || currentEpoch.Generation != connectionGeneration
                    || _nativeResumeSession is not { } latestSession)
                {
                    return NexIrcResumeExecutionResult.Failed("The pairing import became stale before submission.");
                }

                if (ReferenceEquals(latestSession, sessionToPersist))
                {
                    submissionEpoch = currentEpoch;
                    submissionAttempt = new NativeResumeAttempt(connectionGeneration, boundary)
                    {
                        IsAttachmentRequest = true,
                        IsPairingRequest = true
                    };
                    _nativeResumeAttempt = submissionAttempt;
                    break;
                }

                sessionToPersist = latestSession;
            }
        }

        if (submissionEpoch is null || submissionAttempt is null)
        {
            return NexIrcResumeExecutionResult.Failed("The pairing import changed too quickly to persist a stable recovery point.");
        }

        epoch = submissionEpoch;
        attempt = submissionAttempt;

        try
        {
            var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.PairSubcommand, NexIrcResumeProtocol.PairUseSubcommand, code, boundary, recoveryCredential]);
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
            attempt.RequestSent = true;
            return await attempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelNativeResumeAttempt(connectionGeneration, "Native pairing import was cancelled by the owning generation.");
            throw;
        }
        catch
        {
            FailNativeResumeAttempt(connectionGeneration, NexIrcResumeOutcome.Failed, "The pairing request could not be written.", fallbackSafe: false);
            throw;
        }
    }

    public async ValueTask<NexIrcResumeExecutionResult> RequestNativePairingRecoveryAsync(
        int connectionGeneration,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ConnectionEpoch epoch;
        NativeResumeAttempt attempt;
        string recoveryCredential;
        string boundary;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered
                || _activeEpoch is not { IsActive: true } currentEpoch
                || currentEpoch.Generation != connectionGeneration)
            {
                throw new InvalidOperationException("The pairing recovery belongs to a non-current IRC generation.");
            }

            if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
                || !_options.Endpoint.UseTls
                || _authenticationState != SaslAuthenticationState.Succeeded
                || _nativeResumeSession is null
                || _pairingRecoveryCredential is not { } receipt
                || _pairingRecoveryBoundary is not { } savedBoundary)
            {
                return NexIrcResumeExecutionResult.Unsupported("No authenticated, protected pairing handoff is available to recover.");
            }

            if (_nativeResumeAttempt is not null || _pairingAuthorizationAttempt is not null || _pairingRevocationAttempt is not null)
            {
                throw new InvalidOperationException("Another native recovery or pairing request is active for this connection generation.");
            }

            epoch = currentEpoch;
            recoveryCredential = receipt;
            boundary = savedBoundary;
            attempt = new NativeResumeAttempt(connectionGeneration, boundary)
            {
                IsAttachmentRequest = true,
                IsPairingRequest = true,
                IsPairingRecoveryRequest = true
            };
            _nativeResumeAttempt = attempt;
        }

        try
        {
            var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.PairSubcommand, NexIrcResumeProtocol.PairRecoverSubcommand, recoveryCredential]);
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
            attempt.RequestSent = true;
            return await attempt.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelNativeResumeAttempt(connectionGeneration, "Pairing handoff recovery was cancelled by the owning generation.");
            throw;
        }
        catch
        {
            FailNativeResumeAttempt(connectionGeneration, NexIrcResumeOutcome.Failed, "The pairing handoff recovery request could not be written.", fallbackSafe: false);
            throw;
        }
    }

    /// <summary>Revokes an unused pairing authorization issued by this live attachment.</summary>
    public async ValueTask<bool> RevokeNativePairingAuthorizationAsync(
        int connectionGeneration,
        string pairingMaterial,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairingMaterial);
        if (!NexIrcResumeProtocol.TryParsePairingMaterial(pairingMaterial, Snapshot.Features.NetworkName, out var code, out _))
        {
            return false;
        }

        ConnectionEpoch epoch;
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered
                || _activeEpoch is not { IsActive: true } currentEpoch
                || currentEpoch.Generation != connectionGeneration)
            {
                throw new InvalidOperationException("The pairing revocation belongs to a non-current IRC generation.");
            }

            if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
                || _nativeResumeSession?.AttachmentId is null
                || !_options.Endpoint.UseTls
                || _authenticationState != SaslAuthenticationState.Succeeded)
            {
                return false;
            }

            if (_nativeResumeAttempt is not null || _pairingAuthorizationAttempt is not null || _pairingRevocationAttempt is not null)
            {
                throw new InvalidOperationException("Another native recovery or pairing request is already active for this connection generation.");
            }

            epoch = currentEpoch;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pairingRevocationAttempt = completion;
        }

        try
        {
            var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.PairSubcommand, NexIrcResumeProtocol.PairRevokeSubcommand, code]);
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pairingRevocationAttempt, completion))
                {
                    _pairingRevocationAttempt = null;
                }
            }
        }
    }

    public ChathistoryConversationState GetChathistoryState(string conversation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversation);
        lock (_gate)
        {
            var key = NormalizeHistoryConversation(conversation);
            var result = _historyStates.TryGetValue(key, out var state)
                ? state
                : ChathistoryConversationState.Empty(conversation, _connectionGeneration);
            return result;
        }
    }

    public bool CanLoadOlderHistory(string conversation)
    {
        var state = GetChathistoryState(conversation);
        return ChathistorySupport.IsUsable
            && !state.RequestActive
            && !state.BeginningReached
            && state.ConnectionGeneration == Snapshot.ConnectionGeneration;
    }

    public bool CanLoadNewerHistory(string conversation)
    {
        var state = GetChathistoryState(conversation);
        return ChathistorySupport.IsUsable
            && !state.RequestActive
            && state.ConnectionGeneration == Snapshot.ConnectionGeneration;
    }

    public bool CancelHistoryRequest(string conversation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversation);
        lock (_gate)
        {
            if (_activeHistoryRequest is null
                || _activeHistoryRequest.Request.Conversation is not { Length: > 0 } activeConversation
                || !string.Equals(
                    NormalizeHistoryConversation(activeConversation),
                    NormalizeHistoryConversation(conversation),
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        CompleteHistoryRequest(ChathistoryRequestCompletion.Cancelled, "The history conversation was closed.");
        return true;
    }

    /// <summary>
    /// Sends one bounded, correlated server-history request. A single active
    /// request is allowed per session; application code supplies the logical
    /// conversation identity so playback can never be routed by raw protocol
    /// state alone.
    /// </summary>
    public async ValueTask<ChathistoryResult> RequestHistoryAsync(
        ChathistoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PendingChathistoryRequest pending;
        ConnectionEpoch epoch;
        ChathistoryRequestBuildResult built;
        lock (_gate)
        {
            if (_activeHistoryRequest is not null)
            {
                throw new InvalidOperationException("A CHATHISTORY request is already active for this session.");
            }

            if (_registration != RegistrationState.Registered || _activeEpoch is not { IsActive: true } currentEpoch)
            {
                throw new InvalidOperationException("The IRC session is not registered.");
            }

            if (request.ConnectionGeneration != currentEpoch.Generation)
            {
                throw new InvalidOperationException("The CHATHISTORY request belongs to a stale connection generation.");
            }

            if (_options.NetworkId is Guid networkId && request.NetworkId != networkId)
            {
                throw new InvalidOperationException("The CHATHISTORY request belongs to another network session.");
            }

            built = ChathistoryCommandBuilder.BuildValidated(
                request,
                _features.Chathistory,
                _options.MaximumOutboundLineBytes);
            epoch = currentEpoch;
            pending = new PendingChathistoryRequest(
                Interlocked.Increment(ref _historyRequestSequence),
                built.Request,
                currentEpoch.Generation);
            _activeHistoryRequest = pending;
            _continuity.RecordDiagnostic(
                currentEpoch.Generation,
                ContinuityDiagnosticKind.RecoveryRequestStarted,
                $"{pending.Request.Purpose}:{pending.Request.Operation}");
            if (pending.Request.Conversation is { Length: > 0 } conversation)
            {
                SetHistoryStateUnsafe(conversation, requestActive: true, beginningReached: false, failed: false, failure: null);
            }
        }

        try
        {
            await QueueOutboundAsync(built.Command, cancellationToken, epoch).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or OperationCanceledException)
        {
            CompleteHistoryRequest(ChathistoryRequestCompletion.Cancelled, exception.Message);
        }

        using var timeout = new CancellationTokenSource(_options.ChathistoryRequestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token, _disposeCts.Token);
        try
        {
            return await pending.Completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteHistoryRequest(
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? ChathistoryRequestCompletion.TimedOut
                    : ChathistoryRequestCompletion.Cancelled,
                timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? "The CHATHISTORY request timed out."
                    : "The CHATHISTORY request was cancelled.");
            return await pending.Completion.Task.ConfigureAwait(false);
        }
    }

    public IAsyncEnumerable<RawIrcLineEvent> ReadRawEventsAsync(CancellationToken cancellationToken = default) => _rawEvents.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<ParsedIrcMessageEvent> ReadParsedEventsAsync(CancellationToken cancellationToken = default) => _parsedEvents.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<IrcParseErrorEvent> ReadParseErrorsAsync(CancellationToken cancellationToken = default) => _parseErrors.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<SessionSemanticEvent> ReadSemanticEventsAsync(CancellationToken cancellationToken = default) => _semanticEvents.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<OutboundIrcCommandEvent> ReadOutboundEventsAsync(CancellationToken cancellationToken = default) => _outboundEvents.Reader.ReadAllAsync(cancellationToken);

    public void SetDesiredChannels(IEnumerable<string> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        var channelList = channels.ToArray();
        foreach (var channel in channelList)
        {
            ValidateChannelName(channel);
        }

        lock (_gate)
        {
            _stateStore.SetDesiredChannels(channelList);
        }
    }

    public async ValueTask JoinChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ValidateChannelName(channel);
        var shouldSend = false;
        var isRegistered = false;
        lock (_gate)
        {
            _stateStore.AddDesiredChannel(channel);
            isRegistered = _registration == RegistrationState.Registered;
            shouldSend = isRegistered && _stateStore.ShouldRequestJoin(channel);
            if (shouldSend)
            {
                _stateStore.MarkChannelJoining(channel);
            }
        }

        if (shouldSend)
        {
            await SendCommandAsync("JOIN", [channel], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask RejoinChannelAsync(string channel, CancellationToken cancellationToken = default)
    {
        ValidateChannelName(channel);
        var shouldSend = false;
        lock (_gate)
        {
            _stateStore.AddDesiredChannel(channel);
            shouldSend = _registration == RegistrationState.Registered;
            if (shouldSend)
            {
                _stateStore.MarkChannelJoining(channel);
            }
        }

        if (shouldSend)
        {
            await SendCommandAsync("JOIN", [channel], cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask PartChannelAsync(string channel, string? reason = null, CancellationToken cancellationToken = default)
    {
        ValidateChannelName(channel);
        lock (_gate)
        {
            _stateStore.RemoveDesiredChannel(channel);
        }

        if (Snapshot.Registration == RegistrationState.Registered)
        {
            await SendCommandAsync("PART", [channel], reason, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask RequestNamesAsync(string? channel = null, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(channel))
        {
            ValidateChannelName(channel);
            lock (_gate)
            {
                _stateStore.BeginNamesRequest(channel);
            }
        }

        await SendCommandAsync("NAMES", string.IsNullOrWhiteSpace(channel) ? null : [channel], cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _runTask ??= StartRun(cancellationToken);
        }
    }

    public async ValueTask SendCommandAsync(string command, IReadOnlyList<string>? middleParameters = null, string? trailingParameter = null, CancellationToken cancellationToken = default)
    {
        var message = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(command, middleParameters, trailingParameter);
        await QueueOutboundAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the protected native-resume record for this network. Normal
    /// disconnects intentionally do not call this; profile removal, logout,
    /// or credential replacement may call it explicitly.
    /// </summary>
    public ValueTask ClearPersistedNativeResumeStateAsync(CancellationToken cancellationToken = default) =>
        ClearProtectedResumeStateAsync(cancellationToken: cancellationToken);

    public async ValueTask SendCommandAsync(IrcOutboundMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.FramedBytes.Length > _options.MaximumOutboundLineBytes)
        {
            throw new InvalidOperationException($"The IRC command exceeds the configured maximum of {_options.MaximumOutboundLineBytes} bytes.");
        }

        await QueueOutboundAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendRawCommandAsync(string rawLine, CancellationToken cancellationToken = default)
    {
        var message = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).BuildRaw(rawLine);
        await QueueOutboundAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SendTaggedCommandAsync(
        IReadOnlyDictionary<string, string?> tags,
        string command,
        IReadOnlyList<string>? middleParameters = null,
        string? trailingParameter = null,
        CancellationToken cancellationToken = default)
    {
        var message = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).BuildWithTags(tags, command, middleParameters, trailingParameter);
        await QueueOutboundAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a PRIVMSG carrying the IRCv3 +reply relationship. The capability
    /// and connection-generation checks happen at the same boundary as the
    /// outbound writer selection, so a reconnect cannot leak a stale reply.
    /// </summary>
    public async ValueTask SendReplyAsync(
        string target,
        string text,
        string parentMessageId,
        int? expectedConnectionGeneration = null,
        CancellationToken cancellationToken = default)
    {
        IrcOutboundMessage message;
        ConnectionEpoch epoch;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered || _activeEpoch is not { IsActive: true } currentEpoch)
            {
                throw new InvalidOperationException("The IRC session is not registered.");
            }

            if (expectedConnectionGeneration is int expected && expected != currentEpoch.Generation)
            {
                throw new InvalidOperationException("The reply belongs to a stale connection generation.");
            }

            if (!_capabilities.Snapshot.IsEnabled(IrcCapabilityCatalog.MessageTags))
            {
                throw new InvalidOperationException("The server did not negotiate message-tags; replies are unavailable.");
            }

            if (_features.RuntimeISupport.IsClientTagDenied("+reply")
                || _features.RuntimeISupport.IsClientTagDenied("reply"))
            {
                throw new InvalidOperationException("The server's CLIENTTAGDENY policy disallows replies.");
            }

            message = IrcReplyCommandBuilder.Build(
                new IrcCommandBuilder(_options.MaximumOutboundLineBytes),
                target,
                text,
                parentMessageId);
            epoch = currentEpoch;
        }

        await QueueOutboundAsync(message, cancellationToken, epoch).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one application-owned IRCv3 draft reaction TAGMSG. All capability,
    /// target, identity-generation, and CLIENTTAGDENY checks occur before the
    /// command enters the transport queue.
    /// </summary>
    public ValueTask SendReactionAsync(
        string target,
        string reaction,
        string parentMessageId,
        bool unreaction = false,
        int? expectedConnectionGeneration = null,
        CancellationToken cancellationToken = default)
    {
        return SendReactionCoreAsync(
            target,
            reaction,
            parentMessageId,
            unreaction ? IrcReactionOperation.Unreact : IrcReactionOperation.React,
            expectedConnectionGeneration,
            cancellationToken);
    }

    public ValueTask SendUnreactionAsync(
        string target,
        string reaction,
        string parentMessageId,
        int? expectedConnectionGeneration = null,
        CancellationToken cancellationToken = default) =>
        SendReactionCoreAsync(
            target,
            reaction,
            parentMessageId,
            IrcReactionOperation.Unreact,
            expectedConnectionGeneration,
            cancellationToken);

    private async ValueTask SendReactionCoreAsync(
        string target,
        string reaction,
        string parentMessageId,
        IrcReactionOperation operation,
        int? expectedConnectionGeneration,
        CancellationToken cancellationToken)
    {
        IrcOutboundMessage message;
        ConnectionEpoch epoch;
        lock (_gate)
        {
            if (_registration != RegistrationState.Registered || _activeEpoch is not { IsActive: true } currentEpoch)
            {
                throw new InvalidOperationException("The IRC session is not registered.");
            }

            if (expectedConnectionGeneration is int expected && expected != currentEpoch.Generation)
            {
                throw new InvalidOperationException("The reaction belongs to a stale connection generation.");
            }

            if (!_capabilities.Snapshot.IsEnabled(IrcCapabilityCatalog.MessageTags))
            {
                throw new InvalidOperationException("The server did not negotiate message-tags; reactions are unavailable.");
            }

            if (_features.RuntimeISupport.IsClientTagDenied("+reply")
                || _features.RuntimeISupport.IsClientTagDenied("reply")
                || _features.RuntimeISupport.IsClientTagDenied(operation == IrcReactionOperation.React ? "+draft/react" : "+draft/unreact")
                || _features.RuntimeISupport.IsClientTagDenied(operation == IrcReactionOperation.React ? "draft/react" : "draft/unreact"))
            {
                throw new InvalidOperationException("The server's CLIENTTAGDENY policy disallows this reaction.");
            }

            message = IrcReactionCommandBuilder.Build(
                new IrcCommandBuilder(_options.MaximumOutboundLineBytes),
                target,
                reaction,
                operation,
                parentMessageId);
            epoch = currentEpoch;
        }

        await QueueOutboundAsync(message, cancellationToken, epoch).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(string? reason = null)
    {
        Task? runTask;
        lock (_gate)
        {
            if (_disconnectRequested)
            {
                runTask = _runTask;
            }
            else
            {
                _disconnectRequested = true;
                runTask = _runTask;
                if (_state is not ServerSessionState.Disconnected and not ServerSessionState.Disconnecting)
                {
                    SetStateUnsafe(ServerSessionState.Disconnecting);
                }
            }
        }

        PublishContinuityTransition(MarkIntentionalDisconnect(reason ?? "The user requested disconnect."));

        if (runTask is null)
        {
            return;
        }

        if (_registration == RegistrationState.Registered && Interlocked.Exchange(ref _quitSent, 1) == 0)
        {
            try
            {
                await SendCommandAsync("QUIT", trailingParameter: reason ?? "nexIRC disconnect").ConfigureAwait(false);
                await _quitWritten.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
            catch (TimeoutException)
            {
            }
        }

        _runCts?.Cancel();
        await runTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            _disposeTask = DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>
    /// Completes the replacement-generation synchronization barrier.  The
    /// generation check is the ownership fence for asynchronous history work.
    /// </summary>
    public bool CompleteSynchronization(
        int connectionGeneration,
        ContinuitySynchronizationOutcome outcome,
        string? detail = null)
    {
        var result = ConnectionContinuityRecoveryResult.FromOutcome(
            connectionGeneration,
            true,
            outcome,
            historyAvailable: outcome is not ContinuitySynchronizationOutcome.Unsupported,
            recoveryRequestSent: outcome is ContinuitySynchronizationOutcome.Recovered
                or ContinuitySynchronizationOutcome.Partial
                or ContinuitySynchronizationOutcome.Failed,
            replayCompleted: outcome == ContinuitySynchronizationOutcome.Recovered,
            recoveryImpossible: outcome == ContinuitySynchronizationOutcome.Unsupported,
            detail: detail);
        return CompleteSynchronization(connectionGeneration, result, detail);
    }

    public bool CompleteSynchronization(
        int connectionGeneration,
        ConnectionContinuityRecoveryResult result,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ConnectionContinuityTransition? transition;
        lock (_gate)
        {
            transition = _continuity.CompleteSynchronization(
                connectionGeneration,
                result,
                DateTimeOffset.UtcNow,
                detail);
        }

        PublishContinuityTransition(transition);
        return transition is not null;
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await DisconnectAsync("nexIRC session disposed").ConfigureAwait(false);
        }
        finally
        {
            _disposeCts.Cancel();
            _disposeCts.Dispose();
            Interlocked.Decrement(ref _liveInstanceCount);
        }
    }

    private Task StartRun(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        return RunSupervisorAsync(_runCts.Token);
    }

    private async Task LoadProtectedResumeStateAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_resumeStateLoadCompleted)
            {
                return;
            }

            _resumeStateLoadCompleted = true;
        }

        if (_resumeStateStore is null || _resumeSecretProtector is null)
        {
            return;
        }

        ResumeStateLoadResult loaded;
        try
        {
            loaded = await _resumeStateStore.LoadAsync(_resumeNetworkIdentity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _continuity.RecordDiagnostic(0, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state could not be loaded; ordinary IRC fallback remains available.");
            return;
        }

        if (!loaded.IsUsable || loaded.State is not { } state)
        {
            if (loaded.Status is ResumeStateLoadStatus.Corrupt or ResumeStateLoadStatus.Unsupported or ResumeStateLoadStatus.Unavailable)
            {
                _continuity.RecordDiagnostic(0, ContinuityDiagnosticKind.RecoveryRequestFailed, $"Protected native-resume state was not usable ({loaded.Status}); ordinary IRC fallback remains available.");
            }

            return;
        }

        if (!state.IsSupported)
        {
            _continuity.RecordDiagnostic(0, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state uses an unsupported persistence version.");
            return;
        }

        if (!state.IsWellFormed || !string.Equals(state.NetworkIdentity, _resumeNetworkIdentity, StringComparison.Ordinal))
        {
            _continuity.RecordDiagnostic(0, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state failed its local binding validation.");
            return;
        }

        try
        {
            var currentToken = _resumeSecretProtector.Unprotect(state.ProtectedCurrentToken, _resumeNetworkIdentity);
            var pendingToken = state.ProtectedPendingToken is { Length: > 0 } protectedPending
                ? _resumeSecretProtector.Unprotect(protectedPending, _resumeNetworkIdentity)
                : null;
            var sessionCredential = state.ProtectedSessionCredential is { Length: > 0 } protectedSessionCredential
                ? _resumeSecretProtector.Unprotect(protectedSessionCredential, _resumeNetworkIdentity)
                : null;
            var pairingRecoveryCredential = state.ProtectedPairingRecoveryCredential is { Length: > 0 } protectedPairingRecoveryCredential
                ? _resumeSecretProtector.Unprotect(protectedPairingRecoveryCredential, _resumeNetworkIdentity)
                : null;
            var restored = NexIrcResumeSession.FromDurableState(
                currentToken,
                pendingToken,
                state.AuthoritativeBoundary,
                state.TokenGeneration,
                state.PendingTokenGeneration,
                state.AttachmentId,
                sessionCredential);
            lock (_gate)
            {
                _loadedResumeState = state;
                _resumeStateCreatedAt = state.CreatedAt;
                _nativeResumeSession = restored;
                _pairingRecoveryCredential = pairingRecoveryCredential;
                _pairingRecoveryBoundary = state.PairingRecoveryBoundary;
                _pairingRecoveryCredentialsDurable = state.PairingRecoveryCredentialsDurable;
                _restoredResumeActive = true;
            }
        }
        catch
        {
            _continuity.RecordDiagnostic(0, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state could not be unprotected; ordinary IRC fallback remains available.");
        }
    }

    private bool TryActivateRestoredResumeStateUnsafe()
    {
        if (!_restoredResumeActive
            || _nativeResumeSession is null
            || _loadedResumeState is not { } loaded
            || !NexIrcResumeProtocol.IsSupported(_capabilities.Snapshot))
        {
            return false;
        }

        if (_options.SaslPolicy != SaslAuthenticationPolicy.Disabled
            && _authenticationState != SaslAuthenticationState.Succeeded)
        {
            return false;
        }

        var account = _authenticatedAccountHint ?? (_options.SaslPolicy == SaslAuthenticationPolicy.Disabled ? _options.Username : null);
        if (account is null || !string.Equals(account, loaded.AccountIdentity, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private async ValueTask<bool> PersistNativeResumeStateAsync(
        NexIrcResumeSession session,
        string? accountIdentity = null,
        CancellationToken cancellationToken = default)
    {
        if (_resumeStateStore is null || _resumeSecretProtector is null)
        {
            // Protected persistence is optional for headless/third-party
            // composition. Preserve the Phase 36 in-memory protocol contract
            // when no durable provider has been installed.
            return true;
        }

        var account = accountIdentity ?? _authenticatedAccountHint ?? _options.Username;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset createdAt;
        string? pairingRecoveryCredential;
        string? pairingRecoveryBoundary;
        bool pairingRecoveryCredentialsDurable;
        lock (_gate)
        {
            createdAt = _resumeStateCreatedAt ?? now;
            pairingRecoveryCredential = _pairingRecoveryCredential;
            pairingRecoveryBoundary = _pairingRecoveryBoundary;
            pairingRecoveryCredentialsDurable = _pairingRecoveryCredentialsDurable;
        }

        try
        {
            var state = new ClientResumeStateRecord(
                ClientResumeStateRecord.CurrentVersion,
                _resumeNetworkIdentity,
                account,
                NexIrcResumeProtocol.CapabilityVersion,
                _resumeSecretProtector.Protect(session.DurableCurrentToken, _resumeNetworkIdentity),
                session.HasPendingRotation
                    ? _resumeSecretProtector.Protect(session.Token, _resumeNetworkIdentity)
                    : null,
                session.DurableCurrentGeneration,
                session.HasPendingRotation ? session.EstablishedGeneration : null,
                session.HasPendingRotation ? session.DurableCurrentGeneration : session.EstablishedGeneration,
                session.AuthoritativeBoundary,
                createdAt,
                now,
                AttachmentId: session.AttachmentId,
                ProtectedSessionCredential: session.SessionCredential is { } sessionCredential
                    ? _resumeSecretProtector.Protect(sessionCredential, _resumeNetworkIdentity)
                    : null,
                ProtectedPairingRecoveryCredential: pairingRecoveryCredential is { } recoveryCredential
                    ? _resumeSecretProtector.Protect(recoveryCredential, _resumeNetworkIdentity)
                    : null,
                PairingRecoveryBoundary: pairingRecoveryBoundary,
                PairingRecoveryCredentialsDurable: pairingRecoveryCredentialsDurable);
            var result = await _resumeStateStore.SaveAsync(state, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _continuity.RecordDiagnostic(_connectionGeneration, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state could not be durably committed; the in-memory session remains bounded and fallback-safe.");
                return false;
            }

            lock (_gate)
            {
                _resumeStateCreatedAt = createdAt;
                _loadedResumeState = state;
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _continuity.RecordDiagnostic(_connectionGeneration, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state persistence failed without exposing credentials.");
            return false;
        }
    }

    private async ValueTask ClearProtectedResumeStateAsync(
        bool clearInMemory = true,
        CancellationToken cancellationToken = default)
    {
        if (_resumeStateStore is null)
        {
            return;
        }

        try
        {
            _ = await _resumeStateStore.DeleteAsync(_resumeNetworkIdentity, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _continuity.RecordDiagnostic(_connectionGeneration, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected native-resume state cleanup failed safely.");
        }
        finally
        {
            if (clearInMemory)
            {
                lock (_gate)
                {
                    _loadedResumeState = null;
                    _nativeResumeSession = null;
                    _freshSessionAfterRestoredResume = null;
                    _pairingRecoveryCredential = null;
                    _pairingRecoveryBoundary = null;
                    _pairingRecoveryCredentialsDurable = false;
                    _restoredResumeActive = false;
                }
            }
        }
    }

    private async Task RunSupervisorAsync(CancellationToken cancellationToken)
    {
        var reconnectAttempt = 0;
        try
        {
            await LoadProtectedResumeStateAsync(cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested && !_disconnectRequested)
            {
                reconnectAttempt++;
                IIrcTransport? transport = null;
                ConnectionEpoch? epoch = null;
                ConnectionFailure? failure;
                try
                {
                    SetState(ServerSessionState.Connecting);
                    transport = await _transportFactory.CreateAsync(_options.Endpoint, cancellationToken).ConfigureAwait(false);
                    epoch = BeginConnectionGeneration();
                    _identityDetector.ObserveHostname(_options.Endpoint.Host);
                    if (_options.Endpoint.UseTls)
                    {
                        SetState(ServerSessionState.TlsNegotiation);
                    }

                    await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    SetState(ServerSessionState.Connected);
                    failure = await RunConnectionAsync(transport, epoch, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    failure = new ConnectionFailure(ConnectionFailureKind.Cancelled, "The session was cancelled.", IsTransient: false);
                }
                catch (IrcTransportException exception)
                {
                    failure = exception.Failure;
                }
                catch (Exception exception)
                {
                    failure = new ConnectionFailure(ConnectionFailureKind.Unknown, $"The IRC session failed: {exception.Message}", exception);
                }
                finally
                {
                    if (transport is not null)
                    {
                        try
                        {
                            await transport.DisconnectAsync(null, CancellationToken.None).ConfigureAwait(false);
                            await transport.DisposeAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
                        {
                            _lastFailure ??= new ConnectionFailure(ConnectionFailureKind.Network, "The IRC transport did not close cleanly.", exception);
                        }
                    }
                }

                if (epoch is not null && !_disconnectRequested && !cancellationToken.IsCancellationRequested)
                {
                    if (failure.IsTransient)
                    {
                        PublishContinuityTransition(MarkInterrupted(epoch.Generation, failure));
                    }
                    else if (failure.Kind is not ConnectionFailureKind.Cancelled and not ConnectionFailureKind.Intentional)
                    {
                        PublishContinuityTransition(MarkTerminal(epoch.Generation, failure.Message));
                    }

                    if (_state == ServerSessionState.Failed)
                    {
                        PublishContinuityTransition(MarkTerminal(epoch.Generation, failure.Message));
                    }
                }
                else if (epoch is null
                    && !_disconnectRequested
                    && !cancellationToken.IsCancellationRequested
                    && failure.Kind is not ConnectionFailureKind.Cancelled and not ConnectionFailureKind.Intentional)
                {
                    PublishContinuityTransition(MarkTerminal(_connectionGeneration, failure.Message));
                }

                if (_disconnectRequested || cancellationToken.IsCancellationRequested || failure.Kind is ConnectionFailureKind.Cancelled or ConnectionFailureKind.Intentional)
                {
                    break;
                }

                _lastFailure = failure;
                ResetForReconnect();
                if (!_options.Reconnect.Enabled || reconnectAttempt >= Math.Max(1, _options.Reconnect.MaximumAttempts))
                {
                    if (epoch is not null)
                    {
                        PublishContinuityTransition(MarkTerminal(epoch.Generation, failure.Message));
                    }
                    else
                    {
                        PublishContinuityTransition(MarkTerminal(_connectionGeneration, failure.Message));
                    }
                    SetState(ServerSessionState.Failed);
                    break;
                }

                SetState(ServerSessionState.ReconnectWaiting);
                var delay = CalculateReconnectDelay(reconnectAttempt, _options.Reconnect);
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            _outbound?.Writer.TryComplete();
            _rawEvents.Writer.TryComplete();
            _parsedEvents.Writer.TryComplete();
            _parseErrors.Writer.TryComplete();
            _semanticEvents.Writer.TryComplete();
            _outboundEvents.Writer.TryComplete();
            if (_state != ServerSessionState.Failed)
            {
                if (_disconnectRequested || cancellationToken.IsCancellationRequested)
                {
                    PublishContinuityTransition(MarkIntentionalDisconnect("The IRC session shut down."));
                }
                SetState(ServerSessionState.Disconnected);
            }
        }
    }

    private async Task<ConnectionFailure> RunConnectionAsync(IIrcTransport transport, ConnectionEpoch epoch, CancellationToken sessionCancellation)
    {
        var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCancellation);
        var outbound = Channel.CreateBounded<IrcOutboundMessage>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        lock (_gate)
        {
            _outbound = outbound;
        }

        IIrcTransportCallbackSource? callbackSource = transport as IIrcTransportCallbackSource;
        Func<IrcTransportCallback, ValueTask>? callbackHandler = null;
        if (callbackSource is not null)
        {
            var weakSession = new WeakReference<ServerSession>(this);
            callbackHandler = callback => weakSession.TryGetTarget(out var session)
                ? session.ProcessTransportCallbackAsync(callback, epoch, connectionCts)
                : ValueTask.CompletedTask;
            callbackSource.CallbackReceived += callbackHandler;
        }

        var writerFailure = new StrongBox<ConnectionFailure?>(null);
        var writerTask = WriteLoopAsync(transport, outbound.Reader, connectionCts, writerFailure, epoch);
        var capabilityFallbackTask = CapabilityFallbackAsync(outbound.Writer, epoch, connectionCts);
        try
        {
            SetState(ServerSessionState.CapNegotiation);
            _registration = RegistrationState.CapNegotiating;
            await QueueRegistrationAsync(outbound.Writer, epoch, connectionCts.Token).ConfigureAwait(false);
            return await ReadLoopAsync(transport, epoch, connectionCts, writerFailure).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
            return writerFailure.Value ?? new ConnectionFailure(
                _disconnectRequested ? ConnectionFailureKind.Intentional : ConnectionFailureKind.Cancelled,
                "The IRC connection was cancelled.",
                IsTransient: false);
        }
        catch (IrcTransportException exception)
        {
            return exception.Failure;
        }
        catch (IrcLineTooLongException exception)
        {
            return new ConnectionFailure(ConnectionFailureKind.Protocol, exception.Message, exception, IsTransient: false);
        }
        catch (Exception exception)
        {
            return new ConnectionFailure(ConnectionFailureKind.Unknown, $"The IRC protocol loop failed: {exception.Message}", exception);
        }
        finally
        {
            if (callbackSource is not null && callbackHandler is not null)
            {
                callbackSource.CallbackReceived -= callbackHandler;
            }

            outbound.Writer.TryComplete();
            connectionCts.Cancel();
            try
            {
                await writerTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IrcTransportException or IOException)
            {
            }

            try
            {
                await capabilityFallbackTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IrcTransportException or IOException)
            {
            }

            lock (_gate)
            {
                _outbound = null;
            }

            InvalidateConnectionState(epoch);

            connectionCts.Dispose();
        }
    }

    private async Task QueueRegistrationAsync(ChannelWriter<IrcOutboundMessage> writer, ConnectionEpoch epoch, CancellationToken cancellationToken)
    {
        if (!IsCurrentEpoch(epoch))
        {
            _continuity.RecordStaleCallback(epoch.Generation, "A stale inbound frame was ignored.");
            return;
        }

        var capStart = _capabilities.Start();
        foreach (var command in capStart.Commands)
        {
            await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }

        var password = _options.Password;
        if (password is null && _options.PasswordProvider is not null)
        {
            try
            {
                password = await _options.PasswordProvider.GetPasswordAsync(_options.Endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A vault/provider failure must not take down the session or
                // cause a plaintext fallback. Registration continues without
                // PASS and the provider remains responsible for diagnostics.
                password = null;
            }
        }

        if (!string.IsNullOrEmpty(password))
        {
            await writer.WriteAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("PASS", trailingParameter: password), cancellationToken).ConfigureAwait(false);
        }

        SetState(ServerSessionState.CapNegotiation);
    }

    private async Task CapabilityFallbackAsync(
        ChannelWriter<IrcOutboundMessage> writer,
        ConnectionEpoch epoch,
        CancellationTokenSource connectionCts)
    {
        try
        {
            await Task.Delay(_options.CapabilityNegotiationTimeout, connectionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
            return;
        }

        CapNegotiationResult result;
        lock (_gate)
        {
            if (!IsCurrentEpochUnsafe(epoch) || _capabilities.Snapshot.NegotiationState == CapNegotiationState.Ended)
            {
                return;
            }

            result = _capabilities.Complete();
        }

        foreach (var command in result.Commands)
        {
            await writer.WriteAsync(command, connectionCts.Token).ConfigureAwait(false);
        }

        if (result.Snapshot.NegotiationState == CapNegotiationState.Ended)
        {
            if (_options.SaslPolicy != SaslAuthenticationPolicy.Disabled)
            {
                lock (_gate)
                {
                    if (_authenticationState == SaslAuthenticationState.WaitingForCapability)
                    {
                        _authenticationState = SaslAuthenticationState.Skipped;
                    }
                }
            }

            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required && !result.Snapshot.IsAvailable(IrcCapabilityCatalog.Sasl))
            {
                lock (_gate)
                {
                    _authenticationState = SaslAuthenticationState.Failed;
                    _authenticationFailure = "The server did not complete CAP negotiation and SASL is required.";
                    _registration = RegistrationState.Failed;
                    _lastFailure = new ConnectionFailure(ConnectionFailureKind.RegistrationRejected, _authenticationFailure, IsTransient: false);
                }

                SetState(ServerSessionState.Failed);
                connectionCts.Cancel();
                return;
            }

            await BeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
        }
    }

    private async Task<ConnectionFailure> ReadLoopAsync(IIrcTransport transport, ConnectionEpoch epoch, CancellationTokenSource connectionCts, StrongBox<ConnectionFailure?> writerFailure)
    {
        var framer = new IrcLineFramer(_options.MaximumInboundLineBytes);
        var buffer = new byte[_options.ReadBufferBytes];
        while (!connectionCts.IsCancellationRequested)
        {
            var read = await transport.ReadAsync(buffer, connectionCts.Token).ConfigureAwait(false);
            if (read == 0)
            {
                var incomplete = framer.Disconnect();
                if (incomplete.HasIncompleteLine)
                {
                    await PublishParseErrorAsync(incomplete.IncompleteText ?? string.Empty, "The server disconnected with an incomplete IRC line.", epoch).ConfigureAwait(false);
                }

                return transport.LastFailure ?? new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "The remote IRC server closed the connection.");
            }

            IReadOnlyList<IrcLineFrame> frames;
            try
            {
                frames = framer.Push(buffer.AsSpan(0, read));
            }
            catch (IrcLineTooLongException exception)
            {
                connectionCts.Cancel();
                return new ConnectionFailure(ConnectionFailureKind.Protocol, exception.Message, exception, IsTransient: false);
            }

            foreach (var frame in frames)
            {
                await ProcessFrameAsync(frame, epoch, connectionCts).ConfigureAwait(false);
            }
        }

        return writerFailure.Value ?? new ConnectionFailure(ConnectionFailureKind.Cancelled, "The IRC connection loop was cancelled.", IsTransient: false);
    }

    private async Task WriteLoopAsync(
        IIrcTransport transport,
        ChannelReader<IrcOutboundMessage> reader,
        CancellationTokenSource connectionCts,
        StrongBox<ConnectionFailure?> writerFailure,
        ConnectionEpoch epoch)
    {
        try
        {
            await foreach (var command in reader.ReadAllAsync(connectionCts.Token).ConfigureAwait(false))
            {
                await transport.WriteAsync(command.FramedBytes, connectionCts.Token).ConfigureAwait(false);
                if (IsQuit(command.Line))
                {
                    _quitWritten.TrySetResult();
                }

                if (IsCurrentEpoch(epoch))
                {
                    var redactedBytes = IrcSensitiveData.RedactFramedBytes(command.FramedBytes.Span);
                    var outboundEvent = new OutboundIrcCommandEvent(
                        DateTimeOffset.UtcNow,
                        IrcSensitiveData.RedactLine(command.Line),
                        redactedBytes,
                        epoch.Generation);
                    _outboundEvents.Writer.TryWrite(outboundEvent);
                    PublishOutboundDiagnostic(outboundEvent);
                }
            }
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
        }
        catch (IrcTransportException exception)
        {
            writerFailure.Value = exception.Failure;
            connectionCts.Cancel();
        }
        catch (Exception exception)
        {
            writerFailure.Value = new ConnectionFailure(ConnectionFailureKind.Network, $"The outbound IRC queue failed: {exception.Message}", exception);
            connectionCts.Cancel();
        }
    }

    private async Task ProcessFrameAsync(IrcLineFrame frame, ConnectionEpoch epoch, CancellationTokenSource connectionCts)
    {
        if (!IsCurrentEpoch(epoch))
        {
            _continuity.RecordStaleCallback(epoch.Generation, "A stale inbound frame was ignored.");
            return;
        }

        var receivedAt = DateTimeOffset.UtcNow;
        var redactedLine = IrcSensitiveData.RedactLine(frame.Text);
        var redactedBytes = IrcSensitiveData.RedactFramedBytes(frame.Bytes.Span);
        var rawEvent = new RawIrcLineEvent(receivedAt, redactedLine, redactedBytes, epoch.Generation);
        await _rawEvents.Writer.WriteAsync(rawEvent, connectionCts.Token).ConfigureAwait(false);
        PublishRawDiagnostic(rawEvent);
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var parse = IrcMessageParser.Parse(frame.Text);
        if (!parse.Success)
        {
            await PublishParseErrorAsync(IrcSensitiveData.RedactLine(frame.Text), parse.Error ?? "The IRC line could not be parsed.", epoch).ConfigureAwait(false);
            return;
        }

        var message = parse.Message!;
        await _parsedEvents.Writer.WriteAsync(new ParsedIrcMessageEvent(receivedAt, IrcSensitiveData.RedactParsedMessage(message), epoch.Generation), connectionCts.Token).ConfigureAwait(false);
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }
        CapabilitySnapshot capabilitiesBefore;
        CapNegotiationResult capResult;
        lock (_gate)
        {
            if (!IsCurrentEpochUnsafe(epoch))
            {
                return;
            }

            capabilitiesBefore = _capabilities.Snapshot;
            capResult = _capabilities.Handle(message);
        }
        if (message.Command == "CAP")
        {
            lock (_gate)
            {
                _identityDetector.ObserveCapabilities(capResult.Snapshot);
                _features = ServerFeatureSet.Build(capResult.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot, _options.MaximumChathistoryRequestSize);
            }

            await PublishCapabilityChangesAsync(message, capabilitiesBefore, capResult.Snapshot, epoch, receivedAt).ConfigureAwait(false);
            await HandleCapabilityResultAsync(message, capResult, epoch, connectionCts).ConfigureAwait(false);
        }

        if (message.NumericCommand == 5)
        {
            lock (_gate)
            {
                _isupport.Apply(message);
                _identityDetector.ObserveISupport(_isupport.Snapshot);
                _features = ServerFeatureSet.Build(_capabilities.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot, _options.MaximumChathistoryRequestSize);
                _stateStore.SetCaseMapping(_features.CaseMapping);
            }
        }

        if (message.NumericCommand == 421
            && message.Parameters.Any(static parameter => parameter.Equals("CAP", StringComparison.OrdinalIgnoreCase)))
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                lock (_gate)
                {
                    _authenticationState = SaslAuthenticationState.Failed;
                    _authenticationFailure = "The server does not support CAP and SASL is required.";
                    _registration = RegistrationState.Failed;
                    _lastFailure = new ConnectionFailure(ConnectionFailureKind.RegistrationRejected, _authenticationFailure, IsTransient: false);
                }

                SetState(ServerSessionState.Failed);
                connectionCts.Cancel();
                return;
            }

            CapNegotiationResult caplessResult;
            lock (_gate)
            {
                caplessResult = _capabilities.Complete();
            }
            foreach (var command in caplessResult.Commands)
            {
                await QueueOutboundAsync(command, connectionCts.Token, epoch).ConfigureAwait(false);
            }

            if (caplessResult.Snapshot.NegotiationState == CapNegotiationState.Ended)
            {
                if (_options.SaslPolicy != SaslAuthenticationPolicy.Disabled)
                {
                    await SetAuthenticationStateAsync(message, SaslAuthenticationState.Skipped, null, "The server does not support CAP; continuing without authentication.", epoch, receivedAt).ConfigureAwait(false);
                }

                await BeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
            }
        }
        else if (message.NumericCommand == 4)
        {
            lock (_gate)
            {
                _identityDetector.ObserveMyInfo(message);
                _features = ServerFeatureSet.Build(_capabilities.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot, _options.MaximumChathistoryRequestSize);
            }
        }

        if (message.NumericCommand == 1)
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required && _authenticationState != SaslAuthenticationState.Succeeded)
            {
                await FailAuthenticationAsync(message, "SASL authentication was required before registration.", epoch, connectionCts, fatal: true).ConfigureAwait(false);
                return;
            }

            var previousRegistration = _registration;
            _registration = RegistrationState.Registered;
            var welcomeNickname = message.Parameters.Count > 0 ? message.Parameters[0] : null;
            if (!string.IsNullOrWhiteSpace(welcomeNickname) && !NamesEqual(welcomeNickname, _stateStore.Nickname, _features.CaseMapping))
            {
                _stateStore.SetNickname(welcomeNickname);
            }

            var continuityRequiresSynchronization = _continuity.Snapshot.SynchronizationRequired;
            PublishContinuityTransition(MarkRegistrationComplete(
                epoch.Generation,
                continuityRequiresSynchronization));
            SetState(ServerSessionState.Registered);
            lock (_gate)
            {
                if (TryActivateRestoredResumeStateUnsafe())
                {
                    _continuity.RecordDiagnostic(
                        epoch.Generation,
                        ContinuityDiagnosticKind.RegistrationCompleted,
                        $"Protected native-resume state bound to authenticated account; session={_nativeResumeSession!.TokenFingerprint}; boundary={_nativeResumeSession.AuthoritativeBoundary}");
                }
            }
            await PublishSemanticAsync(new IrcRegistrationStateEvent(message, previousRegistration, _registration), epoch, receivedAt).ConfigureAwait(false);
            await QueueDesiredChannelsAsync(epoch, connectionCts.Token).ConfigureAwait(false);
        }

        if (message.NumericCommand is int numeric && NicknameFailureNumerics.Contains(numeric))
        {
            await HandleNicknameFailureAsync(epoch, connectionCts).ConfigureAwait(false);
        }

        if (message.Command == "AUTHENTICATE")
        {
            await HandleSaslChallengeAsync(message, epoch, connectionCts).ConfigureAwait(false);
        }

        if (message.NumericCommand is int authenticationNumeric && IsSaslSuccess(authenticationNumeric))
        {
            await CompleteSaslAsync(message, epoch, connectionCts).ConfigureAwait(false);
        }
        else if (message.NumericCommand is int authenticationFailureNumeric && IsSaslFailure(authenticationFailureNumeric))
        {
            await FailAuthenticationAsync(message, "The server rejected SASL authentication.", epoch, connectionCts, fatal: _options.SaslPolicy == SaslAuthenticationPolicy.Required).ConfigureAwait(false);
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                return;
            }
        }

        if (message.Command == "PING")
        {
            var payload = message.HasTrailingParameter ? message.TrailingParameter ?? string.Empty : message.Parameters.Count > 0 ? message.Parameters[0] : string.Empty;
            await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("PONG", trailingParameter: payload), connectionCts.Token, epoch).ConfigureAwait(false);
            await PublishSemanticAsync(new IrcPingEvent(message, payload), epoch, receivedAt).ConfigureAwait(false);
        }

        if (message.Command == "ERROR")
        {
            _lastFailure = new ConnectionFailure(ConnectionFailureKind.Protocol, message.HasTrailingParameter ? message.TrailingParameter ?? "The IRC server reported an error." : "The IRC server reported an error.", IsTransient: false);
            _registration = RegistrationState.Failed;
            SetState(ServerSessionState.Failed);
            await PublishSemanticAsync(new IrcServerErrorEvent(message, _lastFailure.Message), epoch, receivedAt).ConfigureAwait(false);
            connectionCts.Cancel();
        }

        if (message.Command == NexIrcResumeProtocol.Command)
        {
            if (message.Parameters.Count > 0 && string.Equals(message.Parameters[0], "STATE", StringComparison.OrdinalIgnoreCase))
                await HandleNexIrcReadStateMessageAsync(message, epoch).ConfigureAwait(false);
            else
                await HandleNativeResumeMessageAsync(message, epoch, connectionCts).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<IrcSemanticEvent> stateEvents;
        var historicalPlayback = false;
        var nativeResumePlayback = false;
        var nativeResumeDuplicate = false;
        var nativeLiveEvent = false;
        var nativeLiveDuplicate = false;
        var suppressBatchedState = false;
        var historyLimitExceeded = false;
        lock (_gate)
        {
            if (message.BatchId is { } batchId
                && _stateStore.TryGetActiveBatch(batchId, out var batchType, out _)
                && IsAcceptedReplayBatchType(batchType))
            {
                historicalPlayback = _acceptedHistoryBatches.Contains(batchId)
                    && _activeHistoryRequest is { ConnectionGeneration: var generation }
                    && generation == epoch.Generation;
                nativeResumePlayback = _acceptedNativeResumeBatches.Contains(batchId)
                    && _nativeResumeAttempt is { ConnectionGeneration: var resumeGeneration }
                    && resumeGeneration == epoch.Generation;
                suppressBatchedState = !historicalPlayback && !nativeResumePlayback;
                if (historicalPlayback
                    && message.Command is "PRIVMSG" or "NOTICE"
                    && !ChathistoryContext.IsContextRow(message)
                    && _activeHistoryRequest!.Messages.Count(item => !ChathistoryContext.IsContextRow(item.Message)) >= _activeHistoryRequest.Request.Limit)
                {
                    historyLimitExceeded = true;
                    historicalPlayback = false;
                    suppressBatchedState = true;
                }

                if (nativeResumePlayback && !ValidateNativeReplayEnvelopeUnsafe(message, epoch.Generation, out nativeResumeDuplicate))
                {
                    return;
                }

            }

            if (message.BatchId is null
                && _nativeResumeSession is not null
                && message.TagValues.ContainsKey(NexIrcResumeProtocol.ResumeSequenceTag))
            {
                nativeLiveEvent = true;
                if (!ValidateNativeLiveEnvelopeUnsafe(message, epoch.Generation, out nativeLiveDuplicate))
                {
                    return;
                }
            }

            stateEvents = _stateStore.Apply(message, _features, historicalPlayback || nativeResumePlayback, suppressBatchedState);
        }

        if (nativeResumeDuplicate)
        {
            return;
        }

        if (nativeLiveDuplicate)
        {
            return;
        }

        if (nativeResumePlayback && message.Command != "BATCH" && stateEvents.Count == 0)
        {
            FailNativeResumeAttempt(
                epoch.Generation,
                NexIrcResumeOutcome.Failed,
                $"A native replay event did not enter the canonical semantic pipeline (command={message.Command}; batch={message.BatchId}).",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        if (historyLimitExceeded)
        {
            CompleteHistoryRequest(ChathistoryRequestCompletion.Failed, "The history batch exceeded the bounded request limit.");
        }

        var projectionBarrier = _nativeEventProjector is not null;
        if (!projectionBarrier && nativeLiveEvent && stateEvents.Count > 0)
        {
            await CommitNativeLiveEventAsync(message, epoch.Generation).ConfigureAwait(false);
        }

        var nativeReplayEventAccepted = false;
        var deliveryEvents = new List<IrcSemanticEvent>(stateEvents.Count);
        foreach (var semanticEvent in stateEvents)
        {
            var deliveryEvent = semanticEvent;

            if (semanticEvent.IsHistorical)
            {
                lock (_gate)
                {
                    if (_activeHistoryRequest is { ConnectionGeneration: var generation } pending
                        && generation == epoch.Generation)
                    {
                        if (semanticEvent is IrcHistoryTargetEvent target)
                        {
                            pending.Targets.Add(new ChathistoryTarget(
                                _options.NetworkId ?? Guid.Empty,
                                epoch.Generation,
                                target.Target,
                                target.LatestTimestamp,
                                target.Kind));
                        }
                        else
                        {
                            deliveryEvent = semanticEvent with
                            {
                                Source = IrcSemanticEventSource.ServerPlayback,
                                NetworkId = _options.NetworkId,
                                HistoricalConversation = pending.Request.Conversation
                            };
                            pending.Messages.Add(deliveryEvent);
                        }
                    }
                }

                if (nativeResumePlayback)
                {
                    deliveryEvent = semanticEvent with
                    {
                        Source = IrcSemanticEventSource.ServerPlayback,
                        NetworkId = _options.NetworkId,
                        // The nexirc/resume batch parameter identifies the
                        // server's IRC target. It is not the Application's
                        // canonical history key (for example, `private:peer`).
                        // Leave this unset so recovered queries resolve from
                        // the event sender/recipient instead of creating an
                        // isolated conversation keyed by the IRC target.
                        HistoricalConversation = null
                    };
                }
            }

            var nativeReplaySemanticEvent = nativeResumePlayback && semanticEvent.IsHistorical;
            if (projectionBarrier && (nativeLiveEvent || nativeReplaySemanticEvent))
            {
                await ProjectNativeEventAsync(deliveryEvent, epoch, receivedAt, connectionCts.Token).ConfigureAwait(false);
            }
            else if (!projectionBarrier && nativeReplaySemanticEvent)
            {
                await CommitNativeReplayEventAsync(message, epoch.Generation).ConfigureAwait(false);
            }

            nativeReplayEventAccepted |= nativeReplaySemanticEvent;
            deliveryEvents.Add(deliveryEvent);
        }

        if (projectionBarrier && nativeLiveEvent && stateEvents.Count > 0)
        {
            await CommitNativeLiveEventAsync(message, epoch.Generation).ConfigureAwait(false);
        }

        if (projectionBarrier && nativeReplayEventAccepted)
        {
            await CommitNativeReplayEventAsync(message, epoch.Generation).ConfigureAwait(false);
        }

        foreach (var deliveryEvent in deliveryEvents)
        {
            await PublishSemanticAsync(deliveryEvent, epoch, receivedAt).ConfigureAwait(false);

            if (!deliveryEvent.IsHistorical
                && deliveryEvent is IrcJoinEvent join
                && NamesEqual(join.Nickname, _stateStore.Nickname, _features.CaseMapping))
            {
                await QueueChannelResynchronizationAsync(join.Channel, epoch, connectionCts.Token).ConfigureAwait(false);
                await PublishSemanticAsync(new IrcChannelSynchronizationEvent(message, join.Channel, ChannelSynchronizationState.Synchronizing), epoch, receivedAt).ConfigureAwait(false);
            }
        }

        if (message.Command == "BATCH")
        {
            ObserveHistoryBatch(message, epoch);
            ObserveNativeResumeBatch(message, epoch);
        }

        if (message.Command is "FAIL" or "WARN" or "NOTE"
            || message.NumericCommand is 400 or 401 or 402 or 403 or 404 or 405 or 407 or 409 or 411 or 412 or 421 or 461)
        {
            ObserveHistoryError(message, epoch);
        }

        if (message.Command is "FAIL" or "WARN" or "NOTE")
        {
            var standardKind = message.Command switch
            {
                "FAIL" => IrcStandardReplyKind.Fail,
                "WARN" => IrcStandardReplyKind.Warn,
                _ => IrcStandardReplyKind.Note
            };
            var code = message.Parameters.Skip(1).FirstOrDefault() ?? string.Empty;
            await PublishSemanticAsync(new IrcStandardReplyEvent(message, standardKind, code, MessageText(message)), epoch, receivedAt).ConfigureAwait(false);
        }

        if (EventDispatcher.TryDispatch(message, out var extensionEvent))
        {
            await PublishSemanticAsync(extensionEvent!, epoch, receivedAt).ConfigureAwait(false);
        }

        if (message.NumericCommand is int finalNumeric)
        {
            if (!KnownNumerics.Contains(finalNumeric))
            {
                await PublishSemanticAsync(new IrcUnknownNumericEvent(message, finalNumeric), epoch, receivedAt).ConfigureAwait(false);
            }
            else if (finalNumeric is not 1 && !IrcNumericCatalog.IsRecognized(finalNumeric))
            {
                await PublishSemanticAsync(new IrcNumericEvent(message, finalNumeric), epoch, receivedAt).ConfigureAwait(false);
            }
        }
        else if (!KnownCommands.Contains(message.Command))
        {
            await PublishSemanticAsync(new IrcUnknownCommandEvent(message), epoch, receivedAt).ConfigureAwait(false);
        }
    }

    private void PublishRawDiagnostic(RawIrcLineEvent rawEvent)
    {
        try
        {
            RawLineReceived?.Invoke(this, rawEvent);
        }
        catch
        {
            // Diagnostics must never alter the protocol state machine.
        }
    }

    private void PublishOutboundDiagnostic(OutboundIrcCommandEvent outboundEvent)
    {
        try
        {
            OutboundCommandSent?.Invoke(this, outboundEvent);
        }
        catch
        {
            // Diagnostics must never alter the protocol state machine.
        }
    }

    private ValueTask ProcessTransportCallbackAsync(IrcTransportCallback callback, ConnectionEpoch epoch, CancellationTokenSource connectionCts)
    {
        if (!IsCurrentEpoch(epoch))
        {
            return ValueTask.CompletedTask;
        }

        switch (callback)
        {
            case IrcTransportInboundLineCallback inbound:
                return new ValueTask(ProcessFrameAsync(new IrcLineFrame(System.Text.Encoding.UTF8.GetBytes(inbound.Line.TrimEnd('\r', '\n'))), epoch, connectionCts));
            case IrcTransportFailureCallback failure:
                _lastFailure = failure.Failure;
                connectionCts.Cancel();
                return ValueTask.CompletedTask;
            case IrcTransportDisconnectedCallback disconnected:
                _lastFailure = disconnected.Failure ?? new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "The transport reported a delayed disconnect.");
                connectionCts.Cancel();
                return ValueTask.CompletedTask;
            default:
                return ValueTask.CompletedTask;
        }
    }

    private async Task HandleCapabilityResultAsync(
        IrcMessage message,
        CapNegotiationResult result,
        ConnectionEpoch epoch,
        CancellationTokenSource connectionCts)
    {
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var subcommand = FindCapSubcommand(message);
        var saslRequested = _options.SaslPolicy != SaslAuthenticationPolicy.Disabled && _capabilities.Snapshot.RequestedTokens.Contains(IrcCapabilityCatalog.Sasl, StringComparer.Ordinal);
        var saslAvailable = result.Snapshot.IsAvailable(IrcCapabilityCatalog.Sasl);
        if (saslRequested && subcommand == "LS" && !HasMoreCapabilityListing(message) && !saslAvailable)
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                await FailAuthenticationAsync(message, "The server does not advertise SASL.", epoch, connectionCts, fatal: true).ConfigureAwait(false);
                return;
            }

            await SetAuthenticationStateAsync(message, SaslAuthenticationState.Skipped, null, "SASL is unavailable; continuing without authentication.", epoch).ConfigureAwait(false);
        }

        if (subcommand == "NAK" && saslRequested && messageHasCapability(message, IrcCapabilityCatalog.Sasl))
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                await FailAuthenticationAsync(message, "The server rejected the SASL capability request.", epoch, connectionCts, fatal: true).ConfigureAwait(false);
                return;
            }

            await SetAuthenticationStateAsync(message, SaslAuthenticationState.Failed, _authenticationMechanism, "The server rejected the SASL capability request.", epoch).ConfigureAwait(false);
            DisposeActiveCredential();
            foreach (var command in result.Commands)
            {
                await QueueOutboundAsync(command, connectionCts.Token, epoch).ConfigureAwait(false);
            }

            if (result.Snapshot.NegotiationState == CapNegotiationState.Ended)
            {
                await BeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
            }
            return;
        }

        if (subcommand == "ACK" && saslRequested && result.Snapshot.IsEnabled(IrcCapabilityCatalog.Sasl))
        {
            await StartSaslAsync(message, result.Snapshot, epoch, connectionCts).ConfigureAwait(false);
            return;
        }

        foreach (var command in result.Commands)
        {
            await QueueOutboundAsync(command, connectionCts.Token, epoch).ConfigureAwait(false);
        }

        if (result.Snapshot.NegotiationState == CapNegotiationState.Ended)
        {
            await BeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
        }

        static bool messageHasCapability(IrcMessage capMessage, string capability)
        {
            var values = capMessage.HasTrailingParameter ? capMessage.TrailingParameter : null;
            return values is not null && values.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(value => value.TrimStart('-').Split('=', 2)[0].Equals(capability, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task StartSaslAsync(
        IrcMessage message,
        CapabilitySnapshot capabilities,
        ConnectionEpoch epoch,
        CancellationTokenSource connectionCts)
    {
        if (_authenticationState is SaslAuthenticationState.Negotiating or SaslAuthenticationState.Succeeded)
        {
            return;
        }

        var mechanism = SelectSaslMechanism(capabilities, _options.SaslMechanisms);
        if (mechanism is null)
        {
            await FailAuthenticationAsync(message, "No configured SASL mechanism is accepted by the server.", epoch, connectionCts, _options.SaslPolicy == SaslAuthenticationPolicy.Required).ConfigureAwait(false);
            return;
        }

        _authenticationMechanism = mechanism.Name.ToUpperInvariant();
        await SetAuthenticationStateAsync(message, SaslAuthenticationState.Negotiating, _authenticationMechanism, null, epoch).ConfigureAwait(false);
        if (_options.SaslCredentialProvider is null)
        {
            await FailAuthenticationAsync(message, "No SASL credential provider is configured.", epoch, connectionCts, _options.SaslPolicy == SaslAuthenticationPolicy.Required).ConfigureAwait(false);
            return;
        }

        SaslCredential? credential;
        try
        {
            credential = await _options.SaslCredentialProvider.GetCredentialsAsync(_options.Endpoint, mechanism.Name, connectionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            await FailAuthenticationAsync(message, "The SASL credential provider failed.", epoch, connectionCts, _options.SaslPolicy == SaslAuthenticationPolicy.Required).ConfigureAwait(false);
            return;
        }

        if (credential is null)
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                await FailAuthenticationAsync(message, "SASL credentials were unavailable.", epoch, connectionCts, fatal: true).ConfigureAwait(false);
            }
            else
            {
                await SetAuthenticationStateAsync(message, SaslAuthenticationState.Skipped, mechanism.Name, "SASL credentials were unavailable; continuing without authentication.", epoch).ConfigureAwait(false);
                await CompleteCapabilityAndBeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
            }

            return;
        }

        _activeCredential = credential;
        _authenticatedAccountHint = credential.UserName;
        _activeSaslMechanism = mechanism;
        _saslResponseSent = false;
        await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("AUTHENTICATE", [mechanism.Name]), connectionCts.Token, epoch).ConfigureAwait(false);
    }

    private async Task HandleSaslChallengeAsync(IrcMessage message, ConnectionEpoch epoch, CancellationTokenSource connectionCts)
    {
        if (_authenticationState != SaslAuthenticationState.Negotiating || _saslResponseSent || _activeCredential is null || _activeSaslMechanism is null)
        {
            return;
        }

        var challenge = message.HasTrailingParameter
            ? message.TrailingParameter ?? string.Empty
            : message.Parameters.Count == 0 ? string.Empty : message.Parameters[0];
        ReadOnlyMemory<byte> response;
        try
        {
            response = challenge == "+"
                ? await _activeSaslMechanism.CreateInitialResponseAsync(_activeCredential, connectionCts.Token).ConfigureAwait(false)
                : await _activeSaslMechanism.CreateChallengeResponseAsync(_activeCredential, Convert.FromBase64String(challenge), connectionCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (connectionCts.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            await FailAuthenticationAsync(message, "The SASL mechanism could not produce a response.", epoch, connectionCts, _options.SaslPolicy == SaslAuthenticationPolicy.Required).ConfigureAwait(false);
            return;
        }

        try
        {
            var encoded = Convert.ToBase64String(response.Span);
            const int chunkSize = 400;
            if (encoded.Length == 0)
            {
                await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("AUTHENTICATE", ["+"]), connectionCts.Token, epoch).ConfigureAwait(false);
            }
            else
            {
                for (var offset = 0; offset < encoded.Length; offset += chunkSize)
                {
                    var count = Math.Min(chunkSize, encoded.Length - offset);
                    await QueueOutboundAsync(
                        new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("AUTHENTICATE", [encoded.Substring(offset, count)]),
                        connectionCts.Token,
                        epoch).ConfigureAwait(false);
                }

                if (encoded.Length % chunkSize == 0)
                {
                    await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("AUTHENTICATE", ["+"]), connectionCts.Token, epoch).ConfigureAwait(false);
                }
            }

            _saslResponseSent = true;
        }
        finally
        {
            if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(response, out var segment) && segment.Array is not null)
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(segment.Array.AsSpan(segment.Offset, segment.Count));
            }

            DisposeActiveCredential();
        }
    }

    private async Task CompleteSaslAsync(IrcMessage message, ConnectionEpoch epoch, CancellationTokenSource connectionCts)
    {
        if (_authenticationState != SaslAuthenticationState.Negotiating)
        {
            return;
        }

        await SetAuthenticationStateAsync(message, SaslAuthenticationState.Succeeded, _authenticationMechanism, null, epoch).ConfigureAwait(false);
        DisposeActiveCredential();
        await CompleteCapabilityAndBeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
    }

    private async Task FailAuthenticationAsync(
        IrcMessage message,
        string detail,
        ConnectionEpoch epoch,
        CancellationTokenSource connectionCts,
        bool fatal)
    {
        await SetAuthenticationStateAsync(message, SaslAuthenticationState.Failed, _authenticationMechanism, detail, epoch).ConfigureAwait(false);
        DisposeActiveCredential();
        if (fatal)
        {
            _registration = RegistrationState.Failed;
            _lastFailure = new ConnectionFailure(ConnectionFailureKind.RegistrationRejected, detail, IsTransient: false);
            SetState(ServerSessionState.Failed);
            connectionCts.Cancel();
        }
        else
        {
            await CompleteCapabilityAndBeginRegistrationAsync(epoch, connectionCts.Token).ConfigureAwait(false);
        }
    }

    private async Task CompleteCapabilityAndBeginRegistrationAsync(ConnectionEpoch epoch, CancellationToken cancellationToken)
    {
        CapNegotiationResult result;
        lock (_gate)
        {
            result = _capabilities.Complete();
        }
        foreach (var command in result.Commands)
        {
            await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
        }

        if (result.Snapshot.NegotiationState == CapNegotiationState.Ended)
        {
            await BeginRegistrationAsync(epoch, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task BeginRegistrationAsync(ConnectionEpoch epoch, CancellationToken cancellationToken)
    {
        if (!IsCurrentEpoch(epoch) || _registrationCommandsQueued || _registration == RegistrationState.Registered)
        {
            return;
        }

        _registrationCommandsQueued = true;
        _registration = RegistrationState.Registering;
        SetState(ServerSessionState.Registering);
        var builder = new IrcCommandBuilder(_options.MaximumOutboundLineBytes);
        await QueueOutboundAsync(builder.Build("NICK", [_stateStore.Nickname]), cancellationToken, epoch).ConfigureAwait(false);
        await QueueOutboundAsync(builder.Build("USER", [_options.Username, "0", "*"], _options.RealName), cancellationToken, epoch).ConfigureAwait(false);
    }

    private async Task SetAuthenticationStateAsync(
        IrcMessage message,
        SaslAuthenticationState state,
        string? mechanism,
        string? detail,
        ConnectionEpoch epoch,
        DateTimeOffset? receivedAt = null)
    {
        SaslAuthenticationState previous;
        lock (_gate)
        {
            previous = _authenticationState;
            _authenticationState = state;
            _authenticationMechanism = mechanism ?? _authenticationMechanism;
            _authenticationFailure = state == SaslAuthenticationState.Failed ? detail : null;
        }

        if (previous != state || detail is not null)
        {
            await PublishSemanticAsync(new IrcSaslStateChangedEvent(message, previous, state, mechanism ?? _authenticationMechanism, detail), epoch, receivedAt).ConfigureAwait(false);
        }
    }

    private async Task PublishCapabilityChangesAsync(
        IrcMessage message,
        CapabilitySnapshot before,
        CapabilitySnapshot after,
        ConnectionEpoch epoch,
        DateTimeOffset receivedAt)
    {
        var available = after.Available.Keys.Except(before.Available.Keys, StringComparer.Ordinal).ToArray();
        var removed = before.Available.Keys.Except(after.Available.Keys, StringComparer.Ordinal).ToArray();
        var enabled = after.Enabled.Except(before.Enabled, StringComparer.Ordinal).ToArray();
        var disabled = before.Enabled.Except(after.Enabled, StringComparer.Ordinal).ToArray();
        var rejected = after.Rejected.Except(before.Rejected, StringComparer.Ordinal).ToArray();
        foreach (var change in new[]
        {
            (IrcCapabilityChangeKind.Available, available),
            (IrcCapabilityChangeKind.Removed, removed),
            (IrcCapabilityChangeKind.Enabled, enabled),
            (IrcCapabilityChangeKind.Disabled, disabled),
            (IrcCapabilityChangeKind.Rejected, rejected)
        })
        {
            if (change.Item2.Length > 0)
            {
                await PublishSemanticAsync(new IrcCapabilityChangedEvent(message, change.Item1, change.Item2), epoch, receivedAt).ConfigureAwait(false);
            }
        }
    }

    private static ISaslMechanism? SelectSaslMechanism(CapabilitySnapshot capabilities, IReadOnlyList<ISaslMechanism> mechanisms)
    {
        var advertised = capabilities.Available.TryGetValue(IrcCapabilityCatalog.Sasl, out var sasl) && !string.IsNullOrWhiteSpace(sasl.Value)
            ? sasl.Value!.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        return mechanisms.FirstOrDefault(mechanism => advertised.Length == 0 || advertised.Contains(mechanism.Name, StringComparer.OrdinalIgnoreCase));
    }

    private static string? FindCapSubcommand(IrcMessage message)
    {
        foreach (var parameter in message.MiddleParameters)
        {
            var value = parameter.ToUpperInvariant();
            if (value is "LS" or "ACK" or "NAK" or "NEW" or "DEL" or "END")
            {
                return value;
            }
        }

        return null;
    }

    private static bool HasMoreCapabilityListing(IrcMessage message)
    {
        var subcommandIndex = -1;
        for (var index = 0; index < message.MiddleParameters.Count; index++)
        {
            if (message.MiddleParameters[index].Equals("LS", StringComparison.OrdinalIgnoreCase))
            {
                subcommandIndex = index;
                break;
            }
        }

        return subcommandIndex >= 0 && subcommandIndex + 1 < message.MiddleParameters.Count && message.MiddleParameters[subcommandIndex + 1] == "*";
    }

    private static bool IsSaslSuccess(int numeric) => numeric is 900 or 903 or 907;

    private static bool IsSaslFailure(int numeric) => numeric is 904 or 905 or 906 or 908;

    private static bool IsQuit(string line)
    {
        var trimmed = line.TrimStart();
        var separator = trimmed.IndexOfAny([' ', '\t']);
        var command = separator < 0 ? trimmed : trimmed[..separator];
        return string.Equals(command, "QUIT", StringComparison.OrdinalIgnoreCase);
    }

    private void DisposeActiveCredential()
    {
        lock (_gate)
        {
            DisposeActiveCredentialUnsafe();
        }
    }

    private void DisposeActiveCredentialUnsafe()
    {
        _activeCredential?.Dispose();
        _activeCredential = null;
        _activeSaslMechanism = null;
    }

    private void InvalidateConnectionState(ConnectionEpoch epoch)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeEpoch, epoch))
            {
                return;
            }

            epoch.IsActive = false;
            _activeEpoch = null;
            _stateStore.SetGeneration(_connectionGeneration);
            _resynchronizationRequested.Clear();
            _historyStates.Clear();
            _readStateSnapshotBuilder = null;
            _pendingReadStateUpdates.Clear();
            _readStateSnapshotSeen = false;
            _readStateSnapshotRequested = false;
            _capabilities.Reset();
            _isupport.Reset();
            _identityDetector.Reset();
            if (_options.ManualNetworkName is not null || _options.ManualIrcd.HasValue)
            {
                _identityDetector.SetManualOverride(_options.ManualNetworkName, _options.ManualIrcd);
            }

            _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, _options.Profiles, _identityDetector.Snapshot, _options.MaximumChathistoryRequestSize);
            var preserveTerminalFailure = _state == ServerSessionState.Failed || _registration == RegistrationState.Failed;
            var preserveAuthenticationFailure = _authenticationState == SaslAuthenticationState.Failed;
            if (!preserveTerminalFailure)
            {
                _registration = RegistrationState.NotStarted;
            }
            _registrationCommandsQueued = false;
            if (!preserveAuthenticationFailure)
            {
                _authenticationState = _options.SaslPolicy == SaslAuthenticationPolicy.Disabled
                    ? SaslAuthenticationState.Disabled
                    : SaslAuthenticationState.WaitingForCapability;
                _authenticationMechanism = null;
                _authenticationFailure = null;
            }
            _saslResponseSent = false;
            DisposeActiveCredentialUnsafe();

            if (_nativeResumeAttempt is { ConnectionGeneration: var nativeGeneration } nativeAttempt
                && nativeGeneration == epoch.Generation)
            {
                FailNativeResumeAttemptUnsafe(
                    nativeAttempt,
                    NexIrcResumeOutcome.Failed,
                    "The IRC transport ended before native replay completed.",
                    fallbackSafe: false);
            }

            _acceptedNativeResumeBatches.Clear();
        }

        CompleteHistoryRequest(
            ChathistoryRequestCompletion.Disconnected,
            "The IRC connection ended before the history batch completed.",
            expectedGeneration: epoch.Generation);
    }

    private bool IsCurrentEpoch(ConnectionEpoch epoch)
    {
        lock (_gate)
        {
            return IsCurrentEpochUnsafe(epoch);
        }
    }

    private bool IsCurrentEpochUnsafe(ConnectionEpoch epoch) => ReferenceEquals(_activeEpoch, epoch) && epoch.IsActive;

    private async Task HandleNicknameFailureAsync(ConnectionEpoch epoch, CancellationTokenSource connectionCts)
    {
        if (_nicknameCandidateIndex + 1 < _nicknameCandidates.Length)
        {
            _nicknameCandidateIndex++;
            var nickname = _nicknameCandidates[_nicknameCandidateIndex];
            _stateStore.SetNickname(nickname);
            await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("NICK", [nickname]), connectionCts.Token, epoch).ConfigureAwait(false);
            return;
        }

        _registration = RegistrationState.Failed;
        _lastFailure = new ConnectionFailure(ConnectionFailureKind.RegistrationRejected, "The server rejected the configured nickname and no alternate nickname is available.", IsTransient: false);
        SetState(ServerSessionState.Failed);
        _ = epoch;
        connectionCts.Cancel();
    }

    private async Task QueueDesiredChannelsAsync(ConnectionEpoch epoch, CancellationToken cancellationToken)
    {
        string[] desiredChannels;
        lock (_gate)
        {
            desiredChannels = _stateStore.DesiredChannels
                .Where(_stateStore.ShouldRequestJoin)
                .ToArray();
            foreach (var channel in desiredChannels)
            {
                _stateStore.MarkChannelJoining(channel);
            }
        }

        foreach (var channel in desiredChannels)
        {
            await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("JOIN", [channel]), cancellationToken, epoch).ConfigureAwait(false);
        }
    }

    private async Task QueueChannelResynchronizationAsync(string channel, ConnectionEpoch epoch, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_resynchronizationRequested.Add(NormalizeName(channel, _features.CaseMapping)))
            {
                return;
            }

            _stateStore.MarkChannelSynchronizing(channel);
            _stateStore.BeginNamesRequest(channel);
        }

        var builder = new IrcCommandBuilder(_options.MaximumOutboundLineBytes);
        await QueueOutboundAsync(builder.Build("NAMES", [channel]), cancellationToken, epoch).ConfigureAwait(false);
        await QueueOutboundAsync(builder.Build("TOPIC", [channel]), cancellationToken, epoch).ConfigureAwait(false);
        await QueueOutboundAsync(builder.Build("MODE", [channel]), cancellationToken, epoch).ConfigureAwait(false);
        await QueueOutboundAsync(builder.Build("WHO", [channel]), cancellationToken, epoch).ConfigureAwait(false);
    }

    private void ObserveHistoryBatch(IrcMessage message, ConnectionEpoch epoch)
    {
        var token = message.Parameters.Count == 0 ? null : message.Parameters[0];
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
        {
            return;
        }

        var batchId = token[1..];
        if (batchId.Length == 0)
        {
            return;
        }

        if (token[0] == '+')
        {
            string type;
            IReadOnlyList<string> parameters;
            lock (_gate)
            {
                if (!_stateStore.TryGetActiveBatch(batchId, out type, out parameters))
                {
                    return;
                }
            }

            if (!IsHistoryBatchType(type))
            {
                return;
            }

            var target = parameters.Count == 0 ? null : parameters[0];
            lock (_gate)
            {
                if (_activeHistoryRequest is not { ConnectionGeneration: var generation } pending
                    || generation != epoch.Generation)
                {
                    return;
                }

                var isDiscovery = pending.Request.Operation == ChathistoryOperation.Targets;
                var expectedType = isDiscovery ? "draft/chathistory-targets" : "chathistory";
                if (!string.Equals(type, expectedType, StringComparison.OrdinalIgnoreCase)
                    && !(isDiscovery && string.Equals(type, "chathistory-targets", StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                if (isDiscovery)
                {
                    pending.EndMarker = message.HasChathistoryEnd;
                    pending.BatchType = type;
                    pending.BatchTarget = target;
                    pending.BatchId = batchId;
                    _acceptedHistoryBatches.Add(batchId);

                    return;
                }

                if (!HistoryTargetMatches(pending.Request.Target!, target))
                {
                    // A session may receive an unrelated unsolicited
                    // chathistory batch while one request is active. Without
                    // a label it cannot complete or fail our request; its
                    // children remain fenced and the owned request times out
                    // if the matching batch never arrives.
                    return;
                }
                else
                {
                    pending.BatchId = batchId;
                    pending.EndMarker = message.HasChathistoryEnd;
                    pending.BatchType = type;
                    pending.BatchTarget = target;
                    _acceptedHistoryBatches.Add(batchId);
                }
            }

            return;
        }

        if (token[0] == '-')
        {
            var shouldComplete = false;
            var exhausted = false;
            lock (_gate)
            {
                var accepted = _acceptedHistoryBatches.Remove(batchId);
                var activePending = _activeHistoryRequest;
                if (accepted && activePending is { ConnectionGeneration: var generation } pending && generation == epoch.Generation && string.Equals(pending.BatchId, batchId, StringComparison.Ordinal))
                {
                    shouldComplete = true;
                    exhausted = pending.EndMarker;
                }
            }

            if (shouldComplete)
            {
                CompleteHistoryRequest(ChathistoryRequestCompletion.Succeeded, null, exhausted);
            }
        }
    }

    private async Task HandleNexIrcReadStateMessageAsync(IrcMessage message, ConnectionEpoch epoch)
    {
        CapabilitySnapshot capabilities;
        lock (_gate)
        {
            capabilities = _capabilities.Snapshot;
        }
        if (!NexIrcReadStateProtocol.IsSupported(capabilities)) return;

        var parameters = message.Parameters;
        var subcommand = parameters.Count > 1 ? parameters[1].ToUpperInvariant() : string.Empty;
        if (subcommand == "BEGIN")
        {
            if (parameters.Count == 4
                && int.TryParse(parameters[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var epochValue)
                && epochValue > 0
                && int.TryParse(parameters[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count)
                && count is >= 0 and <= NexIrcReadStateProtocol.MaximumEntries)
            {
                _readStateSnapshotBuilder = new ReadStateSnapshotBuilder(epochValue, count);
            }
            else
            {
                _readStateSnapshotBuilder = null;
                _readStateSnapshotRequested = false;
                _readStateSnapshotSeen = true;
                await FlushPendingReadStateUpdatesAsync(message, epoch).ConfigureAwait(false);
            }
            return;
        }

        if (subcommand == "ENTRY")
        {
            if (parameters.Count == 8
                && string.Equals(parameters[2], "READ", StringComparison.OrdinalIgnoreCase)
                && NexIrcReadStateProtocol.TryCreateMarker(parameters, 3, out var marker)
                && marker is not null
                && _readStateSnapshotBuilder is { } builder
                && builder.Markers.Count < builder.ExpectedCount
                && marker.SessionEpoch <= builder.SessionEpoch
                && builder.Markers.TryAdd(marker.ConversationKey, marker))
            {
                return;
            }
            _readStateSnapshotBuilder = null;
            _readStateSnapshotRequested = false;
            _readStateSnapshotSeen = true;
            await FlushPendingReadStateUpdatesAsync(message, epoch).ConfigureAwait(false);
            return;
        }

        if (subcommand == "END")
        {
            var builder = _readStateSnapshotBuilder;
            _readStateSnapshotBuilder = null;
            _readStateSnapshotRequested = false;
            if (parameters.Count == 3
                && builder is not null
                && int.TryParse(parameters[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var endEpoch)
                && endEpoch == builder.SessionEpoch
                && builder.Markers.Count == builder.ExpectedCount)
            {
                var snapshot = new NexIrcReadStateSnapshot(builder.SessionEpoch,
                    builder.Markers.Values.OrderBy(static item => item.ConversationKey, StringComparer.Ordinal).ToArray());
                await PublishSemanticAsync(new IrcNexIrcReadStateEvent(message, Snapshot: snapshot), epoch).ConfigureAwait(false);
            }
            _readStateSnapshotSeen = true;
            await FlushPendingReadStateUpdatesAsync(message, epoch).ConfigureAwait(false);
            return;
        }

        if (subcommand == "UPDATE")
        {
            if (parameters.Count == 8
                && string.Equals(parameters[2], "READ", StringComparison.OrdinalIgnoreCase)
                && NexIrcReadStateProtocol.TryCreateMarker(parameters, 3, out var marker)
                && marker is not null)
            {
                if (_readStateSnapshotBuilder is not null || !_readStateSnapshotSeen || _readStateSnapshotRequested)
                {
                    BufferReadStateUpdate(marker);
                    return;
                }
                await PublishSemanticAsync(new IrcNexIrcReadStateEvent(message, Marker: marker), epoch).ConfigureAwait(false);
            }
            return;
        }

        if (subcommand == "ACK" && parameters.Count == 3)
        {
            if (string.Equals(parameters[2], "UNAVAILABLE", StringComparison.OrdinalIgnoreCase)
                && (_readStateSnapshotRequested || !_readStateSnapshotSeen))
            {
                _readStateSnapshotRequested = false;
                _readStateSnapshotSeen = true;
                await FlushPendingReadStateUpdatesAsync(message, epoch).ConfigureAwait(false);
            }
            await PublishSemanticAsync(new IrcNexIrcReadStateEvent(message, Status: parameters[2]), epoch).ConfigureAwait(false);
        }
    }

    private void BufferReadStateUpdate(NexIrcReadMarker marker)
    {
        if (_pendingReadStateUpdates.TryGetValue(marker.ConversationKey, out var current))
        {
            if (IsNewerReadStateMarker(marker, current))
                _pendingReadStateUpdates[marker.ConversationKey] = marker;
            return;
        }

        if (_pendingReadStateUpdates.Count < NexIrcReadStateProtocol.MaximumEntries)
            _pendingReadStateUpdates.Add(marker.ConversationKey, marker);
    }

    private async Task FlushPendingReadStateUpdatesAsync(IrcMessage source, ConnectionEpoch epoch)
    {
        var updates = _pendingReadStateUpdates.Values.OrderBy(static marker => marker.ConversationKey, StringComparer.Ordinal).ToArray();
        _pendingReadStateUpdates.Clear();
        foreach (var marker in updates)
            await PublishSemanticAsync(new IrcNexIrcReadStateEvent(source, Marker: marker), epoch).ConfigureAwait(false);
    }

    private static bool IsNewerReadStateMarker(NexIrcReadMarker candidate, NexIrcReadMarker current)
    {
        if (candidate.Revision <= current.Revision || candidate.SessionEpoch < current.SessionEpoch
            || candidate.SessionEpoch == current.SessionEpoch && candidate.Sequence < current.Sequence)
            return false;
        return candidate.SessionEpoch > current.SessionEpoch || candidate.Sequence > current.Sequence
            || string.Equals(candidate.MessageId, current.MessageId, StringComparison.Ordinal);
    }

    private async Task HandleNativeResumeMessageAsync(
        IrcMessage message,
        ConnectionEpoch epoch,
        CancellationTokenSource connectionCts)
    {
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var subcommand = message.Parameters.Count == 0
            ? string.Empty
            : message.Parameters[0].ToUpperInvariant();
        if (subcommand == NexIrcResumeProtocol.PairSubcommand)
        {
            var pairResponse = message.Parameters.Count >= 2 ? message.Parameters[1].ToUpperInvariant() : string.Empty;
            if (pairResponse == "CREATED" && message.Parameters.Count >= 5)
            {
                TaskCompletionSource<NexIrcPairingAuthorization>? completion;
                lock (_gate)
                {
                    completion = _pairingAuthorizationAttempt;
                }

                if (completion is not null
                    && long.TryParse(message.Parameters[3], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var expiry)
                    && NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[2])
                    && NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[4]))
                {
                    var material = NexIrcResumeProtocol.CreatePairingMaterial(message.Parameters[2], message.Parameters[4]);
                    completion.TrySetResult(new NexIrcPairingAuthorization(material, DateTimeOffset.FromUnixTimeSeconds(expiry)));
                }
                else
                {
                    completion?.TrySetException(new InvalidOperationException("The server returned a malformed pairing authorization."));
                }

                return;
            }

            if (pairResponse == "REVOKED")
            {
                lock (_gate)
                {
                    _pairingRevocationAttempt?.TrySetResult(true);
                }
                return;
            }

            if (pairResponse == NexIrcResumeProtocol.PairCustodySubcommand
                && message.Parameters.Count >= 4
                && string.Equals(message.Parameters[2], "OK", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(message.Parameters[3], "N", out var completedAttachmentId))
            {
                await HandlePairingCustodyAcceptedAsync(completedAttachmentId).ConfigureAwait(false);
                return;
            }

            if (pairResponse == "REJECT")
            {
                var reasonText = message.Parameters.Count >= 3 ? message.Parameters[2] : "MALFORMED";
                NativeResumeAttempt? pairingAttempt;
                TaskCompletionSource<NexIrcPairingAuthorization>? completion;
                TaskCompletionSource<bool>? revocation;
                lock (_gate)
                {
                    pairingAttempt = _nativeResumeAttempt is { IsPairingRequest: true } active ? active : null;
                    completion = _pairingAuthorizationAttempt;
                    revocation = _pairingRevocationAttempt;
                }

                if (pairingAttempt is not null)
                {
                    CompleteNativeResumeRejected(
                        ParseNativeResumeRejection(reasonText),
                        "The server rejected the pairing request.",
                        epoch.Generation);
                }
                else
                {
                    if (revocation is not null)
                    {
                        revocation.TrySetResult(false);
                    }
                    else
                    {
                        completion?.TrySetException(new InvalidOperationException($"The server rejected pairing authorization ({reasonText})."));
                    }
                }

                return;
            }

            FailNativeResumeAttempt(epoch.Generation, NexIrcResumeOutcome.Failed, "The server sent a malformed pairing response.", fallbackSafe: false, reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        if (subcommand == NexIrcResumeProtocol.SessionSubcommand)
        {
            if (message.Parameters.Count >= 3
                && string.Equals(message.Parameters[1], NexIrcResumeProtocol.SessionRotateSubcommand, StringComparison.OrdinalIgnoreCase))
            {
                await HandleNativeSessionRotationAsync(message, epoch, connectionCts).ConfigureAwait(false);
            }
            else if (message.Parameters.Count >= 4
                && string.Equals(message.Parameters[1], NexIrcResumeProtocol.SessionAckSubcommand, StringComparison.OrdinalIgnoreCase)
                && string.Equals(message.Parameters[2], "OK", StringComparison.OrdinalIgnoreCase))
            {
                await HandleNativeSessionAcknowledgementAsync(message, epoch).ConfigureAwait(false);
            }
            else if (message.Parameters.Count >= 4
                && string.Equals(message.Parameters[1], "ATTACHMENT", StringComparison.OrdinalIgnoreCase))
            {
                await HandleNativeAttachmentAnnouncementAsync(message, epoch.Generation).ConfigureAwait(false);
            }
            else if (message.Parameters.Count >= 3
                && string.Equals(message.Parameters[1], "KEY", StringComparison.OrdinalIgnoreCase))
            {
                await HandleNativeSessionCredentialAnnouncementAsync(message, epoch.Generation).ConfigureAwait(false);
            }
            else
            {
                await HandleNativeSessionAnnouncementAsync(message, epoch.Generation).ConfigureAwait(false);
            }

            return;
        }

        if (subcommand == NexIrcResumeProtocol.AttachSubcommand)
        {
            var attachResponse = message.Parameters.Count >= 2 ? message.Parameters[1].ToUpperInvariant() : string.Empty;
            if (attachResponse == "ACCEPT")
            {
                await HandleNativeAttachmentAcceptedAsync(message, epoch.Generation, connectionCts).ConfigureAwait(false);
            }
            else if (attachResponse == "SYNC"
                && message.Parameters.Count >= 4
                && string.Equals(message.Parameters[2], "UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
            {
                await HandleNativeReplayUnavailableAsync(message.Parameters[3], epoch.Generation).ConfigureAwait(false);
            }
            else if (attachResponse == "REJECT")
            {
                CompleteNativeResumeRejected(
                    ParseNativeResumeRejection(message.Parameters.Count >= 3 ? message.Parameters[2] : string.Empty),
                    "The server rejected the native attachment request.",
                    epoch.Generation);
            }
            else
            {
                FailNativeResumeAttempt(
                    epoch.Generation,
                    NexIrcResumeOutcome.Failed,
                    "The server sent a malformed nexIRC attachment response.",
                    fallbackSafe: false,
                    reason: NexIrcResumeRejectionReason.Malformed);
            }

            return;
        }

        if (subcommand != NexIrcResumeProtocol.ResumeSubcommand || message.Parameters.Count < 2)
        {
            FailNativeResumeAttempt(
                epoch.Generation,
                NexIrcResumeOutcome.Failed,
                "The server sent a malformed nexIRC resume response.",
                fallbackSafe: false,
                 reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        var response = message.Parameters[1].ToUpperInvariant();
        switch (response)
        {
            case "ACCEPT" when message.Parameters.Count >= 3:
                HandleNativeResumeAccepted(message.Parameters[2], epoch.Generation);
                break;
            case "REJECT":
                await HandleNativeResumeRejectedAsync(
                    message.Parameters.Count >= 3 ? message.Parameters[2] : string.Empty,
                    epoch.Generation).ConfigureAwait(false);
                break;
            case "UNAVAILABLE" when message.Parameters.Count >= 3:
                await HandleNativeReplayUnavailableAsync(message.Parameters[2], epoch.Generation).ConfigureAwait(false);
                break;
            case "UNSUPPORTED":
            case "NEW":
                CompleteNativeResumeRejected(
                    NexIrcResumeRejectionReason.Unsupported,
                    "The server requires a new session instead of resuming the prior logical session.",
                    epoch.Generation);
                break;
            case "COMPLETE" when message.Parameters.Count >= 3:
                await HandleNativeResumeCompleteAsync(message.Parameters[2], epoch.Generation).ConfigureAwait(false);
                break;
            default:
                FailNativeResumeAttempt(
                    epoch.Generation,
                    NexIrcResumeOutcome.Failed,
                    "The server sent a malformed nexIRC resume response.",
                    fallbackSafe: false,
                     reason: NexIrcResumeRejectionReason.Malformed);
                break;
        }

        _ = connectionCts;
    }

    private async Task HandlePairingCustodyAcceptedAsync(Guid attachmentId)
    {
        NexIrcResumeSession? current;
        lock (_gate)
        {
            if (_nativeResumeSession?.AttachmentId != attachmentId
                || _pairingRecoveryCredential is null
                || !_pairingRecoveryCredentialsDurable)
            {
                return;
            }

            current = _nativeResumeSession;
            _pairingRecoveryCredential = null;
            _pairingRecoveryBoundary = null;
            _pairingRecoveryCredentialsDurable = false;
        }

        if (current is not null && !await PersistNativeResumeStateAsync(current).ConfigureAwait(false))
        {
            // The server has confirmed custody. Keep the already committed
            // attachment credentials usable even if retiring the local receipt
            // record needs another normal state write.
            _continuity.RecordDiagnostic(_connectionGeneration, ContinuityDiagnosticKind.RecoveryRequestFailed, "Protected pairing receipt cleanup could not be durably committed after server custody confirmation.");
        }
    }

    private async Task HandleNativeAttachmentAcceptedAsync(
        IrcMessage message,
        int generation,
        CancellationTokenSource connectionCts)
    {
        if (message.Parameters.Count < 7
            || !Guid.TryParseExact(message.Parameters[2], "N", out var attachmentId)
            || attachmentId == Guid.Empty
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[3])
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[4])
            || !int.TryParse(message.Parameters[5], out var tokenGeneration)
            || tokenGeneration < 1
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[6]))
        {
            FailNativeResumeAttempt(
                generation,
                NexIrcResumeOutcome.Failed,
                "The server sent a malformed nexIRC attachment credential.",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        NexIrcResumeSession? attached = null;
        var requestMismatch = false;
        var pairingHandoffReceived = false;
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration, IsAttachmentRequest: true } attempt
                || attemptGeneration != generation
                || !string.Equals(message.Parameters[4], attempt.RequestedBoundary, StringComparison.Ordinal)
                || _nativeResumeSession is not { SessionCredential: { } })
            {
                requestMismatch = true;
            }
            else
            {
                pairingHandoffReceived = attempt.IsPairingRequest && _pairingRecoveryCredential is not null;
                if (pairingHandoffReceived)
                {
                    _pairingRecoveryCredentialsDurable = true;
                }

                attached = new NexIrcResumeSession(
                    message.Parameters[3],
                    message.Parameters[4],
                    tokenGeneration,
                    attachmentId: attachmentId,
                    sessionCredential: message.Parameters[6]);
            }
        }

        if (requestMismatch || attached is null)
        {
            FailNativeResumeAttempt(
                generation,
                NexIrcResumeOutcome.Failed,
                "The native attachment acceptance did not match the outstanding request.",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        if (!await PersistNativeResumeStateAsync(attached).ConfigureAwait(false))
        {
            if (pairingHandoffReceived)
            {
                lock (_gate)
                {
                    _pairingRecoveryCredentialsDurable = false;
                }
            }

            FailNativeResumeAttempt(
                generation,
                NexIrcResumeOutcome.Failed,
                "The new attachment credential could not be durably committed.",
                fallbackSafe: false);
            connectionCts.Cancel();
            return;
        }

        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration, IsAttachmentRequest: true } attempt
                || attemptGeneration != generation)
            {
                return;
            }

            _nativeResumeSession = attached;
            _restoredResumeActive = false;
            _freshSessionAfterRestoredResume = null;
            attempt.AttachmentCreated = true;
            attempt.ReplayAccepted = true;
            attempt.AcceptedBoundary = attached.AuthoritativeBoundary;
            attempt.AttachmentAuthorityRecovered = true;
        }

        if (pairingHandoffReceived)
        {
            await SendPairingCustodyAcknowledgementAsync(attached, generation, connectionCts.Token).ConfigureAwait(false);
        }
    }

    private async Task SendPairingCustodyAcknowledgementAsync(
        NexIrcResumeSession session,
        int generation,
        CancellationToken cancellationToken)
    {
        string? recoveryCredential;
        ConnectionEpoch? epoch;
        lock (_gate)
        {
            recoveryCredential = _pairingRecoveryCredentialsDurable ? _pairingRecoveryCredential : null;
            epoch = _activeEpoch is { IsActive: true } current && current.Generation == generation ? current : null;
        }

        if (recoveryCredential is null || epoch is null || session.AttachmentId is not { } attachmentId)
        {
            return;
        }

        var command = new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
            NexIrcResumeProtocol.Command,
            [NexIrcResumeProtocol.PairSubcommand, NexIrcResumeProtocol.PairCustodySubcommand, recoveryCredential, attachmentId.ToString("N")]);
        await QueueOutboundAsync(command, cancellationToken, epoch).ConfigureAwait(false);
    }

    private async Task HandleNativeAttachmentAnnouncementAsync(IrcMessage message, int generation)
    {
        if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
            || !_options.Endpoint.UseTls
            || !Guid.TryParseExact(message.Parameters[2], "N", out var attachmentId)
            || attachmentId == Guid.Empty
            || !int.TryParse(message.Parameters[3], out var attachmentGeneration)
            || attachmentGeneration < 1)
        {
            return;
        }

        NexIrcResumeSession? updated = null;
        var persist = false;
        lock (_gate)
        {
            if (_restoredResumeActive && _freshSessionAfterRestoredResume is { } fallback)
            {
                _freshSessionAfterRestoredResume = updated = fallback.WithAttachment(attachmentId);
            }
            else if (_nativeResumeSession is { } current)
            {
                _nativeResumeSession = updated = current.WithAttachment(attachmentId);
                persist = true;
            }
        }

        if (persist && updated is not null)
        {
            await PersistNativeResumeStateAsync(updated).ConfigureAwait(false);
        }

        _ = generation;
        _ = attachmentGeneration;
    }

    private async Task HandleNativeSessionCredentialAnnouncementAsync(IrcMessage message, int generation)
    {
        if (!NexIrcResumeProtocol.AreAttachmentsSupported(_capabilities.Snapshot)
            || !_options.Endpoint.UseTls
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[2]))
        {
            return;
        }

        NexIrcResumeSession? updated = null;
        var persist = false;
        lock (_gate)
        {
            if (_restoredResumeActive && _freshSessionAfterRestoredResume is { } fallback)
            {
                _freshSessionAfterRestoredResume = updated = fallback.WithSessionCredential(message.Parameters[2]);
            }
            else if (_nativeResumeSession is { } current)
            {
                _nativeResumeSession = updated = current.WithSessionCredential(message.Parameters[2]);
                persist = true;
            }
        }

        if (persist && updated is not null)
        {
            await PersistNativeResumeStateAsync(updated).ConfigureAwait(false);
        }

        _ = generation;
    }

    private async Task HandleNativeSessionAnnouncementAsync(IrcMessage message, int generation)
    {
        if (!NexIrcResumeProtocol.IsSupported(_capabilities.Snapshot)
            || !NexIrcResumeProtocol.TryReadOpaqueParameters(message, 1, out var token, out var boundary))
        {
            return;
        }

        NexIrcResumeSession? announced = null;
        var persist = false;
        lock (_gate)
        {
            if (_nativeResumeSession is null || (_restoredResumeActive && !TryActivateRestoredResumeStateUnsafe()))
            {
                announced = new NexIrcResumeSession(token, boundary, generation);
                _nativeResumeSession = announced;
                _freshSessionAfterRestoredResume = null;
                _restoredResumeActive = false;
                _resumeStateCreatedAt = DateTimeOffset.UtcNow;
                persist = true;
                _continuity.RecordDiagnostic(
                    generation,
                    ContinuityDiagnosticKind.RegistrationCompleted,
                    $"NexIrcResume session established session={_nativeResumeSession.TokenFingerprint}; boundary={boundary}");
            }
            else if (_restoredResumeActive)
            {
                _freshSessionAfterRestoredResume = new NexIrcResumeSession(token, boundary, generation);
                _continuity.RecordDiagnostic(
                    generation,
                    ContinuityDiagnosticKind.RegistrationCompleted,
                    "A fresh server-issued session was retained as bounded fallback while protected native-resume state is attempted.");
            }
            else if (_nativeResumeAttempt is { ReplayAccepted: true })
            {
                _continuity.RecordDiagnostic(
                    generation,
                    ContinuityDiagnosticKind.RecoveryRequestCompleted,
                    "A duplicate nexIRC session announcement was ignored after replay acceptance; ROTATE carries replacements.");
            }
        }

        if (persist && announced is not null)
        {
            await PersistNativeResumeStateAsync(announced).ConfigureAwait(false);
        }
    }

    private void HandleNativeResumeAccepted(string boundary, int generation)
    {
        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(boundary))
        {
            FailNativeResumeAttempt(
                generation,
                NexIrcResumeOutcome.Failed,
                "The native resume acceptance boundary is malformed.",
                fallbackSafe: false,
                 reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation)
            {
                _continuity.RecordStaleCallback(generation, "A stale native resume acceptance was ignored.");
                return;
            }

            if (!string.Equals(boundary, attempt.RequestedBoundary, StringComparison.Ordinal))
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "The server accepted a different replay anchor than the client requested.",
                    fallbackSafe: false,
                     reason: NexIrcResumeRejectionReason.Malformed);
                return;
            }

            attempt.ReplayAccepted = true;
            attempt.AcceptedBoundary = boundary;
            attempt.AttachmentAuthorityRecovered = true;
        }
    }

    private async Task HandleNativeResumeRejectedAsync(string reason, int generation)
    {
        var rejection = ParseNativeResumeRejection(reason);
        var detail = string.IsNullOrWhiteSpace(reason)
            ? "The server rejected the native resume request."
            : $"The server rejected the native resume request: {reason}.";
        if (rejection is NexIrcResumeRejectionReason.UnknownToken
            or NexIrcResumeRejectionReason.ExpiredToken
            or NexIrcResumeRejectionReason.AccountMismatch
            or NexIrcResumeRejectionReason.SessionInvalidated
            or NexIrcResumeRejectionReason.ServerRestarted
            or NexIrcResumeRejectionReason.NewSessionRequired)
        {
            await ClearProtectedResumeStateAsync(clearInMemory: false).ConfigureAwait(false);
            NexIrcResumeSession? fallback = null;
            lock (_gate)
            {
                _loadedResumeState = null;
                if (_freshSessionAfterRestoredResume is { } fresh)
                {
                    fallback = fresh;
                    _nativeResumeSession = fresh;
                    _freshSessionAfterRestoredResume = null;
                    _restoredResumeActive = false;
                    _resumeStateCreatedAt = DateTimeOffset.UtcNow;
                }
                else
                {
                    _nativeResumeSession = null;
                    _restoredResumeActive = false;
                }
            }

            if (fallback is not null)
            {
                await PersistNativeResumeStateAsync(fallback).ConfigureAwait(false);
            }
        }

        // The resume caller treats completion as the postcondition for this
        // rejection. Finish clearing/adopting durable fallback state first so
        // it cannot observe a rejected result alongside the stale credential.
        CompleteNativeResumeRejected(rejection, detail, generation);
    }

    private async Task HandleNativeReplayUnavailableAsync(string reason, int generation)
    {
        var limitation = reason.ToUpperInvariant() switch
        {
            "BOUNDARY_TOO_OLD" => NexIrcSynchronizationLimitation.BoundaryBelowRetention,
            "TOO_LARGE" or "REPLAY_TOO_LARGE" => NexIrcSynchronizationLimitation.ReplayEventLimitExceeded,
            "BYTE_LIMIT_EXCEEDED" => NexIrcSynchronizationLimitation.ReplayByteLimitExceeded,
            "INVALID_BOUNDARY" => NexIrcSynchronizationLimitation.InvalidBoundary,
            _ => (NexIrcSynchronizationLimitation?)null
        };
        if (limitation is null)
        {
            FailNativeResumeAttempt(
                generation,
                NexIrcResumeOutcome.Failed,
                "The server reported an unknown native replay limitation.",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        TaskCompletionSource<NexIrcResumeExecutionResult>? completion = null;
        NexIrcResumeExecutionResult? result = null;
        NexIrcResumeSession? sessionToPersist = null;
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation)
            {
                _continuity.RecordStaleCallback(generation, "A stale native replay limitation was ignored.");
                return;
            }

            if (!attempt.AttachmentAuthorityRecovered || attempt.ActiveBatchIds.Count > 0)
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "The server reported unavailable replay before attachment authority was established or after replay began.",
                    fallbackSafe: false,
                    reason: NexIrcResumeRejectionReason.Malformed);
                return;
            }

            // The server has authenticated/bound this attachment, but it could
            // not serve its saved history boundary. Keep credentials and finish
            // this request as a separate synchronization outcome.
            attempt.ReplayAccepted = false;
            if (!attempt.IsPairingRequest && _pairingRecoveryCredentialsDurable)
            {
                _pairingRecoveryCredential = null;
                _pairingRecoveryBoundary = null;
                _pairingRecoveryCredentialsDurable = false;
                sessionToPersist = _nativeResumeSession;
            }

            result = new NexIrcResumeExecutionResult(
                NexIrcResumeOutcome.ReplayUnavailable,
                CapabilityNegotiated: true,
                RequestSent: attempt.RequestSent,
                ReplayAccepted: false,
                ReplayCompleted: false,
                ExactBoundaryRecovered: false,
                attempt.ReplayedEventCount,
                attempt.DuplicateEventsSuppressed,
                attempt.RequestedBoundary,
                FinalBoundary: null,
                $"Attachment authority recovered; native replay is unavailable ({limitation.Value}).",
                RejectionReason: null,
                FallbackSafe: true)
            {
                AttachmentCreated = attempt.AttachmentCreated,
                AttachmentAuthorityRecovered = true,
                SynchronizationLimitation = limitation
            };
            _nativeResumeAttempt = null;
            _acceptedNativeResumeBatches.Clear();
            completion = attempt.Completion;
        }

        if (sessionToPersist is not null)
        {
            await PersistNativeResumeStateAsync(sessionToPersist).ConfigureAwait(false);
        }

        _continuity.RecordDiagnostic(
            generation,
            ContinuityDiagnosticKind.RecoveryRequestCompleted,
            $"Attachment authority recovered; native synchronization requires fallback ({limitation.Value}).");
        completion?.TrySetResult(result!);
    }

    private static NexIrcResumeRejectionReason ParseNativeResumeRejection(string reason) => reason.ToUpperInvariant() switch
    {
        "UNKNOWN" or "UNKNOWN_TOKEN" => NexIrcResumeRejectionReason.UnknownToken,
        "EXPIRED" or "EXPIRED_TOKEN" => NexIrcResumeRejectionReason.ExpiredToken,
        "ACCOUNT" or "ACCOUNT_MISMATCH" => NexIrcResumeRejectionReason.AccountMismatch,
        "TOO_OLD" or "BOUNDARY_TOO_OLD" => NexIrcResumeRejectionReason.BoundaryTooOld,
        "INVALIDATED" or "SESSION_INVALIDATED" => NexIrcResumeRejectionReason.SessionInvalidated,
        "RESTARTED" or "SERVER_RESTARTED" => NexIrcResumeRejectionReason.ServerRestarted,
        "TOO_LARGE" or "REPLAY_TOO_LARGE" => NexIrcResumeRejectionReason.ReplayTooLarge,
        "RATE_LIMITED" => NexIrcResumeRejectionReason.RateLimited,
        "ATTACHMENT_LIMIT" => NexIrcResumeRejectionReason.AttachmentLimit,
        "AUTH_REQUIRED" => NexIrcResumeRejectionReason.AuthenticationRequired,
        "TEMPORARY_FAILURE" => NexIrcResumeRejectionReason.TemporaryFailure,
        "UNSUPPORTED" => NexIrcResumeRejectionReason.Unsupported,
        "NEW" or "NEW_SESSION" => NexIrcResumeRejectionReason.NewSessionRequired,
        _ => NexIrcResumeRejectionReason.Malformed
    };

    private async Task HandleNativeSessionRotationAsync(
        IrcMessage message,
        ConnectionEpoch epoch,
        CancellationTokenSource connectionCts)
    {
        if (message.Parameters.Count < 5
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[2])
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(message.Parameters[3])
            || !int.TryParse(message.Parameters[4], out var generation)
            || generation < 1)
        {
            FailNativeResumeAttempt(
                epoch.Generation,
                NexIrcResumeOutcome.Failed,
                "The server sent a malformed nexIRC token rotation.",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        var token = message.Parameters[2];
        var boundary = message.Parameters[3];
        NexIrcResumeSession? rotated = null;
        lock (_gate)
        {
            if (_nativeResumeSession is not { } current)
            {
                rotated = null;
            }
            else
            {
                var accepted = string.Equals(boundary, current.AuthoritativeBoundary, StringComparison.Ordinal)
                    && generation > current.EstablishedGeneration;
                if (accepted)
                {
                    rotated = current.Rotate(token, boundary, generation);
                }
            }
        }

        if (rotated is null)
        {
            FailNativeResumeAttempt(
                epoch.Generation,
                NexIrcResumeOutcome.Failed,
                "The server sent a token rotation that did not match the current durable boundary.",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        // Persist the old/current plus replacement/pending pair before either
        // mutating the in-memory authority or acknowledging. A crash before
        // this point leaves the old token as the only durable credential.
        if (!await PersistNativeResumeStateAsync(rotated).ConfigureAwait(false))
        {
            _continuity.RecordDiagnostic(
                epoch.Generation,
                ContinuityDiagnosticKind.RecoveryRequestFailed,
                "Token rotation was not acknowledged because the protected replacement could not be durably committed.");
            return;
        }

        lock (_gate)
        {
            if (_nativeResumeSession is not null
                && string.Equals(_nativeResumeSession.Token, rotated.PreviousToken, StringComparison.Ordinal)
                && _nativeResumeSession.EstablishedGeneration == rotated.PreviousGeneration)
            {
                _nativeResumeSession = rotated;
                _restoredResumeActive = false;
                _freshSessionAfterRestoredResume = null;
                _continuity.RecordDiagnostic(
                    epoch.Generation,
                    ContinuityDiagnosticKind.RecoveryRequestCompleted,
                    $"NexIrcResume token rotation accepted; session={_nativeResumeSession.TokenFingerprint}; generation={generation}");
            }
            else
            {
                return;
            }
        }

        // The acknowledgement only tells the server that it may retire the
        // old token. Losing the connection after this write but before ACK OK
        // leaves the protected old/pending pair recoverable on restart.
        await QueueOutboundAsync(
            new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build(
                NexIrcResumeProtocol.Command,
                [NexIrcResumeProtocol.SessionSubcommand, NexIrcResumeProtocol.SessionAckSubcommand, generation.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            connectionCts.Token,
            epoch).ConfigureAwait(false);
    }

    private async Task HandleNativeSessionAcknowledgementAsync(IrcMessage message, ConnectionEpoch epoch)
    {
        if (message.Parameters.Count < 4
            || !int.TryParse(message.Parameters[3], out var generation)
            || generation < 1)
        {
            return;
        }

        NexIrcResumeSession? acknowledged = null;
        lock (_gate)
        {
            if (_nativeResumeSession is { HasPendingRotation: true } current
                && current.EstablishedGeneration == generation)
            {
                acknowledged = current.MarkRotationAcknowledged();
            }
        }

        if (acknowledged is null)
        {
            return;
        }

        // If cleanup persistence fails, retain the pending pair in memory and
        // on disk. The server has already promoted the replacement, so the
        // pending token remains the safe restart credential.
        if (!await PersistNativeResumeStateAsync(acknowledged).ConfigureAwait(false))
        {
            _continuity.RecordDiagnostic(epoch.Generation, ContinuityDiagnosticKind.RecoveryRequestFailed, "ACK OK was received but local rotation cleanup could not be committed; the bounded protected overlap was retained.");
            return;
        }

        lock (_gate)
        {
            if (_nativeResumeSession is { HasPendingRotation: true } current
                && current.EstablishedGeneration == generation)
            {
                _nativeResumeSession = acknowledged;
            }
        }
    }

    private void CompleteNativeResumeRejected(
        NexIrcResumeRejectionReason reason,
        string detail,
        int generation)
    {
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation)
            {
                _continuity.RecordStaleCallback(generation, "A stale native resume rejection was ignored.");
                return;
            }

            FailNativeResumeAttemptUnsafe(
                attempt,
                NexIrcResumeOutcome.Rejected,
                detail,
                fallbackSafe: !attempt.ReplayAccepted,
                reason);
        }
    }

    private async Task HandleNativeResumeCompleteAsync(string boundary, int generation)
    {
        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(boundary))
        {
            FailNativeResumeAttempt(
                generation,
                NexIrcResumeOutcome.Failed,
                "The native resume completion boundary is malformed.",
                fallbackSafe: false,
                 reason: NexIrcResumeRejectionReason.Malformed);
            return;
        }

        NexIrcResumeSession? completedSession = null;
        NexIrcResumeExecutionResult? result = null;
        TaskCompletionSource<NexIrcResumeExecutionResult>? completion = null;
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation)
            {
                _continuity.RecordStaleCallback(generation, "A stale native replay completion was ignored.");
                return;
            }

            if (!attempt.ReplayAccepted
                || attempt.ActiveBatchIds.Count > 0
                || !string.Equals(boundary, attempt.CurrentBoundary, StringComparison.Ordinal))
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "The native replay completion did not match the accepted authoritative boundary.",
                    fallbackSafe: false,
                     reason: NexIrcResumeRejectionReason.Malformed);
                return;
            }

            completedSession = _nativeResumeSession = _nativeResumeSession?.Advance(boundary);
            if (!attempt.IsPairingRequest && _pairingRecoveryCredentialsDurable)
            {
                _pairingRecoveryCredential = null;
                _pairingRecoveryBoundary = null;
                _pairingRecoveryCredentialsDurable = false;
            }

            _restoredResumeActive = false;
            _freshSessionAfterRestoredResume = null;
            result = new NexIrcResumeExecutionResult(
                NexIrcResumeOutcome.Completed,
                CapabilityNegotiated: true,
                RequestSent: attempt.RequestSent,
                ReplayAccepted: true,
                ReplayCompleted: true,
                ExactBoundaryRecovered: true,
                attempt.ReplayedEventCount,
                attempt.DuplicateEventsSuppressed,
                attempt.RequestedBoundary,
                boundary,
                "Authoritative native replay completed through the server boundary.",
                null,
                FallbackSafe: false)
            {
                AttachmentCreated = attempt.AttachmentCreated,
                AttachmentAuthorityRecovered = true
            };
            _nativeResumeAttempt = null;
            _acceptedNativeResumeBatches.Clear();
            completion = attempt.Completion;
        }

        if (completedSession is not null)
        {
            await PersistNativeResumeStateAsync(completedSession).ConfigureAwait(false);
        }

        if (result is not null)
        {
            completion?.TrySetResult(result);
        }
    }

    private bool ValidateNativeReplayEnvelopeUnsafe(
        IrcMessage message,
        int generation,
        out bool duplicate)
    {
        duplicate = false;
        if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
            || attemptGeneration != generation)
        {
            return false;
        }

        var sequence = message.TagValues.TryGetValue(NexIrcResumeProtocol.ResumeSequenceTag, out var rawSequence)
            ? rawSequence
            : null;
        var previous = message.TagValues.TryGetValue(NexIrcResumeProtocol.ResumePreviousSequenceTag, out var rawPrevious)
            ? rawPrevious
            : null;
        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(sequence)
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(previous))
        {
            FailNativeResumeAttemptUnsafe(
                attempt,
                NexIrcResumeOutcome.Failed,
                "A native replay event did not carry safe authoritative sequence metadata.",
                fallbackSafe: false,
                     reason: NexIrcResumeRejectionReason.Malformed);
            return false;
        }

        var messageId = message.ServerMessageId;
        if (attempt.SequenceMessageIds.TryGetValue(sequence!, out var existingMessageId))
        {
            if (messageId is null || !string.Equals(existingMessageId, messageId, StringComparison.Ordinal))
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "A native replay sequence was reused for a different canonical event.",
                    fallbackSafe: false,
                     reason: NexIrcResumeRejectionReason.Malformed);
                return false;
            }

            attempt.DuplicateEventsSuppressed++;
            duplicate = true;
            return true;
        }

        if (string.Equals(sequence, attempt.CurrentBoundary, StringComparison.Ordinal))
        {
            if (messageId is null)
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "A replayed boundary event did not carry a canonical msgid for deduplication.",
                    fallbackSafe: false,
                 reason: NexIrcResumeRejectionReason.Malformed);
                return false;
            }

            if (_nativeLiveSequenceMessageIds.TryGetValue(sequence!, out var committedMessageId)
                && !string.Equals(committedMessageId, messageId, StringComparison.Ordinal))
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "A replayed boundary sequence conflicted with the committed canonical msgid.",
                    fallbackSafe: false,
                    reason: NexIrcResumeRejectionReason.Malformed);
                return false;
            }

            attempt.SequenceMessageIds[sequence!] = messageId;
            attempt.DuplicateEventsSuppressed++;
            duplicate = true;
            return true;
        }

        if (!string.Equals(previous, attempt.CurrentBoundary, StringComparison.Ordinal))
        {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    $"A native replay event was out of order or referenced an unknown boundary (previous={previous}; current={attempt.CurrentBoundary}; sequence={sequence}; committed={attempt.ReplayedEventCount}; known={string.Join(',', attempt.SequenceMessageIds.Keys)}).",
                fallbackSafe: false,
                reason: NexIrcResumeRejectionReason.Malformed);
            return false;
        }

        attempt.PendingSequence = sequence;
        attempt.PendingMessageId = messageId;
        return true;
    }

    private bool ValidateNativeLiveEnvelopeUnsafe(
        IrcMessage message,
        int generation,
        out bool duplicate)
    {
        duplicate = false;
        var sequence = message.TagValues.TryGetValue(NexIrcResumeProtocol.ResumeSequenceTag, out var rawSequence)
            ? rawSequence
            : null;
        var previous = message.TagValues.TryGetValue(NexIrcResumeProtocol.ResumePreviousSequenceTag, out var rawPrevious)
            ? rawPrevious
            : null;
        if (!NexIrcResumeProtocol.IsSafeOpaqueValue(sequence)
            || !NexIrcResumeProtocol.IsSafeOpaqueValue(previous)
            || message.ServerMessageId is null)
        {
            _continuity.RecordDiagnostic(generation, ContinuityDiagnosticKind.RecoveryRequestFailed, "A live native event lacked safe authoritative sequence metadata.");
            return false;
        }

        if (_nativeLiveSequenceMessageIds.TryGetValue(sequence!, out var existingMessageId))
        {
            if (!string.Equals(existingMessageId, message.ServerMessageId, StringComparison.Ordinal))
            {
                _continuity.RecordDiagnostic(generation, ContinuityDiagnosticKind.RecoveryRequestFailed, "A live native sequence was reused for a different canonical event.");
                return false;
            }

            _continuity.RecordHistoricalDuplicateSuppressed(generation, $"live:{message.ServerMessageId}");
            duplicate = true;
            return true;
        }

        var currentBoundary = _nativeResumeSession!.AuthoritativeBoundary;
        if (string.Equals(sequence, currentBoundary, StringComparison.Ordinal))
        {
            _nativeLiveSequenceMessageIds[sequence!] = message.ServerMessageId;
            _continuity.RecordHistoricalDuplicateSuppressed(generation, $"live-boundary:{message.ServerMessageId}");
            duplicate = true;
            return true;
        }

        if (!string.Equals(previous, currentBoundary, StringComparison.Ordinal))
        {
            if (_nativeLiveSequenceMessageIds.TryGetValue(sequence!, out var observedMessageId))
            {
                if (string.Equals(observedMessageId, message.ServerMessageId, StringComparison.Ordinal))
                {
                    _continuity.RecordHistoricalDuplicateSuppressed(generation, $"live:{message.ServerMessageId}");
                    duplicate = true;
                    return true;
                }

                _continuity.RecordDiagnostic(generation, ContinuityDiagnosticKind.RecoveryRequestFailed, "A non-contiguous live native sequence was reused for a different canonical event.");
                return false;
            }

            _nativeLiveSequenceMessageIds[sequence!] = message.ServerMessageId;
            _nativeLivePendingSequence = null;
            _nativeLivePendingMessageId = null;
            _continuity.RecordDiagnostic(
                generation,
                ContinuityDiagnosticKind.RecoveryRequestFailed,
                "A non-contiguous live native event was delivered while the protected boundary remained unchanged.");
            return true;
        }

        _nativeLivePendingSequence = sequence;
        _nativeLivePendingMessageId = message.ServerMessageId;
        return true;
    }

    private async Task CommitNativeLiveEventAsync(IrcMessage message, int generation)
    {
        NexIrcResumeSession? updated = null;
        lock (_gate)
        {
            if (_nativeResumeSession is null
                || _nativeLivePendingSequence is not { } sequence
                || !string.Equals(_nativeLivePendingMessageId, message.ServerMessageId, StringComparison.Ordinal))
            {
                return;
            }

            _nativeLiveSequenceMessageIds[sequence] = message.ServerMessageId!;
            updated = _nativeResumeSession = _nativeResumeSession.Advance(sequence);
            _nativeLivePendingSequence = null;
            _nativeLivePendingMessageId = null;
        }

        if (updated is not null)
        {
            await PersistNativeResumeStateAsync(updated).ConfigureAwait(false);
        }
    }

    private async Task CommitNativeReplayEventAsync(IrcMessage message, int generation)
    {
        NexIrcResumeSession? updated = null;
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation
                || attempt.PendingSequence is not { } sequence)
            {
                return;
            }

            if (string.Equals(attempt.CurrentBoundary, sequence, StringComparison.Ordinal))
            {
                attempt.PendingSequence = null;
                attempt.PendingMessageId = null;
                return;
            }

            if (!string.Equals(attempt.PendingMessageId, message.ServerMessageId, StringComparison.Ordinal))
            {
                FailNativeResumeAttemptUnsafe(
                    attempt,
                    NexIrcResumeOutcome.Failed,
                    "A native replay event changed while crossing the canonical acceptance boundary.",
                    fallbackSafe: false,
                    reason: NexIrcResumeRejectionReason.Malformed);
                return;
            }

            attempt.SequenceMessageIds[sequence] = message.ServerMessageId!;
            _nativeLiveSequenceMessageIds[sequence] = message.ServerMessageId!;
            attempt.CurrentBoundary = sequence;
            attempt.ReplayedEventCount++;
            attempt.PendingSequence = null;
            attempt.PendingMessageId = null;
            updated = _nativeResumeSession = _nativeResumeSession?.Advance(sequence);
        }

        if (updated is not null)
        {
            await PersistNativeResumeStateAsync(updated).ConfigureAwait(false);
        }
    }

    private async ValueTask ProjectNativeEventAsync(
        IrcSemanticEvent semanticEvent,
        ConnectionEpoch epoch,
        DateTimeOffset? receivedAt,
        CancellationToken cancellationToken)
    {
        var projector = _nativeEventProjector;
        if (projector is null)
        {
            return;
        }

        await projector(
            new SessionSemanticEvent(semanticEvent, epoch.Generation, receivedAt ?? DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private void ObserveNativeResumeBatch(IrcMessage message, ConnectionEpoch epoch)
    {
        var token = message.Parameters.Count == 0 ? null : message.Parameters[0];
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
        {
            return;
        }

        var batchId = token[1..];
        if (batchId.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var generation } attempt
                || generation != epoch.Generation)
            {
                return;
            }

            if (token[0] == '+')
            {
                if (!_stateStore.TryGetActiveBatch(batchId, out var type, out _)
                    || !string.Equals(type, NexIrcResumeProtocol.BatchType, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (!attempt.ReplayAccepted)
                {
                    return;
                }

                _acceptedNativeResumeBatches.Add(batchId);
                attempt.ActiveBatchIds.Add(batchId);
            }
            else if (token[0] == '-')
            {
                if (_acceptedNativeResumeBatches.Remove(batchId))
                {
                    attempt.ActiveBatchIds.Remove(batchId);
                }
            }
        }
    }

    private void CancelNativeResumeAttempt(int generation, string detail)
    {
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation)
            {
                return;
            }

            FailNativeResumeAttemptUnsafe(
                attempt,
                NexIrcResumeOutcome.Cancelled,
                detail,
                fallbackSafe: false);
        }
    }

    private void FailNativeResumeAttempt(
        int generation,
        NexIrcResumeOutcome outcome,
        string detail,
        bool fallbackSafe,
        NexIrcResumeRejectionReason? reason = null)
    {
        lock (_gate)
        {
            if (_nativeResumeAttempt is not { ConnectionGeneration: var attemptGeneration } attempt
                || attemptGeneration != generation)
            {
                return;
            }

            FailNativeResumeAttemptUnsafe(attempt, outcome, detail, fallbackSafe, reason);
        }
    }

    private void FailNativeResumeAttemptUnsafe(
        NativeResumeAttempt attempt,
        NexIrcResumeOutcome outcome,
        string detail,
        bool fallbackSafe,
        NexIrcResumeRejectionReason? reason = null)
    {
        var result = new NexIrcResumeExecutionResult(
            outcome,
            CapabilityNegotiated: true,
            RequestSent: attempt.RequestSent,
            ReplayAccepted: attempt.ReplayAccepted,
            ReplayCompleted: false,
            ExactBoundaryRecovered: false,
            attempt.ReplayedEventCount,
            attempt.DuplicateEventsSuppressed,
            attempt.RequestedBoundary,
            attempt.CurrentBoundary,
            detail,
            reason,
            fallbackSafe);
        _nativeResumeAttempt = null;
        _acceptedNativeResumeBatches.Clear();
        attempt.Completion.TrySetResult(result);
    }

    private bool IsAcceptedReplayBatchType(string type) =>
        IsAcceptedHistoryBatchType(type)
        || string.Equals(type, NexIrcResumeProtocol.BatchType, StringComparison.OrdinalIgnoreCase);

    private void ObserveHistoryError(IrcMessage message, ConnectionEpoch epoch)
    {
        if (message.Command is "FAIL"
            && !message.Parameters.Any(static parameter => parameter.Equals("CHATHISTORY", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        if (message.Command is not "FAIL"
            && message.NumericCommand is not (400 or 401 or 402 or 403 or 404 or 405 or 407 or 409 or 411 or 412 or 421 or 461))
        {
            return;
        }

        lock (_gate)
        {
            if (_activeHistoryRequest is null || _activeHistoryRequest.ConnectionGeneration != epoch.Generation)
            {
                return;
            }
        }

        CompleteHistoryRequest(ChathistoryRequestCompletion.Failed, MessageText(message));
    }

    private bool HistoryTargetMatches(string requested, string? returned)
    {
        return !string.IsNullOrWhiteSpace(returned)
            && IrcCaseMappingComparer.Equals(requested, returned, _features.CaseMapping);
    }

    private static bool IsHistoryBatchType(string type) =>
        string.Equals(type, "chathistory", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "draft/chathistory-targets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "chathistory-targets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "draft/chathistory-end", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "chathistory-end", StringComparison.OrdinalIgnoreCase);

    private bool IsAcceptedHistoryBatchType(string type) =>
        string.Equals(type, "chathistory", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "draft/chathistory-targets", StringComparison.OrdinalIgnoreCase)
        || string.Equals(type, "chathistory-targets", StringComparison.OrdinalIgnoreCase);

    private string NormalizeTarget(string target) => NormalizeName(target, _features.CaseMapping);

    private string NormalizeHistoryConversation(string conversation) =>
        IrcCaseMappingComparer.Fold(conversation, _features.CaseMapping);

    private ChathistoryConversationState GetHistoryStateUnsafe(string conversation)
    {
        var key = NormalizeHistoryConversation(conversation);
        return _historyStates.TryGetValue(key, out var state)
            ? state
            : ChathistoryConversationState.Empty(conversation, _connectionGeneration);
    }

    private void SetHistoryStateUnsafe(
        string conversation,
        bool requestActive,
        bool beginningReached,
        bool failed,
        string? failure,
        bool? latestReached = null,
        ChathistoryOperation? lastRequestOperation = null)
    {
        var previous = GetHistoryStateUnsafe(conversation);
        _historyStates[NormalizeHistoryConversation(conversation)] = new ChathistoryConversationState(
            conversation,
            _connectionGeneration,
            requestActive,
            beginningReached,
            failed,
            failure ?? (requestActive ? null : previous.LastFailure))
        {
            LatestReached = latestReached ?? previous.LatestReached,
            LastRequestOperation = lastRequestOperation ?? previous.LastRequestOperation
        };
    }

    private void CompleteHistoryRequest(
        ChathistoryRequestCompletion completion,
        string? failure,
        bool exhausted = false,
        int? expectedGeneration = null)
    {
        PendingChathistoryRequest? pending;
        lock (_gate)
        {
            pending = _activeHistoryRequest;
            if (pending is null || expectedGeneration is int expected && pending.ConnectionGeneration != expected)
            {
                return;
            }

            _activeHistoryRequest = null;
            _acceptedHistoryBatches.Clear();
            var reachedBeginning = pending.Request.Operation == ChathistoryOperation.Before
                && completion == ChathistoryRequestCompletion.Succeeded
                && (exhausted || pending.Messages.Count == 0);
            var reachedLatest = pending.Request.Operation == ChathistoryOperation.After
                && completion == ChathistoryRequestCompletion.Succeeded
                && exhausted;
            if (pending.Request.Conversation is { Length: > 0 } conversation)
            {
                var previous = GetHistoryStateUnsafe(conversation);
                SetHistoryStateUnsafe(
                    conversation,
                    requestActive: false,
                    beginningReached: reachedBeginning || previous.BeginningReached,
                    failed: completion is not ChathistoryRequestCompletion.Succeeded,
                    failure: failure,
                    latestReached: reachedLatest || previous.LatestReached,
                    lastRequestOperation: pending.Request.Operation);
            }
        }

        pending.Completion.TrySetResult(new ChathistoryResult(
            pending.RequestId,
            pending.Request,
            completion,
            pending.Messages.ToArray(),
            failure,
            exhausted)
        {
            BatchId = pending.BatchId,
            BatchType = pending.BatchType,
            BatchTarget = pending.BatchTarget,
            Targets = pending.Targets
                .GroupBy(target => NormalizeTarget(target.Target), StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(target => target.LatestTimestamp).First())
                .Take(16)
                .ToArray()
        });
        _continuity.RecordDiagnostic(
            pending.ConnectionGeneration,
            completion switch
            {
                ChathistoryRequestCompletion.Succeeded => ContinuityDiagnosticKind.RecoveryRequestCompleted,
                ChathistoryRequestCompletion.Cancelled or ChathistoryRequestCompletion.Disconnected or ChathistoryRequestCompletion.StaleGeneration => ContinuityDiagnosticKind.RecoveryRequestCancelled,
                _ => ContinuityDiagnosticKind.RecoveryRequestFailed
            },
            $"{pending.Request.Purpose}:{completion}{(failure is null ? string.Empty : $": {failure}")}");
    }

    private async ValueTask QueueOutboundAsync(IrcOutboundMessage command, CancellationToken cancellationToken, ConnectionEpoch? epoch = null)
    {
        ChannelWriter<IrcOutboundMessage>? writer;
        lock (_gate)
        {
            if (epoch is not null && !IsCurrentEpochUnsafe(epoch))
            {
                return;
            }

            writer = _outbound?.Writer;
        }

        if (writer is null)
        {
            throw new InvalidOperationException("The IRC session is not connected.");
        }

        await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private ConnectionEpoch BeginConnectionGeneration()
    {
        ConnectionContinuityTransition? transition;
        ConnectionEpoch epoch;
        lock (_gate)
        {
            if (_activeEpoch is not null)
            {
                _activeEpoch.IsActive = false;
            }

            _connectionGeneration++;
            _registration = RegistrationState.NotStarted;
            _lastFailure = null;
            _nicknameCandidateIndex = 0;
            _stateStore.SetNickname(_nicknameCandidates[0]);
            _registrationCommandsQueued = false;
            _capabilities.Reset();
            _isupport.Reset();
            _identityDetector.Reset();
            if (_options.ManualNetworkName is not null || _options.ManualIrcd.HasValue)
            {
                _identityDetector.SetManualOverride(_options.ManualNetworkName, _options.ManualIrcd);
            }

            _stateStore.SetGeneration(_connectionGeneration);
            _resynchronizationRequested.Clear();
            _historyStates.Clear();
            _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, _options.Profiles, _identityDetector.Snapshot, _options.MaximumChathistoryRequestSize);
            _authenticationState = _options.SaslPolicy == SaslAuthenticationPolicy.Disabled
                ? SaslAuthenticationState.Disabled
                : SaslAuthenticationState.WaitingForCapability;
            _authenticationMechanism = null;
            _authenticationFailure = null;
            _authenticatedAccountHint = null;
            DisposeActiveCredentialUnsafe();
            _activeSaslMechanism = null;
            _saslResponseSent = false;
            epoch = new ConnectionEpoch(_connectionGeneration);
            _activeEpoch = epoch;
            transition = _continuity.BeginRecovery(
                _connectionGeneration,
                _options.ContinuityRecoveryRequired,
                _options.ContinuityPreviousGeneration,
                DateTimeOffset.UtcNow);
        }

        PublishContinuityTransition(transition);
        return epoch;
    }

    private void ResetForReconnect()
    {
        ConnectionEpoch? epoch;
        lock (_gate)
        {
            epoch = _activeEpoch;
        }

        if (epoch is not null)
        {
            InvalidateConnectionState(epoch);
        }
    }

    private async Task PublishParseErrorAsync(string rawLine, string error, ConnectionEpoch epoch)
    {
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var item = new IrcParseErrorEvent(DateTimeOffset.UtcNow, IrcSensitiveData.RedactLine(rawLine), error, epoch.Generation);
        await _parseErrors.Writer.WriteAsync(item).ConfigureAwait(false);
    }

    private async Task PublishSemanticAsync(IrcSemanticEvent semanticEvent, ConnectionEpoch epoch, DateTimeOffset? receivedAt = null)
    {
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var item = new SessionSemanticEvent(semanticEvent, epoch.Generation, receivedAt ?? DateTimeOffset.UtcNow);
        await _semanticEvents.Writer.WriteAsync(item).ConfigureAwait(false);
        try
        {
            SemanticEventReceived?.Invoke(this, item);
        }
        catch
        {
        }
    }

    private void SetState(ServerSessionState state)
    {
        SessionStateChangedEvent? change = null;
        lock (_gate)
        {
            if (_state != state)
            {
                var previous = _state;
                SetStateUnsafe(state);
                change = new SessionStateChangedEvent(previous, state, _connectionGeneration);
            }
        }

        if (change is not null)
        {
            try
            {
                StateChanged?.Invoke(this, change);
            }
            catch
            {
            }
        }
    }

    private void SetStateUnsafe(ServerSessionState state)
    {
        _state = state;
    }

    private ConnectionContinuityTransition? MarkRegistrationComplete(int generation, bool synchronizationRequired)
    {
        lock (_gate)
        {
            return _continuity.MarkRegistrationComplete(
                generation,
                synchronizationRequired,
                DateTimeOffset.UtcNow);
        }
    }

    private ConnectionContinuityTransition? MarkInterrupted(int generation, ConnectionFailure failure)
    {
        lock (_gate)
        {
            return _continuity.MarkInterrupted(generation, failure, DateTimeOffset.UtcNow);
        }
    }

    private ConnectionContinuityTransition? MarkTerminal(int generation, string detail)
    {
        lock (_gate)
        {
            return _continuity.MarkTerminal(generation, DateTimeOffset.UtcNow, detail);
        }
    }

    private ConnectionContinuityTransition? MarkIntentionalDisconnect(string detail)
    {
        lock (_gate)
        {
            return _continuity.MarkIntentionalDisconnect(_connectionGeneration, DateTimeOffset.UtcNow, detail);
        }
    }

    private void PublishContinuityTransition(ConnectionContinuityTransition? transition)
    {
        if (transition is null)
        {
            return;
        }

        var change = new ConnectionContinuityStateChangedEvent(
            transition.Previous,
            transition.Current,
            transition.Snapshot);
        try
        {
            ContinuityStateChanged?.Invoke(this, change);
        }
        catch
        {
            // Continuity observers are projections/diagnostics and cannot
            // alter the protocol state machine.
        }
    }

    private ServerSessionSnapshot CreateSnapshot() => new(
        _state,
        _registration,
        _stateStore.Nickname,
        _options.Username,
        _options.RealName,
        _options.Endpoint,
        _connectionGeneration,
        _capabilities.Snapshot,
        _isupport.Snapshot,
        _identityDetector.Snapshot,
        _features,
        _lastFailure,
        _stateStore.Channels,
        _stateStore.Queries,
        _stateStore.DesiredChannels)
    {
        Continuity = _continuity.Snapshot,
        Motd = _stateStore.Motd,
        Authentication = new SaslAuthenticationSnapshot(
            _options.SaslPolicy,
            _authenticationState,
            _authenticationMechanism,
            _authenticationFailure,
            _connectionGeneration),
        DesiredNickname = _nicknameCandidates[0]
    };

    private static Channel<T> CreateEventChannel<T>() => Channel.CreateBounded<T>(new BoundedChannelOptions(4096)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = true
    });

    private static TimeSpan CalculateReconnectDelay(int attempt, ReconnectPolicy policy)
    {
        var multiplier = Math.Pow(2, Math.Max(0, attempt - 1));
        var milliseconds = Math.Min(policy.EffectiveMaximumDelay.TotalMilliseconds, policy.EffectiveInitialDelay.TotalMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(Math.Max(1, milliseconds));
    }

    private static bool NamesEqual(string? left, string? right, IrcCaseMapping mapping) => IrcCaseMappingComparer.Equals(left, right, mapping);

    private static string NormalizeName(string value, IrcCaseMapping mapping) => IrcCaseMappingComparer.Fold(value, mapping);

    private static string MessageText(IrcMessage message) => message.HasTrailingParameter
        ? message.TrailingParameter ?? string.Empty
        : string.Join(' ', message.Parameters);

    private static string[] BuildNicknameCandidates(ServerSessionOptions options)
    {
        var candidates = new[] { options.Nickname }
            .Concat(string.IsNullOrWhiteSpace(options.AlternateNickname) ? Array.Empty<string>() : [options.AlternateNickname!])
            .Concat(options.NicknameFallbacks ?? Array.Empty<string>())
            .Where(static nickname => !string.IsNullOrWhiteSpace(nickname))
            .Distinct(IrcCaseMappingComparer.For(IrcCaseMapping.Rfc1459))
            .ToArray();
        return candidates.Length == 0 ? [options.Nickname] : candidates;
    }

    private sealed class ConnectionEpoch(int generation)
    {
        public int Generation { get; } = generation;

        public bool IsActive { get; set; } = true;
    }

    private sealed class ReadStateSnapshotBuilder(int sessionEpoch, int expectedCount)
    {
        public int SessionEpoch { get; } = sessionEpoch;

        public int ExpectedCount { get; } = expectedCount;

        public Dictionary<string, NexIrcReadMarker> Markers { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PendingChathistoryRequest(long requestId, ChathistoryRequest request, int connectionGeneration)
    {
        public long RequestId { get; } = requestId;

        public ChathistoryRequest Request { get; } = request;

        public int ConnectionGeneration { get; } = connectionGeneration;

        public TaskCompletionSource<ChathistoryResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<IrcSemanticEvent> Messages { get; } = [];

        public List<ChathistoryTarget> Targets { get; } = [];

        public string? BatchId { get; set; }

        public string? BatchType { get; set; }

        public string? BatchTarget { get; set; }

        public bool EndMarker { get; set; }
    }

    private sealed class NativeResumeAttempt(int connectionGeneration, string requestedBoundary)
    {
        public int ConnectionGeneration { get; } = connectionGeneration;

        public bool IsAttachmentRequest { get; init; }

        public bool IsPairingRequest { get; init; }

        public bool IsPairingRecoveryRequest { get; init; }

        public bool AttachmentCreated { get; set; }

        public bool AttachmentAuthorityRecovered { get; set; }

        public string RequestedBoundary { get; } = requestedBoundary;

        public string CurrentBoundary { get; set; } = requestedBoundary;

        public string? AcceptedBoundary { get; set; }

        public bool RequestSent { get; set; }

        public bool ReplayAccepted { get; set; }

        public string? PendingSequence { get; set; }

        public string? PendingMessageId { get; set; }

        public int ReplayedEventCount { get; set; }

        public int DuplicateEventsSuppressed { get; set; }

        public HashSet<string> ActiveBatchIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> SequenceMessageIds { get; } = new(StringComparer.Ordinal);

        public TaskCompletionSource<NexIrcResumeExecutionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static void ValidateOptions(ServerSessionOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Nickname))
        {
            throw new ArgumentException("A nickname is required.", nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new ArgumentException("A username is required.", nameof(options));
        }

        if (options.ReadBufferBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ReadBufferBytes));
        }

        if (options.CapabilityNegotiationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.CapabilityNegotiationTimeout));
        }

        if (options.MaximumChathistoryRequestSize < 1 || options.MaximumChathistoryRequestSize > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MaximumChathistoryRequestSize));
        }

        if (options.ChathistoryRequestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.ChathistoryRequestTimeout));
        }

        if (options.Reconnect.MaximumAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options.Reconnect));
        }

        foreach (var channel in options.DesiredChannels)
        {
            ValidateChannelName(channel);
        }
    }

    private static void ValidateChannelName(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new ArgumentException("A channel name is required.", nameof(channel));
        }

        if (channel.Any(static character => character is ' ' or '\r' or '\n' or ','))
        {
            throw new ArgumentException("A channel name contains an invalid character.", nameof(channel));
        }
    }

}
