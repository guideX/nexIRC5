using System.Windows;
using nexIRC.Core.Networking;
using nexIRC.Networking;
using nexIRC.Networking.Testing;

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

        var window = new MainWindow(transportFactory);
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
    }
}
