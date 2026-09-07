using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Session;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1STests
{
    [Fact]
    public async Task SelectingAcrossNetworksDeactivatesThePreviousNetworkForUnreadClassification()
    {
        var factory = new FakeIrcTransportFactory();
        var transportA = new FakeIrcTransport(new IrcEndpoint("phase1s-alpha.example", 6667, false));
        var transportB = new FakeIrcTransport(new IrcEndpoint("phase1s-beta.example", 6667, false));
        factory.Add(transportA);
        factory.Add(transportB);
        await using var manager = new NetworkSessionManager(factory);
        var networkA = manager.Add(Options("Alpha", transportA.Endpoint, "alpha", "#room"));
        var networkB = manager.Add(Options("Beta", transportB.Endpoint, "beta", "#room"));

        await manager.ConnectAsync(networkA.Id);
        await manager.ConnectAsync(networkB.Id);
        await WaitForAsync(() => transportA.ConnectCount == 1 && transportB.ConnectCount == 1);
        Register(transportA, "a", "alpha");
        Register(transportB, "b", "beta");
        transportA.EnqueueInboundLine(":alpha!u@a JOIN #room");
        transportB.EnqueueInboundLine(":beta!u@b JOIN #room");
        await WaitForAsync(() => networkA.Channels.Single().IsJoined && networkB.Channels.Single().IsJoined);

        var alphaChannel = networkA.Channels.Single();
        var betaChannel = networkB.Channels.Single();
        manager.ActivateView(alphaChannel.Id);
        manager.ActivateView(betaChannel.Id);
        manager.ActivateView(alphaChannel.Id);

        Assert.True(alphaChannel.IsActive);
        Assert.False(betaChannel.IsActive);
        transportB.EnqueueInboundLine(":quiet!u@b PRIVMSG #room :phase1s unread after network switch");
        await WaitForAsync(() => betaChannel.UnreadCount == 1);

        Assert.Equal(WorkspaceActivity.Unread, betaChannel.Activity);
        Assert.DoesNotContain(alphaChannel.EntriesSnapshot, entry => entry.Text.Contains("phase1s unread", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AuthorityResidenceDiagnosticsExposePendingAgeSeparatelyFromCompletedSamples()
    {
        var inner = new GateDispatcher();
        var dispatcher = new SerializedWorkspaceDispatcher(inner);
        var first = dispatcher.InvokeAsync(static () => { }).AsTask();
        await inner.FirstCallbackStarted.Task;
        var second = dispatcher.InvokeAsync(static () => { }).AsTask();

        await WaitForAsync(() => dispatcher.Diagnostics.CurrentQueueDepth >= 1);
        await Task.Delay(25);
        var pendingDiagnostics = dispatcher.Diagnostics;

        Assert.True(pendingDiagnostics.CurrentOldestQueuedWorkAgeMilliseconds >= 10);
        Assert.Equal(0, pendingDiagnostics.ProcessedActions);

        inner.Release();
        await Task.WhenAll(first, second);
        await dispatcher.CompleteAsync();

        var completedDiagnostics = dispatcher.Diagnostics;
        Assert.Equal(2, completedDiagnostics.ProcessedActions);
        Assert.Equal(0, completedDiagnostics.CurrentQueueDepth);
        Assert.Equal(2, completedDiagnostics.RecentSamples!.Count);
        Assert.Contains(completedDiagnostics.RecentSamples, sample => sample.QueueWaitMilliseconds >= 10);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1S dispatcher condition did not complete.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class GateDispatcher : IWorkspaceDispatcher
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource FirstCallbackStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InvokeAsync(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            return new ValueTask(Task.Run(async () =>
            {
                FirstCallbackStarted.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                action();
            }));
        }

        public void Release() => _release.TrySetResult();
    }

    private static NetworkConnectionOptions Options(string displayName, IrcEndpoint endpoint, string nickname, string channel) => new()
    {
        DisplayName = displayName,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1S cross-network selection",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = new HashSet<string>(StringComparer.Ordinal) { channel },
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string server, string nickname)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome");
    }

}
