namespace nexIRC.Application;

/// <summary>
/// Monotonic ownership token for interactive history-search result
/// publication. A completed worker may publish only while its generation is
/// still current; cancellation remains an optimization rather than the
/// correctness boundary.
/// </summary>
public sealed class HistorySearchGeneration
{
    private long _current;

    public long Begin() => Interlocked.Increment(ref _current);

    public bool IsCurrent(long generation) => generation > 0 && Volatile.Read(ref _current) == generation;

    public void Invalidate() => Interlocked.Increment(ref _current);
}
