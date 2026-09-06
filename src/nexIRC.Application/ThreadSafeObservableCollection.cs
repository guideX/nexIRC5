using System.Collections;
using System.Collections.ObjectModel;

namespace nexIRC.Application;

/// <summary>
/// ObservableCollection-compatible projection collection with snapshot
/// enumeration. Application mutations still happen on the serialized/UI
/// boundary; snapshot enumeration also keeps diagnostic/test readers from
/// observing a half-applied collection while a projection is being rebuilt.
/// </summary>
public sealed class ThreadSafeObservableCollection<T> : ObservableCollection<T>, IEnumerable<T>
{
    private readonly object _gate = new();

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
}
