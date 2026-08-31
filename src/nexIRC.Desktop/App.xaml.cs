using System.Windows;
using System.IO;
using nexIRC.Application;
using nexIRC.Core.Networking;
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
        if (demo)
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
            _demoHistoryRoot = Directory.CreateTempSubdirectory("nexirc5-demo-history-").FullName;
            logStore = new JsonlConversationLogStore(_demoHistoryRoot, maximumSegmentBytes: 32 * 1024);
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
        if (demo && demoScenario is not null)
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
        else
        {
            if (loadResult?.Diagnostic is { } diagnostic && loadResult.UsedDefaults)
            {
                window.ViewModel.StatusText = diagnostic;
            }

            await window.ViewModel.RestoreProfilesAsync().ConfigureAwait(true);
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
}
