using System.Windows;
using nexIRC.Application;
using nexIRC.Core.Networking;
using nexIRC.Networking;
using nexIRC.Networking.Testing;
using MessageBox = System.Windows.MessageBox;

namespace nexIRC.Desktop;

public partial class App : System.Windows.Application
{
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
        ConfigurationLoadResult? loadResult = null;
        if (demo)
        {
            configuration = new ConfigurationService(new InMemoryConfigurationStore(new NexIrcConfiguration
            {
                Preferences = new ApplicationPreferences { NotificationsEnabled = true }
            }));
        }
        else
        {
            configuration = new ConfigurationService(new JsonConfigurationStore(ConfigurationPaths.GetDefaultPath()));
            loadResult = await configuration.LoadAsync().ConfigureAwait(true);
        }

        var window = new MainWindow(transportFactory, configuration);
        MainWindow = window;
        window.Show();
        if (demo && demoScenario is not null)
        {
            try
            {
                await demoScenario.SeedAsync(window.ViewModel.Sessions);
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
}
