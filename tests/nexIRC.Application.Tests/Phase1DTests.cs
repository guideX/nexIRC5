using System.Diagnostics;
using System.Threading;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking.Testing;

namespace nexIRC.Application.Tests;

public sealed class Phase1DTests
{
    [Fact]
    public void InputHistoryIsBoundedSkipsBlanksAndPreservesDraft()
    {
        var history = new InputHistory(2);
        history.Submit(" first ");
        history.Submit("first");
        history.Submit("second");
        history.Submit("third");
        history.Submit("   ");

        Assert.Equal(["second", "third"], history.EntriesSnapshot);
        var recalled = history.Navigate(InputHistoryDirection.Older, "draft");
        Assert.Equal("third", recalled);
        recalled = "edited";
        Assert.Equal(["second", "third"], history.EntriesSnapshot);
        Assert.Equal("second", history.Navigate(InputHistoryDirection.Older, recalled));
        Assert.Equal("second", history.Navigate(InputHistoryDirection.Older, "ignored at boundary"));
        Assert.Equal("third", history.Navigate(InputHistoryDirection.Newer, "ignored"));
        Assert.Equal("draft", history.Navigate(InputHistoryDirection.Newer, "ignored"));
        Assert.Equal("ignored", history.Navigate(InputHistoryDirection.Newer, "ignored"));
    }

    [Fact]
    public async Task CompletionUsesDispatcherCommandsAndCyclesChannelMembers()
    {
        var commandEngine = new CompletionEngine();
        var command = commandEngine.Complete("/jo", 3, null, null);
        Assert.Equal("/join", command.Text);
        Assert.Equal(5, command.CaretIndex);

        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("completion.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Completion", transport.Endpoint, "alice", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "alice", "005 alice PREFIX=(qaohv)~&@%+ CASEMAPPING=ascii");
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        transport.EnqueueInboundLine(":srv 353 alice = #room :@Alice +Alfred");
        transport.EnqueueInboundLine(":srv 366 alice #room :End");
        await WaitForAsync(() => network.Channels.Single().MembersSnapshot.Count == 2);

        var channel = network.Channels.Single();
        var nicknameEngine = new CompletionEngine();
        var first = nicknameEngine.Complete("Al", 2, network, channel);
        var second = nicknameEngine.Complete(first.Text, first.CaretIndex, network, channel);
        Assert.Equal("Alfred: ", first.Text);
        Assert.Equal("Alice: ", second.Text);
        var middle = nicknameEngine.Complete("hello Al", 8, network, channel);
        Assert.Equal("hello Alfred", middle.Text);
    }

