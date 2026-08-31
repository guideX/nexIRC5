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
    private static readonly HashSet<int> KnownNumerics =
    [
        1, 4, 5, 301, 307, 310, 311, 312, 313, 315, 317, 318, 319, 321, 322, 323, 324,
        330, 335, 338, 352, 353, 366, 367, 368, 369, 372, 375, 376, 378, 379, 422,
        671, 900, 903, 904, 905, 906, 907, 908
    ];
    private static readonly HashSet<int> NicknameFailureNumerics = [433, 436, 437];
    private static readonly HashSet<string> KnownCommands =
    [
        "CAP", "PING", "PONG", "PASS", "NICK", "USER", "JOIN", "PART", "QUIT", "PRIVMSG", "NOTICE",
        "TOPIC", "ERROR", "MODE", "KICK", "INVITE", "AWAY", "WALLOPS", "AUTHENTICATE"
    ];

    private readonly ServerSessionOptions _options;
    private readonly IIrcTransportFactory _transportFactory;
    private readonly Channel<RawIrcLineEvent> _rawEvents = CreateEventChannel<RawIrcLineEvent>();
    private readonly Channel<ParsedIrcMessageEvent> _parsedEvents = CreateEventChannel<ParsedIrcMessageEvent>();
    private readonly Channel<IrcParseErrorEvent> _parseErrors = CreateEventChannel<IrcParseErrorEvent>();
    private readonly Channel<SessionSemanticEvent> _semanticEvents = CreateEventChannel<SessionSemanticEvent>();
    private readonly Channel<OutboundIrcCommandEvent> _outboundEvents = CreateEventChannel<OutboundIrcCommandEvent>();
    private readonly object _gate = new();
    private readonly SessionStateStore _stateStore;
    private readonly IrcCapabilityNegotiator _capabilities;
    private readonly ISupportState _isupport = new();
    private readonly ServerIdentityDetector _identityDetector = new();
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
    private SaslCredential? _activeCredential;
    private ISaslMechanism? _activeSaslMechanism;
    private bool _saslResponseSent;
    private bool _disposed;

    public ServerSession(ServerSessionOptions options, IIrcTransportFactory transportFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ValidateOptions(options);
        _options = options;
        _transportFactory = transportFactory;
        _stateStore = new SessionStateStore(options.Nickname, options.DesiredChannels);
        _nicknameCandidates = BuildNicknameCandidates(options);
        var requestedCapabilityList = options.RequestedCapabilities
            .Where(capability => options.SaslPolicy != SaslAuthenticationPolicy.Disabled || !string.Equals(capability, "sasl", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (options.SaslPolicy != SaslAuthenticationPolicy.Disabled &&
            !requestedCapabilityList.Any(capability => string.Equals(capability, "sasl", StringComparison.OrdinalIgnoreCase)))
        {
            requestedCapabilityList.Add("sasl");
        }

        var requestedCapabilities = requestedCapabilityList.ToArray();
        _capabilities = new IrcCapabilityNegotiator(
            requestedCapabilities,
            options.MaximumOutboundLineBytes,
            options.SaslPolicy == SaslAuthenticationPolicy.Disabled ? Array.Empty<string>() : ["sasl"]);
        _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, options.Profiles, ServerIdentity.Unknown);
        _authenticationState = options.SaslPolicy == SaslAuthenticationPolicy.Disabled
            ? SaslAuthenticationState.Disabled
            : SaslAuthenticationState.WaitingForCapability;
    }

    public event EventHandler<SessionStateChangedEvent>? StateChanged;

    public event EventHandler<SessionSemanticEvent>? SemanticEventReceived;

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

        if (runTask is null)
        {
            return;
        }

        if (_registration == RegistrationState.Registered)
        {
            try
            {
                await SendCommandAsync("QUIT", trailingParameter: reason ?? "nexIRC disconnect").ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        _runCts?.Cancel();
        await runTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCts.Cancel();
        await DisconnectAsync("nexIRC session disposed").ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private Task StartRun(CancellationToken cancellationToken)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        return RunSupervisorAsync(_runCts.Token);
    }

    private async Task RunSupervisorAsync(CancellationToken cancellationToken)
    {
        var reconnectAttempt = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_disconnectRequested)
            {
                reconnectAttempt++;
                IIrcTransport? transport = null;
                ConnectionFailure? failure;
                try
                {
                    SetState(ServerSessionState.Connecting);
                    transport = await _transportFactory.CreateAsync(_options.Endpoint, cancellationToken).ConfigureAwait(false);
                    var epoch = BeginConnectionGeneration();
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

                if (_disconnectRequested || cancellationToken.IsCancellationRequested || failure.Kind is ConnectionFailureKind.Cancelled or ConnectionFailureKind.Intentional)
                {
                    break;
                }

                _lastFailure = failure;
                ResetForReconnect();
                if (!_options.Reconnect.Enabled || reconnectAttempt >= Math.Max(1, _options.Reconnect.MaximumAttempts))
                {
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

        if (transport is IIrcTransportCallbackSource callbackSource)
        {
            var weakSession = new WeakReference<ServerSession>(this);
            callbackSource.CallbackReceived += callback => weakSession.TryGetTarget(out var session)
                ? session.ProcessTransportCallbackAsync(callback, epoch, connectionCts)
                : ValueTask.CompletedTask;
        }

        var writerFailure = new StrongBox<ConnectionFailure?>(null);
        var writerTask = WriteLoopAsync(transport, outbound.Reader, connectionCts, writerFailure, epoch);
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
            outbound.Writer.TryComplete();
            connectionCts.Cancel();
            try
            {
                await writerTask.ConfigureAwait(false);
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
                if (IsCurrentEpoch(epoch))
                {
                    var redactedBytes = IrcSensitiveData.RedactFramedBytes(command.FramedBytes.Span);
                    _outboundEvents.Writer.TryWrite(new OutboundIrcCommandEvent(
                        DateTimeOffset.UtcNow,
                        IrcSensitiveData.RedactLine(command.Line),
                        redactedBytes,
                        epoch.Generation));
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
            return;
        }

        var rawEvent = new RawIrcLineEvent(DateTimeOffset.UtcNow, frame.Text, frame.Bytes, epoch.Generation);
        await _rawEvents.Writer.WriteAsync(rawEvent, connectionCts.Token).ConfigureAwait(false);
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var parse = IrcMessageParser.Parse(frame.Text);
        if (!parse.Success)
        {
            await PublishParseErrorAsync(frame.Text, parse.Error ?? "The IRC line could not be parsed.", epoch).ConfigureAwait(false);
            return;
        }

        var message = parse.Message!;
        await _parsedEvents.Writer.WriteAsync(new ParsedIrcMessageEvent(DateTimeOffset.UtcNow, message, epoch.Generation), connectionCts.Token).ConfigureAwait(false);
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
                _features = ServerFeatureSet.Build(capResult.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot);
            }

            await PublishCapabilityChangesAsync(message, capabilitiesBefore, capResult.Snapshot, epoch).ConfigureAwait(false);
            await HandleCapabilityResultAsync(message, capResult, epoch, connectionCts).ConfigureAwait(false);
        }

        if (message.NumericCommand == 5)
        {
            lock (_gate)
            {
                _isupport.Apply(message);
                _identityDetector.ObserveISupport(_isupport.Snapshot);
                _features = ServerFeatureSet.Build(_capabilities.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot);
                _stateStore.SetCaseMapping(_features.CaseMapping);
            }
        }
        else if (message.NumericCommand == 4)
        {
            lock (_gate)
            {
                _identityDetector.ObserveMyInfo(message);
                _features = ServerFeatureSet.Build(_capabilities.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot);
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

            SetState(ServerSessionState.Registered);
            await PublishSemanticAsync(new IrcRegistrationStateEvent(message, previousRegistration, _registration), epoch).ConfigureAwait(false);
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
            await PublishSemanticAsync(new IrcPingEvent(message, payload), epoch).ConfigureAwait(false);
        }

        if (message.Command == "ERROR")
        {
            _lastFailure = new ConnectionFailure(ConnectionFailureKind.Protocol, message.HasTrailingParameter ? message.TrailingParameter ?? "The IRC server reported an error." : "The IRC server reported an error.", IsTransient: false);
            _registration = RegistrationState.Failed;
            SetState(ServerSessionState.Failed);
            await PublishSemanticAsync(new IrcServerErrorEvent(message, _lastFailure.Message), epoch).ConfigureAwait(false);
            connectionCts.Cancel();
        }

        IReadOnlyList<IrcSemanticEvent> stateEvents;
        lock (_gate)
        {
            stateEvents = _stateStore.Apply(message, _features);
        }

        foreach (var semanticEvent in stateEvents)
        {
            await PublishSemanticAsync(semanticEvent, epoch).ConfigureAwait(false);

            if (semanticEvent is IrcJoinEvent join && NamesEqual(join.Nickname, _stateStore.Nickname, _features.CaseMapping))
            {
                await QueueChannelResynchronizationAsync(join.Channel, epoch, connectionCts.Token).ConfigureAwait(false);
                await PublishSemanticAsync(new IrcChannelSynchronizationEvent(message, join.Channel, ChannelSynchronizationState.Synchronizing), epoch).ConfigureAwait(false);
            }
        }

        if (EventDispatcher.TryDispatch(message, out var extensionEvent))
        {
            await PublishSemanticAsync(extensionEvent!, epoch).ConfigureAwait(false);
        }

        if (message.NumericCommand is int finalNumeric)
        {
            if (!KnownNumerics.Contains(finalNumeric))
            {
                await PublishSemanticAsync(new IrcUnknownNumericEvent(message, finalNumeric), epoch).ConfigureAwait(false);
            }
            else if (finalNumeric is not 1)
            {
                await PublishSemanticAsync(new IrcNumericEvent(message, finalNumeric), epoch).ConfigureAwait(false);
            }
        }
        else if (!KnownCommands.Contains(message.Command))
        {
            await PublishSemanticAsync(new IrcUnknownCommandEvent(message), epoch).ConfigureAwait(false);
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
        var saslRequested = _options.SaslPolicy != SaslAuthenticationPolicy.Disabled && _capabilities.Snapshot.RequestedTokens.Contains("sasl", StringComparer.Ordinal);
        var saslAvailable = result.Snapshot.IsAvailable("sasl");
        if (saslRequested && subcommand == "LS" && !HasMoreCapabilityListing(message) && !saslAvailable)
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                await FailAuthenticationAsync(message, "The server does not advertise SASL.", epoch, connectionCts, fatal: true).ConfigureAwait(false);
                return;
            }

            await SetAuthenticationStateAsync(message, SaslAuthenticationState.Skipped, null, "SASL is unavailable; continuing without authentication.", epoch).ConfigureAwait(false);
        }

        if (subcommand == "NAK" && saslRequested && messageHasCapability(message, "sasl"))
        {
            if (_options.SaslPolicy == SaslAuthenticationPolicy.Required)
            {
                await FailAuthenticationAsync(message, "The server rejected the SASL capability request.", epoch, connectionCts, fatal: true).ConfigureAwait(false);
                return;
            }

            await FailAuthenticationAsync(message, "The server rejected the SASL capability request.", epoch, connectionCts, fatal: false).ConfigureAwait(false);
            return;
        }

        if (subcommand == "ACK" && saslRequested && result.Snapshot.IsEnabled("sasl"))
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
        var result = _capabilities.Complete();
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
        ConnectionEpoch epoch)
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
            await PublishSemanticAsync(new IrcSaslStateChangedEvent(message, previous, state, mechanism ?? _authenticationMechanism, detail), epoch).ConfigureAwait(false);
        }
    }

    private async Task PublishCapabilityChangesAsync(
        IrcMessage message,
        CapabilitySnapshot before,
        CapabilitySnapshot after,
        ConnectionEpoch epoch)
    {
        var available = after.Available.Keys.Except(before.Available.Keys, StringComparer.Ordinal).ToArray();
        var removed = before.Available.Keys.Except(after.Available.Keys, StringComparer.Ordinal).ToArray();
        var enabled = after.Enabled.Except(before.Enabled, StringComparer.Ordinal).ToArray();
        var disabled = before.Enabled.Except(after.Enabled, StringComparer.Ordinal).ToArray();
        foreach (var change in new[]
        {
            (IrcCapabilityChangeKind.Available, available),
            (IrcCapabilityChangeKind.Removed, removed),
            (IrcCapabilityChangeKind.Enabled, enabled),
            (IrcCapabilityChangeKind.Disabled, disabled)
        })
        {
            if (change.Item2.Length > 0)
            {
                await PublishSemanticAsync(new IrcCapabilityChangedEvent(message, change.Item1, change.Item2), epoch).ConfigureAwait(false);
            }
        }
    }

    private static ISaslMechanism? SelectSaslMechanism(CapabilitySnapshot capabilities, IReadOnlyList<ISaslMechanism> mechanisms)
    {
        var advertised = capabilities.Available.TryGetValue("sasl", out var sasl) && !string.IsNullOrWhiteSpace(sasl.Value)
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
            _capabilities.Reset();
            _isupport.Reset();
            _identityDetector.Reset();
            if (_options.ManualNetworkName is not null || _options.ManualIrcd.HasValue)
            {
                _identityDetector.SetManualOverride(_options.ManualNetworkName, _options.ManualIrcd);
            }

            _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, _options.Profiles, _identityDetector.Snapshot);
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
        }
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
        }

        var builder = new IrcCommandBuilder(_options.MaximumOutboundLineBytes);
        await QueueOutboundAsync(builder.Build("NAMES", [channel]), cancellationToken, epoch).ConfigureAwait(false);
        await QueueOutboundAsync(builder.Build("TOPIC", [channel]), cancellationToken, epoch).ConfigureAwait(false);
        await QueueOutboundAsync(builder.Build("WHO", [channel]), cancellationToken, epoch).ConfigureAwait(false);
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
            _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, _options.Profiles, _identityDetector.Snapshot);
            _authenticationState = _options.SaslPolicy == SaslAuthenticationPolicy.Disabled
                ? SaslAuthenticationState.Disabled
                : SaslAuthenticationState.WaitingForCapability;
            _authenticationMechanism = null;
            _authenticationFailure = null;
            DisposeActiveCredentialUnsafe();
            _activeSaslMechanism = null;
            _saslResponseSent = false;
            var epoch = new ConnectionEpoch(_connectionGeneration);
            _activeEpoch = epoch;
            return epoch;
        }
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

        var item = new IrcParseErrorEvent(DateTimeOffset.UtcNow, rawLine, error, epoch.Generation);
        await _parseErrors.Writer.WriteAsync(item).ConfigureAwait(false);
    }

    private async Task PublishSemanticAsync(IrcSemanticEvent semanticEvent, ConnectionEpoch epoch)
    {
        if (!IsCurrentEpoch(epoch))
        {
            return;
        }

        var item = new SessionSemanticEvent(semanticEvent, epoch.Generation);
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
