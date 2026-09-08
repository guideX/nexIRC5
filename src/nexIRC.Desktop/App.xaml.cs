using System.Windows;
using System.Diagnostics;
using System.IO;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Core.Protocol;
using nexIRC.Core.Session;
using nexIRC.Core.State;
using nexIRC.Networking;
using nexIRC.Networking.Testing;
using MessageBox = System.Windows.MessageBox;

namespace nexIRC.Desktop;

public partial class App : System.Windows.Application
{
    private string? _demoHistoryRoot;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var demo = e.Args.Any(argument => string.Equals(argument, "--demo", StringComparison.OrdinalIgnoreCase));
        var liveSmoke = e.Args.Any(argument => string.Equals(argument, "--live-smoke", StringComparison.OrdinalIgnoreCase));
        var smokeScenario = ReadSmokeScenario(e.Args);
        if (smokeScenario is not null && !demo)
        {
            Console.Error.WriteLine("FAIL_UI_SMOKE " + smokeScenario + ": UI smoke requires --demo so no real network session can be used.");
            Environment.ExitCode = 2;
            Shutdown(2);
            return;
        }

        if (smokeScenario is not null && !UiSmokeHarness.IsKnownScenario(smokeScenario))
        {
            Console.Error.WriteLine("FAIL_UI_SMOKE " + smokeScenario + ": unknown scenario. Expected the existing smoke scenarios, history-search, stale-search, index-recovery, ircv3-metadata, contextual-actions, sustained-interactivity, or close-idle/close-sustained/close-backlog/close-reconnect/close-partial/close-registered/close-persistence/close-interacted.");
            Environment.ExitCode = 2;
            Shutdown(2);
            return;
        }

        IIrcTransportFactory transportFactory;
        DemoScenario? demoScenario = null;
        if (demo)
        {
            transportFactory = DemoScenario.CreateFactory(out demoScenario);
        }
        else
        {
            transportFactory = new TcpTlsIrcTransportFactory();
        }