    [Fact]
    public async Task CompletionNeverLeaksCandidatesAcrossNetworksWithEqualChannelNames()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("first.example", 6667, false));
        var second = new FakeIrcTransport(new IrcEndpoint("second.example", 6667, false));
        factory.Add(first);
        factory.Add(second);
        await using var manager = new NetworkSessionManager(factory);
        var networkA = manager.Add(Options("First", first.Endpoint, "alice", "#room"));
        var networkB = manager.Add(Options("Second", second.Endpoint, "alice", "#room"));
        await manager.ConnectAsync(networkA.Id);
        await manager.ConnectAsync(networkB.Id);
        await WaitForAsync(() => first.ConnectCount == 1 && second.ConnectCount == 1);
        Register(first, "a", "alice");
        Register(second, "b", "alice");
        first.EnqueueInboundLine(":alice!u@a JOIN #room");
        first.EnqueueInboundLine(":a 353 alice = #room :@Alice");
        first.EnqueueInboundLine(":a 366 alice #room :End");
        second.EnqueueInboundLine(":alice!u@b JOIN #room");
        second.EnqueueInboundLine(":b 353 alice = #room :+Bob");
        second.EnqueueInboundLine(":b 366 alice #room :End");
        await WaitForAsync(() => networkA.Channels.Single().MembersSnapshot.Count == 1 && networkB.Channels.Single().MembersSnapshot.Count == 1);

        var engine = new CompletionEngine();
        var result = engine.Complete("A", 1, networkB, networkB.Channels.Single());
        Assert.False(result.Completed);
        Assert.Equal("A", result.Text);
        Assert.Equal("Alice", engine.Complete("A", 1, networkA, networkA.Channels.Single()).Candidate);
    }

    [Fact]
    public async Task WhoisAndListAreDedicatedAndRemainNetworkScoped()
    {
        var factory = new FakeIrcTransportFactory();
        var first = new FakeIrcTransport(new IrcEndpoint("whois-a.example", 6667, false));
        var second = new FakeIrcTransport(new IrcEndpoint("whois-b.example", 6667, false));
        factory.Add(first);
        factory.Add(second);
        await using var manager = new NetworkSessionManager(factory);
        var networkA = manager.Add(Options("Alpha", first.Endpoint, "alice"));
        var networkB = manager.Add(Options("Beta", second.Endpoint, "alice"));
        await manager.ConnectAsync(networkA.Id);
        await manager.ConnectAsync(networkB.Id);
        await WaitForAsync(() => first.ConnectCount == 1 && second.ConnectCount == 1);
        Register(first, "srv-a", "alice");
        Register(second, "srv-b", "alice");
        await WaitForAsync(() => networkA.State == NetworkDisplayState.Registered && networkB.State == NetworkDisplayState.Registered);

        var dispatcher = new IrcCommandDispatcher(manager);
        var whoisA = await dispatcher.DispatchAsync(networkA, networkA.StatusView, "/whois Mira");
        var whoisB = await dispatcher.DispatchAsync(networkB, networkB.StatusView, "/whois Mira");
        var listA = await dispatcher.DispatchAsync(networkA, networkA.StatusView, "/list");
        Assert.IsType<WhoisView>(whoisA.View);
        Assert.IsType<WhoisView>(whoisB.View);
        Assert.IsType<ChannelListView>(listA.View);

        first.EnqueueInboundLine(":srv-a 311 alice Mira mira alpha.host * :Alpha Mira");
        first.EnqueueInboundLine(":srv-a 312 alice Mira irc.alpha :Alpha server");
        first.EnqueueInboundLine(":srv-a 319 alice Mira :@#alpha +#shared");
        first.EnqueueInboundLine(":srv-a 330 alice Mira alpha-account :is logged in as");
        first.EnqueueInboundLine(":srv-a 338 alice Mira :is using a secure connection");
        first.EnqueueInboundLine(":srv-a 318 alice Mira :End");
        second.EnqueueInboundLine(":srv-b 311 alice Mira mira beta.host * :Beta Mira");
        second.EnqueueInboundLine(":srv-b 318 alice Mira :End");
        first.EnqueueInboundLine(":srv-a 321 alice Channel :Users Name");
        first.EnqueueInboundLine(":srv-a 322 alice #alpha 11 :Alpha topic");
        first.EnqueueInboundLine(":srv-a 322 alice #shared 3 :Shared topic");
        first.EnqueueInboundLine(":srv-a 323 alice :End");

        await WaitForAsync(() => ((WhoisView)whoisA.View!).IsCompleted
            && ((WhoisView)whoisB.View!).IsCompleted
            && ((ChannelListView)listA.View!).IsCompleted);
        var resultA = ((WhoisView)whoisA.View!).Result;
        var resultB = ((WhoisView)whoisB.View!).Result;
        Assert.Equal("alpha.host", resultA.Hostname);
        Assert.Equal("beta.host", resultB.Hostname);
        Assert.Equal("alpha-account", resultA.Account);
        Assert.True(resultA.IsSecure);
        Assert.Contains("@#alpha", resultA.Channels);
        Assert.Equal(["#alpha", "#shared"], ((ChannelListView)listA.View!).Result.RowsSnapshot.Select(row => row.Channel));
        Assert.DoesNotContain(resultA.Channels, channel => channel.Contains("beta", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ChannelListResultIsSortedRepeatableAndBounded()
    {
        var result = new ChannelListResult(2);
        result.Begin();
        result.Apply(new IrcListItemEvent(Message(":s 322 me #z 1 :z"), "#z", 1, "z"));
        result.Apply(new IrcListItemEvent(Message(":s 322 me #a 2 :a"), "#a", 2, "a"));
        result.Apply(new IrcListItemEvent(Message(":s 322 me #m 3 :m"), "#m", 3, "m"));
        result.Complete();
        Assert.Equal(["#a", "#z"], result.RowsSnapshot.Select(row => row.Channel));
        Assert.True(result.WasTruncated);
        Assert.Equal(RichResultState.Completed, result.State);

        result.Begin();
        result.Apply(new IrcListItemEvent(Message(":s 322 me #fresh 4 :fresh"), "#fresh", 4, "fresh"));
        Assert.Equal(["#fresh"], result.RowsSnapshot.Select(row => row.Channel));
        Assert.True(result.IsLoading);
    }

    [Fact]
    public void HighlightPolicyUsesBoundariesCaseMappingAndActiveState()
    {
        var policy = new HighlightActivityPolicy { CustomWords = ["release"] };
        Assert.True(policy.IsHighlight("ANN, the release is ready.", "Ann", nexIRC.Core.State.IrcCaseMapping.Ascii));
        Assert.False(policy.IsHighlight("announcement is not a nickname mention", "Ann", nexIRC.Core.State.IrcCaseMapping.Ascii));
        Assert.True(policy.IsHighlight("the RELEASE is ready", "Ann", nexIRC.Core.State.IrcCaseMapping.Ascii));
        policy.HighlightCustomWords = false;
        Assert.False(policy.IsHighlight("the release is ready", "Ann", nexIRC.Core.State.IrcCaseMapping.Ascii));
        Assert.True(policy.IsHighlight("newNick: hello", "newNick", nexIRC.Core.State.IrcCaseMapping.Ascii));
        Assert.False(policy.IsHighlight("Ann: hello", "newNick", nexIRC.Core.State.IrcCaseMapping.Ascii));
        Assert.Equal(WorkspaceActivity.Important, policy.Classify(new TestView(active: false), new IrcQueryMessageEvent(Message(":m!u@h PRIVMSG me :hello"), "Mira", "hello", false), EmptySnapshot()));
        Assert.Equal(WorkspaceActivity.None, policy.Classify(new TestView(active: true), new IrcPrivmsgEvent(Message(":m!u@h PRIVMSG #room :hello"), "#room", "hello", false), EmptySnapshot()));
    }

    [Fact]
    public void WhoisKeepsOptionalAndUnknownNumericInformationWithoutFailing()
    {
        var result = new WhoisResult("Mira");
        result.Begin();
        result.Apply(new IrcWhoisEvent(Message(":s 311 me Mira user host * :Real name"), 311, "Mira", Message(":s 311 me Mira user host * :Real name").Parameters, "Real name"));
        result.Apply(new IrcWhoisEvent(Message(":s 350 me Mira :vendor-specific detail"), 350, "Mira", Message(":s 350 me Mira :vendor-specific detail").Parameters, "vendor-specific detail"));
        result.Apply(new IrcWhoisEvent(Message(":s 318 me Mira :End"), 318, "Mira", Message(":s 318 me Mira :End").Parameters, "End"));

        Assert.Equal(RichResultState.Completed, result.State);
        Assert.Equal("Real name", result.RealName);
        Assert.Contains(result.AdditionalFields, field => field.Numeric == 350 && field.Text == "vendor-specific detail");
    }

    [Fact]
    public async Task NotificationSubscribersAreIsolatedAndPublicationDoesNotWait()
    {
        using var notifications = new NotificationSubscriptionService();
        using var slow = notifications.Subscribe(_ => Thread.Sleep(200));
        var received = new TaskCompletionSource<IrcNotification>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var healthy = notifications.Subscribe(item => received.TrySetResult(item));
        using var failing = notifications.Subscribe(_ => throw new InvalidOperationException("subscriber failure"));
        var notification = new IrcNotification(Guid.NewGuid(), Guid.NewGuid(), WorkspaceViewKind.Channel, IrcNotificationType.Highlight, WorkspaceActivity.Important, "Mira", "hello", DateTimeOffset.UtcNow, false, "IrcPrivmsgEvent");

        var stopwatch = Stopwatch.StartNew();
        notifications.Publish(notification);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
        Assert.Same(notification, await received.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task OutgoingMessagesUseTypedLocalEchoSemantics()
    {
        var factory = new FakeIrcTransportFactory();
        var transport = new FakeIrcTransport(new IrcEndpoint("outgoing.example", 6667, false));
        factory.Add(transport);
        await using var manager = new NetworkSessionManager(factory);
        var network = manager.Add(Options("Outgoing", transport.Endpoint, "alice", "#room"));
        await manager.ConnectAsync(network.Id);
        await WaitForAsync(() => transport.ConnectCount == 1);
        Register(transport, "srv", "alice");
        await WaitForAsync(() => network.State == NetworkDisplayState.Registered);
        transport.EnqueueInboundLine(":alice!u@h JOIN #room");
        await WaitForAsync(() => network.Channels.Count == 1);
        var dispatcher = new IrcCommandDispatcher(manager);
        var channel = network.Channels.Single();
        await dispatcher.DispatchAsync(network, channel, "hello channel");
        await dispatcher.DispatchAsync(network, channel, "/me waves");
        var entries = channel.EntriesSnapshot;
        Assert.Contains(entries, entry => entry.Kind == TranscriptEntryKind.OutgoingMessage && entry.Text == "hello channel");
        Assert.Contains(entries, entry => entry.Kind == TranscriptEntryKind.OutgoingAction && entry.Text == "waves");
        Assert.Contains(entries, entry => entry.DisplayLine == "* alice waves");
        Assert.DoesNotContain(entries, entry => entry.Text.Contains("ACTION", StringComparison.Ordinal));
    }

    private static NetworkConnectionOptions Options(string name, IrcEndpoint endpoint, string nickname, params string[] desiredChannels) => new()
    {
        DisplayName = name,
        Endpoint = endpoint,
        Nickname = nickname,
        Username = nickname,
        RealName = "Phase 1D test",
        RequestedCapabilities = Array.Empty<string>(),
        DesiredChannels = desiredChannels.ToHashSet(StringComparer.Ordinal),
        Reconnect = new ReconnectPolicy(Enabled: false)
    };

    private static void Register(FakeIrcTransport transport, string server, string nickname, string? isupport = null)
    {
        transport.EnqueueInboundLine($":{server} CAP * LS :");
        if (isupport is not null)
        {
            transport.EnqueueInboundLine($":{server} {isupport}");
        }

        transport.EnqueueInboundLine($":{server} 001 {nickname} :Welcome to nexIRC test network");
    }

    private static IrcMessage Message(string raw) => IrcMessageParser.Parse(raw).Message!;

    private static ServerSessionSnapshot EmptySnapshot() => new(
        ServerSessionState.Disconnected,
        RegistrationState.NotStarted,
        "Ann",
        "user",
        "real",
        new IrcEndpoint("empty.example", 6667, false),
        0,
        CapabilitySnapshot.Empty,
        ISupportSnapshot.Empty,
        ServerIdentity.Unknown,
        ServerFeatureSet.Build(CapabilitySnapshot.Empty, ISupportSnapshot.Empty, null, ServerIdentity.Unknown),
        null,
        Array.Empty<IrcChannelSnapshot>(),
        Array.Empty<IrcQuerySnapshot>(),
        new HashSet<string>());

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMilliseconds = 4_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The Phase 1D test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class TestView : WorkspaceView
    {
        public TestView(bool active)
            : base(Guid.NewGuid(), Guid.NewGuid(), WorkspaceViewKind.Channel, "#test")
        {
            if (active)
            {
                Activate();
            }
        }
    }
}
