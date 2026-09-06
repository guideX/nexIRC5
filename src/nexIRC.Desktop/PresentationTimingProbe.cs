using System.Diagnostics;

namespace nexIRC.Desktop;

/// <summary>
/// Records the next WPF composition callback after an explicitly marked
/// interaction. It measures a presentation opportunity, not physical pixel
/// delivery to a monitor.
/// </summary>
internal sealed class PresentationTimingProbe
{
    private readonly object _gate = new();
    private long _nextInteractionId;
    private PendingInteraction? _pending;
    private TaskCompletionSource<PresentationTimingSample?>? _completion;
    private PresentationTimingSample? _lastSample;

    public PresentationTimingSample? LastSample
    {
        get
        {
            lock (_gate)
            {
                return _lastSample;
            }
        }
    }

    public long BeginInteraction()
    {
        lock (_gate)
        {
            var interactionId = ++_nextInteractionId;
            _pending = new PendingInteraction(interactionId, Stopwatch.GetTimestamp(), 0);
            _completion = new TaskCompletionSource<PresentationTimingSample?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return interactionId;
        }
    }

    public void MarkWpfStateChanged(long interactionId)
    {
        lock (_gate)
        {
            if (_pending is { } pending && pending.InteractionId == interactionId)
            {
                _pending = pending with { WpfStateChangedTimestamp = Stopwatch.GetTimestamp() };
            }
        }
    }

    public async Task<PresentationTimingSample?> WaitForNextOpportunityAsync(TimeSpan timeout)
    {
        Task<PresentationTimingSample?> completion;
        lock (_gate)
        {
            if (_pending is null)
            {
                return _lastSample;
            }

            completion = _completion!.Task;
        }

        try
        {
            return await completion.WaitAsync(timeout).ConfigureAwait(true);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public void RecordRendering(long renderingTimestamp)
    {
        lock (_gate)
        {
            if (_pending is not { WpfStateChangedTimestamp: > 0 } pending)
            {
                return;
            }

            var sample = new PresentationTimingSample(
                pending.InteractionId,
                pending.InteractionTimestamp,
                pending.WpfStateChangedTimestamp,
                renderingTimestamp,
                TicksToMilliseconds(pending.InteractionTimestamp, renderingTimestamp),
                TicksToMilliseconds(pending.InteractionTimestamp, pending.WpfStateChangedTimestamp),
                TicksToMilliseconds(pending.WpfStateChangedTimestamp, renderingTimestamp));
            _lastSample = sample;
            _pending = null;
            _completion?.TrySetResult(sample);
            _completion = null;
        }
    }

    private static double TicksToMilliseconds(long start, long end) =>
        Math.Max(0, end - start) * 1000d / Stopwatch.Frequency;

    private sealed record PendingInteraction(long InteractionId, long InteractionTimestamp, long WpfStateChangedTimestamp);
}

internal sealed record PresentationTimingSample(
    long InteractionId,
    long InteractionTimestamp,
    long WpfStateChangedTimestamp,
    long PresentationOpportunityTimestamp,
    double InteractionToOpportunityMilliseconds,
    double InteractionToWpfStateMilliseconds,
    double WpfStateToPresentationOpportunityMilliseconds);