        ConfigurationService? configuration;
        ProfileCredentialService credentials;
        IConversationLogStore logStore;
        ConfigurationLoadResult? loadResult = null;
        if (demo || liveSmoke)
        {
            configuration = new ConfigurationService(new InMemoryConfigurationStore(new NexIrcConfiguration
            {
                Preferences = new ApplicationPreferences
                {
                    NotificationsEnabled = true,
                    ConversationLoggingEnabled = true,
                    PrivateMessageLoggingEnabled = true,
                    StatusLoggingEnabled = true
                }
            }));
            credentials = new ProfileCredentialService(new InMemoryProfileCredentialStore());
            _demoHistoryRoot = Directory.CreateTempSubdirectory(liveSmoke ? "nexirc5-live-history-" : "nexirc5-demo-history-").FullName;
            logStore = new JsonlConversationLogStore(_demoHistoryRoot, maximumSegmentBytes: 32 * 1024);
            await configuration.LoadAsync().ConfigureAwait(true);
        }
        else
        {
            configuration = new ConfigurationService(new JsonConfigurationStore(ConfigurationPaths.GetDefaultPath()));
            loadResult = await configuration.LoadAsync().ConfigureAwait(true);
            credentials = new ProfileCredentialService(new WindowsCredentialStore());
            logStore = new JsonlConversationLogStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "nexIRC",
                "logs"));
        }

        var window = new MainWindow(transportFactory, configuration, credentials, logStore);
        MainWindow = window;
        window.Show();
        _ = CleanupLogsAsync(logStore, configuration);
        if (demo && demoScenario is not null && smokeScenario is null)
        {
            try
            {
                await demoScenario.SeedAsync(window.ViewModel);
            }
            catch (Exception exception)
            {
                MessageBox.Show(window, exception.Message, "Demo mode", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (!demo && !liveSmoke)
        {
            if (loadResult?.Diagnostic is { } diagnostic && loadResult.UsedDefaults)
            {
                window.ViewModel.StatusText = diagnostic;
            }

            await window.ViewModel.RestoreProfilesAsync().ConfigureAwait(true);
        }

        if (liveSmoke)
        {
            _ = RunLiveSmokeAsync(window);
        }

        if (smokeScenario is not null && demoScenario is not null)
        {
            try
            {
                await UiSmokeHarness.RunAsync(smokeScenario, window, demoScenario).ConfigureAwait(true);
                if (!UiSmokeHarness.IsExternalCloseScenario(smokeScenario))
                {
                    await window.CloseAfterSmokeAsync().ConfigureAwait(true);
                }

                Environment.ExitCode = 0;
                Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL_UI_SMOKE {smokeScenario}: {exception.Message}");
                if (!UiSmokeHarness.IsExternalCloseScenario(smokeScenario))
                {
                    try
                    {
                        await window.CloseAfterSmokeAsync().ConfigureAwait(true);
                    }
                    catch
                    {
                    }
                }

                Environment.ExitCode = 1;
                Shutdown(1);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_demoHistoryRoot is { } historyRoot)
        {
            try
            {
                if (Directory.Exists(historyRoot)) Directory.Delete(historyRoot, recursive: true);
            }
            catch
            {
                // Demo history is disposable and cannot affect normal shutdown.
            }
        }

        base.OnExit(e);
    }

    private static async Task CleanupLogsAsync(IConversationLogStore logStore, ConfigurationService configuration)
    {
        try
        {
            await logStore.CleanupAsync(DateTimeOffset.UtcNow.AddDays(-configuration.Preferences.LogRetentionDays)).ConfigureAwait(false);
        }
        catch
        {
            // Retention is maintenance; it cannot prevent the client from launching.
        }
    }

    private static string? ReadSmokeScenario(string[] args)
    {
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (string.Equals(args[index], "--ui-smoke", StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1].Trim();
            }
        }

        return null;
    }

    private static async Task RunLiveSmokeAsync(MainWindow window)
    {
        try
        {
            var viewModel = window.ViewModel;
            var nickname = $"nex5{Environment.ProcessId % 100_000:00000}";
            var network = viewModel.Sessions.Add(new NetworkConnectionOptions
            {
                DisplayName = "Libera Phase 1X",
                Endpoint = new IrcEndpoint("irc.libera.chat", 6697, true),
                Nickname = nickname,
                Username = nickname,
                RealName = "nexIRC 5 Phase 1X live IRCv3 smoke",
                RequestedCapabilities = IrcCapabilityCatalog.PreferredPhase1X,
                DesiredChannels = new HashSet<string>(new[] { "#libera" }, StringComparer.Ordinal),
                Reconnect = new ReconnectPolicy(Enabled: false)
            });
            viewModel.SelectView(network.StatusView);

            var states = new List<ServerSessionState>();
            var whois318 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var serverTimeObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(object? sender, SessionStateChangedEvent change)
            {
                if (!states.Contains(change.Current))
                {
                    states.Add(change.Current);
                }
            }

            void OnSemanticEvent(object? sender, SessionSemanticEvent item)
            {
                if (item.Event is IrcWhoisEvent whois)
                {
                    Console.WriteLine($"LIVE_WPF_TRACE whois_numeric={whois.Numeric} nickname={whois.Nickname} label={(whois.RequestLabel is null ? "none" : "present")}");
                    if (whois.Numeric == 318)
                    {
                        whois318.TrySetResult();
                    }
                }

                if (item.Event.Message.ServerTimestamp is not null)
                {
                    serverTimeObserved.TrySetResult();
                }
            }

            network.Session.StateChanged += OnStateChanged;
            network.Session.SemanticEventReceived += OnSemanticEvent;
            await viewModel.Sessions.ConnectAsync(network.Id).ConfigureAwait(true);
            await WaitForLiveConditionAsync(
                () => network.State == NetworkDisplayState.Registered,
                "Libera registration did not reach 001").ConfigureAwait(true);
            await WaitForLiveConditionAsync(
                () => network.Channels.Any(channel => channel.Channel == "#libera" && channel.IsJoined && channel.Synchronization == ChannelSynchronizationState.Synchronized),
                "Libera #libera JOIN/NAMES did not converge").ConfigureAwait(true);
            var liveChannel = network.Channels.Single(channel => channel.Channel == "#libera");
            var liveMember = liveChannel.Members.FirstOrDefault(member => string.Equals(member.Nickname, nickname, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(
                $"LIVE_WPF_TRACE dns=true tcp=true tls=true registration=001 join=true names=synchronized capabilities={string.Join(',', network.Snapshot.Capabilities.Enabled.OrderBy(static capability => capability, StringComparer.Ordinal))} server_time_seen={serverTimeObserved.Task.IsCompleted} extended_join={(liveMember?.Account is not null || liveMember?.RealName is not null)} multi_prefix={(liveMember?.PrefixModes.Count > 1)}");

            var whois = await viewModel.Sessions.RequestWhoisAsync(network.Id, nickname).ConfigureAwait(true);
            await whois318.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(true);
            Console.WriteLine($"LIVE_WPF_TRACE whois_318_observed=true view_completed={(whois.View is WhoisView whoisResult && whoisResult.IsCompleted)}");
            await WaitForLiveConditionAsync(
                () => whois.View is WhoisView { IsCompleted: true },
                "Libera self-WHOIS did not complete at 318").ConfigureAwait(true);
            Console.WriteLine("LIVE_WPF_TRACE whois=318");

            var preCloseSnapshot = network.Session.Snapshot;
            Console.WriteLine($"LIVE_WPF_TRACE preclose_state={preCloseSnapshot.State} preclose_registration={preCloseSnapshot.Registration}");
            var closeStart = Stopwatch.GetTimestamp();
            void OnWindowClosed(object? sender, EventArgs args)
            {
                var quit = network.Session.QuitWritten.Wait(TimeSpan.FromSeconds(2));
                Console.WriteLine(
                    $"LIVE_WPF_RESULT states={string.Join(',', states)} quit={quit} disconnected={network.Session.Snapshot.State == ServerSessionState.Disconnected} natural_window_close=true close_request_to_window_closed_ms={(Stopwatch.GetTimestamp() - closeStart) * 1000d / Stopwatch.Frequency:F3}");
                Environment.ExitCode = quit && network.Session.Snapshot.State == ServerSessionState.Disconnected ? 0 : 1;
                window.Closed -= OnWindowClosed;
                network.Session.StateChanged -= OnStateChanged;
                network.Session.SemanticEventReceived -= OnSemanticEvent;
            }

            window.Closed += OnWindowClosed;
            Console.WriteLine("LIVE_WPF_TRACE close_origin=MainWindow.Close");
            window.Close();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL_LIVE_WPF {exception.Message}");
            Environment.ExitCode = 1;
            if (window.IsVisible)
            {
                window.Close();
            }
        }
    }

    private static async Task WaitForLiveConditionAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(failure);
            }

            await Task.Delay(50).ConfigureAwait(true);
        }
    }
}
