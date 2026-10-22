using System.Windows;
using CwcConfigurator.App;
using Velopack;

namespace CwcConfigurator;

/// <summary>
/// Entry point. Velopack must run first: during install, update and uninstall it starts the
/// exe with special arguments, handles them and exits before any window is created.
/// <c>--simulator [project.plantcfg]</c> starts only the simulator window instead of the main window.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var application = new CwcApplication();
        application.InitializeComponent();
        var simulator = args.Length > 0 && args[0].Equals(SimulatorWindow.CommandLineSwitch, StringComparison.OrdinalIgnoreCase);
        // Runs after CwcApplication.Application_Startup (logging, icon), which InitializeComponent registered first.
        application.Startup += (_, _) =>
        {
            if (simulator)
            {
                ShowSimulator(application, args.Length > 1 ? args[1] : null);
            }
            else
            {
                new MainWindow().Show();
            }
        };
        application.Run();
    }

    private static void ShowSimulator(CwcApplication application, string? projectPath)
    {
        try
        {
            var window = SimulatorWindow.CreateStandalone(projectPath);
            if (window is null)
            {
                application.Shutdown();
                return;
            }

            window.Show();
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("Simulator.Standalone", ex);
            MessageBox.Show("Could not open the project: " + ex.Message, "Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
            application.Shutdown();
        }
    }
}
