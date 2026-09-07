using System.Net;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text;
using nexIRC.Core.Networking;

namespace nexIRC.Networking.Testing;

public sealed record FakeInboundChunk(ReadOnlyMemory<byte> Bytes, TimeSpan Delay = default);

public sealed record FakeInboundFailure(ConnectionFailure Failure, TimeSpan Delay = default);

public sealed record FakeInboundDisconnect(TimeSpan Delay = default);

/// <summary>
/// Deterministic transport for protocol tests: chunks, delays, failures, disconnects, and outbound capture are explicit.
/// </summary>
public sealed class FakeIrcTransport : IIrcTransport, IIrcTransportCallbackSource
{
    private readonly Channel<object> _inbound = Channel.CreateUnbounded<object>();
    private readonly ConcurrentQueue<byte[]> _outbound = new();
    private readonly object _pendingGate = new();
    private Func<IrcTransportCallback, ValueTask>? _callbackReceived;
    private byte[]? _pendingBytes;
    private int _pendingOffset;
    private bool _connected;
    private bool _disposed;
    private int _activeReads;
    private int _maximumActiveReads;

    public FakeIrcTransport(IrcEndpoint endpoint)
    {
        Endpoint = endpoint;
    }

    public IrcEndpoint Endpoint { get; }

    public EndPoint? RemoteEndPoint { get; private set; }

    public bool IsConnected => _connected;

    public ConnectionFailure? LastFailure { get; private set; }

    public int ConnectCount { get; private set; }

    public int DisconnectCount { get; private set; }

    public int DisposeCount { get; private set; }

    public bool IsDisposed => _disposed;

    public int CallbackSubscriptionCount => _callbackReceived?.GetInvocationList().Length ?? 0;

    public int ActiveReadCount => Volatile.Read(ref _activeReads);

    public int MaximumActiveReadCount => Volatile.Read(ref _maximumActiveReads);

    public int PendingInboundItemCount
    {
        get
        {
            lock (_pendingGate)
            {
                return _inbound.Reader.Count + (_pendingBytes is null ? 0 : 1);
            }
        }
    }

    public ConnectionFailure? ConnectFailure { get; set; }

    public event Func<IrcTransportCallback, ValueTask>? CallbackReceived
    {
        add => _callbackReceived += value;
        remove => _callbackReceived -= value;
    }

    public IReadOnlyList<byte[]> OutboundBytes => _outbound.ToArray();

    public IReadOnlyList<string> OutboundLines => _outbound.Select(bytes => Encoding.UTF8.GetString(bytes).TrimEnd('\r', '\n')).ToArray();

    public ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ConnectCount++;
        if (ConnectFailure is not null)
        {
            LastFailure = ConnectFailure;
            throw new IrcTransportException(ConnectFailure);
        }

        _connected = true;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var activeReads = Interlocked.Increment(ref _activeReads);
        UpdateMaximum(ref _maximumActiveReads, activeReads);
        try
        {
            while (true)
            {
                lock (_pendingGate)
                {
                    if (_pendingBytes is not null)
                    {
                        var remaining = _pendingBytes.Length - _pendingOffset;
                        var count = Math.Min(remaining, buffer.Length);
                        _pendingBytes.AsMemory(_pendingOffset, count).CopyTo(buffer);
                        _pendingOffset += count;
                        if (_pendingOffset == _pendingBytes.Length)
                        {
                            _pendingBytes = null;
                            _pendingOffset = 0;
                        }

                        return count;
                    }
                }

                if (!_connected)
                {
                    return 0;
                }

                var item = await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                switch (item)
                {
                    case FakeInboundChunk chunk:
                        await DelayAsync(chunk.Delay, cancellationToken).ConfigureAwait(false);
                        if (chunk.Bytes.IsEmpty)
                        {
                            continue;
                        }

                        lock (_pendingGate)
                        {
                            _pendingBytes = chunk.Bytes.ToArray();
                        }

                        break;
                    case FakeInboundFailure failure:
                        await DelayAsync(failure.Delay, cancellationToken).ConfigureAwait(false);
                        LastFailure = failure.Failure;
                        _connected = false;
                        throw new IrcTransportException(failure.Failure);
                    case FakeInboundDisconnect disconnect:
                        await DelayAsync(disconnect.Delay, cancellationToken).ConfigureAwait(false);
                        _connected = false;
                        LastFailure = new ConnectionFailure(ConnectionFailureKind.RemoteClosed, "The fake remote closed the connection.");
                        return 0;
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref _activeReads);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_connected)
        {
            throw new IrcTransportException(new ConnectionFailure(ConnectionFailureKind.Network, "The fake transport is disconnected.", IsTransient: false));
        }

        _outbound.Enqueue(buffer.ToArray());
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(ConnectionFailure? reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCount++;
        _connected = false;
        LastFailure = reason ?? LastFailure;
        return ValueTask.CompletedTask;
    }

    public void EnqueueInboundBytes(ReadOnlyMemory<byte> bytes, TimeSpan delay = default) =>
        _inbound.Writer.TryWrite(new FakeInboundChunk(bytes, delay));

    public void EnqueueInboundLine(string line, TimeSpan delay = default) =>
        EnqueueInboundBytes(Encoding.UTF8.GetBytes(line + "\r\n"), delay);

    public void EnqueueFailure(ConnectionFailure failure, TimeSpan delay = default) =>
        _inbound.Writer.TryWrite(new FakeInboundFailure(failure, delay));

    public void EnqueueRemoteDisconnect(TimeSpan delay = default) =>
        _inbound.Writer.TryWrite(new FakeInboundDisconnect(delay));

    public ValueTask EmitCallbackAsync(IrcTransportCallback callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return _callbackReceived is { } handler ? handler(callback) : ValueTask.CompletedTask;
    }

    public void EnqueueScript(IEnumerable<object> steps)
    {
        foreach (var step in steps)
        {
            _inbound.Writer.TryWrite(step);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeCount++;
        _connected = false;
        _inbound.Writer.TryComplete();
        await ValueTask.CompletedTask;
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay > TimeSpan.Zero ? Task.Delay(delay, cancellationToken) : Task.CompletedTask;

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}

public sealed class FakeIrcTransportFactory : IIrcTransportFactory
{
    private readonly Queue<IIrcTransport> _transports = new();
    private readonly object _gate = new();

    public int CreatedTransportCount { get; private set; }

    public int RemainingTransportCount
    {
        get
        {
            lock (_gate)
            {
                return _transports.Count;
            }
        }
    }

    public void Add(IIrcTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        lock (_gate)
        {
            _transports.Enqueue(transport);
        }
    }

    public ValueTask<IIrcTransport> CreateAsync(IrcEndpoint endpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_transports.Count == 0)
            {
                throw new IrcTransportException(new ConnectionFailure(ConnectionFailureKind.Network, "The fake transport factory has no scripted connection left."));
            }

            CreatedTransportCount++;
            return ValueTask.FromResult(_transports.Dequeue());
        }
    }
}
