using System.Configuration;
using System.Data;
using System.Windows;
using System.Threading;

namespace RevenantWorkspaceWarden;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex = null;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string appName = "RevenantWorkspaceWardenCompanion";
        _mutex = new Mutex(true, appName, out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "An instance of Revenant Workspace Warden is already running in the background.\n\nPlease close the existing window, check your system tray, or close the ghost process in Task Manager.",
                "Warden Already Running",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
            Current.Shutdown();
            return;
        }

        base.OnStartup(e);
    }
}

