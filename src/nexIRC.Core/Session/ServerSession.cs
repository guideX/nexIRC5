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
    private static readonly HashSet<int> KnownNumerics = [1, 4, 5, 332, 353, 366];
    private static readonly HashSet<int> NicknameFailureNumerics = [433, 436, 437];
    private static readonly HashSet<string> KnownCommands = ["CAP", "PING", "PONG", "PASS", "NICK", "USER", "JOIN", "PART", "QUIT", "PRIVMSG", "NOTICE", "TOPIC", "ERROR", "MODE"];

    private readonly ServerSessionOptions _options;
    private readonly IIrcTransportFactory _transportFactory;
    private readonly Channel<RawIrcLineEvent> _rawEvents = CreateEventChannel<RawIrcLineEvent>();
    private readonly Channel<ParsedIrcMessageEvent> _parsedEvents = CreateEventChannel<ParsedIrcMessageEvent>();
    private readonly Channel<IrcParseErrorEvent> _parseErrors = CreateEventChannel<IrcParseErrorEvent>();
    private readonly Channel<SessionSemanticEvent> _semanticEvents = CreateEventChannel<SessionSemanticEvent>();
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
    private bool _alternateNicknameUsed;
    private int _connectionGeneration;
    private bool _disposed;

    public ServerSession(ServerSessionOptions options, IIrcTransportFactory transportFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transportFactory);
        ValidateOptions(options);
        _options = options;
        _transportFactory = transportFactory;
        _stateStore = new SessionStateStore(options.Nickname);
        _capabilities = new IrcCapabilityNegotiator(options.RequestedCapabilities, options.MaximumOutboundLineBytes);
        _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, options.Profiles, ServerIdentity.Unknown);
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
                    BeginConnectionGeneration();
                    _identityDetector.ObserveHostname(_options.Endpoint.Host);
                    if (_options.Endpoint.UseTls)
                    {
                        SetState(ServerSessionState.TlsNegotiation);
                    }

                    await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    SetState(ServerSessionState.Connected);
                    failure = await RunConnectionAsync(transport, cancellationToken).ConfigureAwait(false);
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
            if (_state != ServerSessionState.Failed)
            {
                SetState(ServerSessionState.Disconnected);
            }
        }
    }

    private async Task<ConnectionFailure> RunConnectionAsync(IIrcTransport transport, CancellationToken sessionCancellation)
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

        var writerFailure = new StrongBox<ConnectionFailure?>(null);
        var writerTask = WriteLoopAsync(transport, outbound.Reader, connectionCts, writerFailure);
        try
        {
            SetState(ServerSessionState.CapNegotiation);
            _registration = RegistrationState.CapNegotiating;
            await QueueRegistrationAsync(outbound.Writer, connectionCts.Token).ConfigureAwait(false);
            return await ReadLoopAsync(transport, connectionCts, writerFailure).ConfigureAwait(false);
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

            connectionCts.Dispose();
        }
    }

    private async Task QueueRegistrationAsync(ChannelWriter<IrcOutboundMessage> writer, CancellationToken cancellationToken)
    {
        var capStart = _capabilities.Start();
        foreach (var command in capStart.Commands)
        {
            await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }

        if (_options.Password is not null)
        {
            await writer.WriteAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("PASS", trailingParameter: _options.Password), cancellationToken).ConfigureAwait(false);
        }

        await writer.WriteAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("NICK", [_stateStore.Nickname]), cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("USER", [_options.Username, "0", "*"], _options.RealName), cancellationToken).ConfigureAwait(false);
        SetState(ServerSessionState.CapNegotiation);
    }

    private async Task<ConnectionFailure> ReadLoopAsync(IIrcTransport transport, CancellationTokenSource connectionCts, StrongBox<ConnectionFailure?> writerFailure)
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
                    await PublishParseErrorAsync(incomplete.IncompleteText ?? string.Empty, "The server disconnected with an incomplete IRC line.").ConfigureAwait(false);
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
                await ProcessFrameAsync(frame, connectionCts.Token).ConfigureAwait(false);
            }
        }

        return writerFailure.Value ?? new ConnectionFailure(ConnectionFailureKind.Cancelled, "The IRC connection loop was cancelled.", IsTransient: false);
    }

    private async Task WriteLoopAsync(IIrcTransport transport, ChannelReader<IrcOutboundMessage> reader, CancellationTokenSource connectionCts, StrongBox<ConnectionFailure?> writerFailure)
    {
        try
        {
            await foreach (var command in reader.ReadAllAsync(connectionCts.Token).ConfigureAwait(false))
            {
                await transport.WriteAsync(command.FramedBytes, connectionCts.Token).ConfigureAwait(false);
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

    private async Task ProcessFrameAsync(IrcLineFrame frame, CancellationToken cancellationToken)
    {
        var rawEvent = new RawIrcLineEvent(DateTimeOffset.UtcNow, frame.Text, frame.Bytes, _connectionGeneration);
        await _rawEvents.Writer.WriteAsync(rawEvent, cancellationToken).ConfigureAwait(false);
        var parse = IrcMessageParser.Parse(frame.Text);
        if (!parse.Success)
        {
            await PublishParseErrorAsync(frame.Text, parse.Error ?? "The IRC line could not be parsed.").ConfigureAwait(false);
            return;
        }

        var message = parse.Message!;
        await _parsedEvents.Writer.WriteAsync(new ParsedIrcMessageEvent(DateTimeOffset.UtcNow, message, _connectionGeneration), cancellationToken).ConfigureAwait(false);
        var capResult = _capabilities.Handle(message);
        if (message.Command == "CAP")
        {
            lock (_gate)
            {
                _identityDetector.ObserveCapabilities(capResult.Snapshot);
                _features = ServerFeatureSet.Build(capResult.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot);
            }

            foreach (var command in capResult.Commands)
            {
                await QueueOutboundAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (capResult.Snapshot.NegotiationState == CapNegotiationState.Ended && _registration != RegistrationState.Registered)
            {
                _registration = RegistrationState.Registering;
                SetState(ServerSessionState.Registering);
            }
        }

        if (message.NumericCommand == 5)
        {
            lock (_gate)
            {
                _isupport.Apply(message);
                _identityDetector.ObserveISupport(_isupport.Snapshot);
                _features = ServerFeatureSet.Build(_capabilities.Snapshot, _isupport.Snapshot, _options.Profiles, _identityDetector.Snapshot);
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
            _registration = RegistrationState.Registered;
            SetState(ServerSessionState.Registered);
        }

        if (message.NumericCommand is int numeric && NicknameFailureNumerics.Contains(numeric))
        {
            await HandleNicknameFailureAsync(cancellationToken).ConfigureAwait(false);
        }

        if (message.Command == "PING")
        {
            var payload = message.HasTrailingParameter ? message.TrailingParameter ?? string.Empty : message.Parameters.Count > 0 ? message.Parameters[0] : string.Empty;
            await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("PONG", trailingParameter: payload), cancellationToken).ConfigureAwait(false);
            await PublishSemanticAsync(new IrcPingEvent(message, payload)).ConfigureAwait(false);
        }

        if (message.Command == "ERROR")
        {
            _lastFailure = new ConnectionFailure(ConnectionFailureKind.Protocol, message.HasTrailingParameter ? message.TrailingParameter ?? "The IRC server reported an error." : "The IRC server reported an error.", IsTransient: false);
            _registration = RegistrationState.Failed;
            SetState(ServerSessionState.Failed);
            await PublishSemanticAsync(new IrcServerErrorEvent(message, _lastFailure.Message)).ConfigureAwait(false);
            connectionCancellationFromMessage();
        }

        IReadOnlyList<IrcSemanticEvent> stateEvents;
        lock (_gate)
        {
            stateEvents = _stateStore.Apply(message, _features);
        }

        foreach (var semanticEvent in stateEvents)
        {
            await PublishSemanticAsync(semanticEvent).ConfigureAwait(false);
        }

        if (EventDispatcher.TryDispatch(message, out var extensionEvent))
        {
            await PublishSemanticAsync(extensionEvent!).ConfigureAwait(false);
        }

        if (message.NumericCommand is int finalNumeric)
        {
            if (!KnownNumerics.Contains(finalNumeric))
            {
                await PublishSemanticAsync(new IrcUnknownNumericEvent(message, finalNumeric)).ConfigureAwait(false);
            }
            else if (finalNumeric is not 1)
            {
                await PublishSemanticAsync(new IrcNumericEvent(message, finalNumeric)).ConfigureAwait(false);
            }
        }
        else if (!KnownCommands.Contains(message.Command))
        {
            await PublishSemanticAsync(new IrcUnknownCommandEvent(message)).ConfigureAwait(false);
        }

        void connectionCancellationFromMessage()
        {
            _runCts?.Cancel();
        }
    }

    private async Task HandleNicknameFailureAsync(CancellationToken cancellationToken)
    {
        if (!_alternateNicknameUsed && !string.IsNullOrWhiteSpace(_options.AlternateNickname))
        {
            _alternateNicknameUsed = true;
            _stateStore.SetNickname(_options.AlternateNickname!);
            await QueueOutboundAsync(new IrcCommandBuilder(_options.MaximumOutboundLineBytes).Build("NICK", [_options.AlternateNickname!]), cancellationToken).ConfigureAwait(false);
            return;
        }

        _registration = RegistrationState.Failed;
        _lastFailure = new ConnectionFailure(ConnectionFailureKind.RegistrationRejected, "The server rejected the configured nickname and no alternate nickname is available.", IsTransient: false);
        SetState(ServerSessionState.Failed);
        _runCts?.Cancel();
    }

    private async ValueTask QueueOutboundAsync(IrcOutboundMessage command, CancellationToken cancellationToken)
    {
        ChannelWriter<IrcOutboundMessage>? writer;
        lock (_gate)
        {
            writer = _outbound?.Writer;
        }

        if (writer is null)
        {
            throw new InvalidOperationException("The IRC session is not connected.");
        }

        await writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private void BeginConnectionGeneration()
    {
        lock (_gate)
        {
            _connectionGeneration++;
            _registration = RegistrationState.NotStarted;
            _lastFailure = null;
            _alternateNicknameUsed = false;
            _capabilities.Reset();
            _isupport.Reset();
            _identityDetector.Reset();
            if (_options.ManualNetworkName is not null || _options.ManualIrcd.HasValue)
            {
                _identityDetector.SetManualOverride(_options.ManualNetworkName, _options.ManualIrcd);
            }

            _stateStore.SetGeneration(_connectionGeneration);
            _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, _options.Profiles, _identityDetector.Snapshot);
        }
    }

    private void ResetForReconnect()
    {
        lock (_gate)
        {
            _stateStore.SetGeneration(_connectionGeneration);
            _capabilities.Reset();
            _isupport.Reset();
            _identityDetector.Reset();
            _features = ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, _options.Profiles, ServerIdentity.Unknown);
            _registration = RegistrationState.NotStarted;
        }
    }

    private async Task PublishParseErrorAsync(string rawLine, string error)
    {
        var item = new IrcParseErrorEvent(DateTimeOffset.UtcNow, rawLine, error, _connectionGeneration);
        await _parseErrors.Writer.WriteAsync(item).ConfigureAwait(false);
    }

    private async Task PublishSemanticAsync(IrcSemanticEvent semanticEvent)
    {
        var item = new SessionSemanticEvent(semanticEvent, _connectionGeneration);
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
        _options.DesiredChannels);

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
    }

}
