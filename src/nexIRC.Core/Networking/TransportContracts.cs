using System.Net;

namespace nexIRC.Core.Networking;

public readonly record struct IrcEndpoint
{
    public IrcEndpoint(string host, int port, bool useTls = true) : this()
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("An IRC endpoint requires a host.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "The port must be between 1 and 65535.");
        }

        Host = host;
        Port = port;
        UseTls = useTls;
    }

    public string Host { get; }

    public int Port { get; }

    public bool UseTls { get; }

    public override string ToString() => $"{Host}:{Port}";
}

public enum ConnectionFailureKind
{
    None,
    Cancelled,
    Timeout,
    Dns,
    Tls,
    Network,
    RemoteClosed,
    Protocol,
    RegistrationRejected,
    Intentional,
    Unknown
}

public sealed record ConnectionFailure(
    ConnectionFailureKind Kind,
    string Message,
    Exception? Exception = null,
    bool IsTransient = true);

public sealed class IrcTransportException : IOException
{
    public IrcTransportException(ConnectionFailure failure, Exception? innerException = null)
        : base(failure.Message, innerException ?? failure.Exception)
    {
        Failure = failure;
    }

    public ConnectionFailure Failure { get; }
}

public interface IIrcTransport : IAsyncDisposable
{
    IrcEndpoint Endpoint { get; }

    EndPoint? RemoteEndPoint { get; }

    bool IsConnected { get; }

    ConnectionFailure? LastFailure { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    ValueTask DisconnectAsync(ConnectionFailure? reason, CancellationToken cancellationToken);
}

public interface IIrcTransportFactory
{
    ValueTask<IIrcTransport> CreateAsync(IrcEndpoint endpoint, CancellationToken cancellationToken);
}

/// <summary>
/// Optional push notifications for transports that expose callbacks in
/// addition to the pull-based read contract. ServerSession binds these to the
/// connection epoch so late notifications cannot affect a newer connection.
/// </summary>
public abstract record IrcTransportCallback;

public sealed record IrcTransportInboundLineCallback(string Line) : IrcTransportCallback;

public sealed record IrcTransportFailureCallback(ConnectionFailure Failure) : IrcTransportCallback;

public sealed record IrcTransportDisconnectedCallback(ConnectionFailure? Failure = null) : IrcTransportCallback;

public interface IIrcTransportCallbackSource
{
    event Func<IrcTransportCallback, ValueTask>? CallbackReceived;
}
