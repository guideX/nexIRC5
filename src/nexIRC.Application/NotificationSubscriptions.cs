using System.Threading.Channels;

namespace nexIRC.Application;

public enum IrcNotificationType
{
    Message,
    PrivateMessage,
    Highlight,
    Notice,
    Error,
    Status
}

public sealed record IrcNotification(
    Guid NetworkId,
    Guid ViewId,
    WorkspaceViewKind ViewKind,
    IrcNotificationType Type,
    WorkspaceActivity Activity,
    string? Sender,
    string Summary,
    DateTimeOffset Timestamp,
    bool IsViewActive,
    string SemanticSource,
    bool IsOwnMessage = false);

public interface IIrcNotificationService : IDisposable
{
    IDisposable Subscribe(Action<IrcNotification> subscriber);

    void Publish(IrcNotification notification);
}

/// <summary>
/// Narrow application notification boundary. Each subscriber has a bounded
/// drop-oldest queue and an independent consumer, so publication is a quick
/// TryWrite and cannot wait for or fail because of another subscriber.
/// </summary>
public sealed class NotificationSubscriptionService : IIrcNotificationService
{
    private readonly object _gate = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private bool _disposed;

    public IDisposable Subscribe(Action<IrcNotification> subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);
        var subscription = new Subscription(this, subscriber);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(subscription);
        }

        subscription.Start();
        return subscription;
    }

    public void Publish(IrcNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            subscriptions = _subscriptions.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.TryPublish(notification);
        }
    }

    public void Dispose()
    {
        Subscription[] subscriptions;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            subscriptions = _subscriptions.ToArray();
            _subscriptions.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly NotificationSubscriptionService _owner;
        private readonly Action<IrcNotification> _subscriber;
        private readonly Channel<IrcNotification> _queue = Channel.CreateBounded<IrcNotification>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        private int _disposed;

        public Subscription(NotificationSubscriptionService owner, Action<IrcNotification> subscriber)
        {
            _owner = owner;
            _subscriber = subscriber;
        }

        public void Start() => _ = ConsumeAsync();

        public void TryPublish(IrcNotification notification) => _queue.Writer.TryWrite(notification);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Remove(this);
            _queue.Writer.TryComplete();
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var notification in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }

                    try
                    {
                        _subscriber(notification);
                    }
                    catch
                    {
                        // A subscriber is an extension boundary; it cannot
                        // take down the session or another subscriber.
                    }
                }
            }
            catch (ChannelClosedException)
            {
            }
        }
    }
}
