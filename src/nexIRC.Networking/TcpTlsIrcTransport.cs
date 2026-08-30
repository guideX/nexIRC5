using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using nexIRC.Core.Networking;

namespace nexIRC.Networking;

public sealed class TcpTlsIrcTransportOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaximumReadBytes { get; init; } = 64 * 1024;

    public SslProtocols EnabledSslProtocols { get; init; } = SslProtocols.None;
}

/// <summary>
/// Standard .NET TCP/TLS IRC transport. Certificate validation is delegated to the platform defaults.
/// </summary>
public sealed class TcpTlsIrcTransport : IIrcTransport
{
    private readonly TcpTlsIrcTransportOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private TcpClient? _client;
    private Stream? _stream;
    private bool _disposed;

    public TcpTlsIrcTransport(IrcEndpoint endpoint, TcpTlsIrcTransportOptions? options = null)
    {
        Endpoint = endpoint;
        _options = options ?? new TcpTlsIrcTransportOptions();
        if (_options.ConnectTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The connection timeout must be positive.");
        }

        if (_options.MaximumReadBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The maximum read size must be positive.");
        }
    }

    public IrcEndpoint Endpoint { get; }

    public EndPoint? RemoteEndPoint => _client?.Client.RemoteEndPoint;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public ConnectionFailure? LastFailure { get; private set; }

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
        {
            return;
        }

        await DisconnectAsync(null, CancellationToken.None).ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(Endpoint.Host, Endpoint.Port, linked.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (Endpoint.UseTls)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = Endpoint.Host,
                    EnabledSslProtocols = _options.EnabledSslProtocols
                }, linked.Token).ConfigureAwait(false);
                stream = ssl;
            }

            _client = client;
            _stream = stream;
            LastFailure = null;
        }
        catch (OperationCanceledException exception)
        {
            await CloseResourcesAsync(client).ConfigureAwait(false);
            var failure = cancellationToken.IsCancellationRequested
                ? new ConnectionFailure(ConnectionFailureKind.Cancelled, "The IRC connection was cancelled.", exception, false)
                : new ConnectionFailure(ConnectionFailureKind.Timeout, $"The IRC connection timed out after {_options.ConnectTimeout}.", exception);
            LastFailure = failure;
            throw new IrcTransportException(failure, exception);
        }
        catch (AuthenticationException exception)
        {
            await CloseResourcesAsync(client).ConfigureAwait(false);
            var failure = new ConnectionFailure(ConnectionFailureKind.Tls, "TLS negotiation failed; the server certificate or protocol was not accepted.", exception, false);
            LastFailure = failure;
            throw new IrcTransportException(failure, exception);
        }
        catch (SocketException exception)
        {
            await CloseResourcesAsync(client).ConfigureAwait(false);
            var kind = exception.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain
                ? ConnectionFailureKind.Dns
                : ConnectionFailureKind.Network;
            var failure = new ConnectionFailure(kind, $"The IRC TCP connection failed: {exception.Message}", exception);
            LastFailure = failure;
            throw new IrcTransportException(failure, exception);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            await CloseResourcesAsync(client).ConfigureAwait(false);
            var failure = new ConnectionFailure(ConnectionFailureKind.Network, $"The IRC connection failed: {exception.Message}", exception);
            LastFailure = failure;
            throw new IrcTransportException(failure, exception);
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        var stream = _stream ?? throw new IrcTransportException(new ConnectionFailure(ConnectionFailureKind.Network, "The IRC transport is not connected.", IsTransient: false));
        var bounded = buffer.Length > _options.MaximumReadBytes ? buffer[.._options.MaximumReadBytes] : buffer;
        try
        {
            return await stream.ReadAsync(bounded, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            var failure = new ConnectionFailure(ConnectionFailureKind.Network, $"Reading from the IRC connection failed: {exception.Message}", exception);
            LastFailure = failure;
            throw new IrcTransportException(failure, exception);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stream = _stream ?? throw new IrcTransportException(new ConnectionFailure(ConnectionFailureKind.Network, "The IRC transport is not connected.", IsTransient: false));
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception)
        {
            var failure = new ConnectionFailure(ConnectionFailureKind.Network, $"Writing to the IRC connection failed: {exception.Message}", exception);
            LastFailure = failure;
            throw new IrcTransportException(failure, exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(ConnectionFailure? reason, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (reason is not null)
            {
                LastFailure = reason;
            }

            var stream = Interlocked.Exchange(ref _stream, null);
            var client = Interlocked.Exchange(ref _client, null);
            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            client?.Dispose();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync(new ConnectionFailure(ConnectionFailureKind.Intentional, "The IRC transport was disposed.", IsTransient: false), CancellationToken.None).ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private static async ValueTask CloseResourcesAsync(TcpClient client)
    {
        try
        {
            client.Close();
            client.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

public sealed class TcpTlsIrcTransportFactory : IIrcTransportFactory
{
    private readonly TcpTlsIrcTransportOptions _options;

    public TcpTlsIrcTransportFactory(TcpTlsIrcTransportOptions? options = null)
    {
        _options = options ?? new TcpTlsIrcTransportOptions();
    }

    public ValueTask<IIrcTransport> CreateAsync(IrcEndpoint endpoint, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IIrcTransport>(new TcpTlsIrcTransport(endpoint, _options));
}
