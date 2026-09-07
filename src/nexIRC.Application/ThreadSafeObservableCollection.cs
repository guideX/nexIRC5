using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace nexIRC.Application;

/// <summary>
/// ObservableCollection-compatible projection collection with snapshot
/// enumeration. Application mutations still happen on the serialized/UI
/// boundary; snapshot enumeration also keeps diagnostic/test readers from
/// observing a half-applied collection while a projection is being rebuilt.
/// </summary>
/// <summary>
/// Defers collection notifications while one bounded UI projection slice is
/// applying several already-ordered state actions. Values are still changed
/// immediately; only equivalent binding invalidations are reduced to one
/// reset per collection at the end of the slice.
/// </summary>
public static class WorkspaceProjectionBatch
{
    [ThreadStatic]
    private static int _depth;

    [ThreadStatic]
    private static List<IProjectionBatchParticipant>? _participants;

    public static bool IsActive => _depth > 0;

    public static IDisposable Begin()
    {
        _depth++;
        return new Scope();
    }

    internal static void Register(IProjectionBatchParticipant participant)
    {
        if (_depth > 0 && !(_participants?.Contains(participant) ?? false))
        {
            (_participants ??= []).Add(participant);
        }
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (--_depth != 0)
            {
                return;
            }

            var participants = _participants;
            _participants = null;
            if (participants is not null)
            {
                foreach (var participant in participants)
                {
                    participant.FlushProjectionNotifications();
                }
            }
        }
    }
}

internal interface IProjectionBatchParticipant
{
    void FlushProjectionNotifications();
}

public sealed class ThreadSafeObservableCollection<T> : ObservableCollection<T>, IEnumerable<T>, IProjectionBatchParticipant
{
    private readonly object _gate = new();
    private bool _notificationsDeferred;

    public new int Count
    {
        get
        {
            lock (_gate)
            {
                return base.Count;
            }
        }
    }

    public new T this[int index]
    {
        get
        {
            lock (_gate)
            {
                return base[index];
            }
        }
        set
        {
            lock (_gate)
            {
                base[index] = value;
            }
        }
    }

    protected override void InsertItem(int index, T item)
    {
        lock (_gate)
        {
            base.InsertItem(index, item);
        }
    }

    protected override void RemoveItem(int index)
    {
        lock (_gate)
        {
            base.RemoveItem(index);
        }
    }

    protected override void SetItem(int index, T item)
    {
        lock (_gate)
        {
            base.SetItem(index, item);
        }
    }

    protected override void ClearItems()
    {
        lock (_gate)
        {
            base.ClearItems();
        }
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (WorkspaceProjectionBatch.IsActive)
        {
            _notificationsDeferred = true;
            WorkspaceProjectionBatch.Register(this);
            return;
        }

        base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (WorkspaceProjectionBatch.IsActive
            && (string.Equals(e.PropertyName, nameof(Count), StringComparison.Ordinal)
                || string.Equals(e.PropertyName, "Item[]", StringComparison.Ordinal)))
        {
            _notificationsDeferred = true;
            WorkspaceProjectionBatch.Register(this);
            return;
        }

        base.OnPropertyChanged(e);
    }

    public new IEnumerator<T> GetEnumerator() => Snapshot().AsEnumerable().GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private T[] Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new T[base.Count];
            for (var index = 0; index < snapshot.Length; index++)
            {
                snapshot[index] = base[index];
            }

            return snapshot;
        }
    }

    void IProjectionBatchParticipant.FlushProjectionNotifications()
    {
        if (!_notificationsDeferred)
        {
            return;
        }

        _notificationsDeferred = false;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        // WPF ItemsControl generators do not consistently consume a range
        // Add/Remove event when several ordered mutations occurred in one
        // projection slice. A single Reset is both correct for add-only
        // batches and safe for mixed transcript trim/rebuild operations.
        base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
