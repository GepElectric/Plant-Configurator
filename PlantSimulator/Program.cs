using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CwcConfigurator;
using CwcConfigurator.App;
using CwcConfigurator.Services.Simulation;

namespace PlantSimulator;

/// <summary>
/// Plant Simulator: PlantSimulator.exe [--project] [project.plantcfg]. Without a project it opens the project used
/// last time, or asks for one. Plant Configurator starts it from Tools → Simulator with the open project.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PlantConfigurator;component/Themes/SharedStyles.xaml")
        });
        application.DispatcherUnhandledException += (_, e) =>
        {
            CwcApplication.LogException("PlantSimulator.Unhandled", e.Exception);
            MessageBox.Show(e.Exception.Message, "Plant Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        application.Startup += (_, _) => Start(application, args);
        application.Run();
    }

    private static void Start(Application application, string[] args)
    {
        CwcApplication.InitializeLogging();
        CwcApplication.Log($"Started Plant Simulator. PID={Environment.ProcessId}.");
        var path = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
        if (path is null)
        {
            var last = SimulatorSettingsStore.LoadLastProject();
            path = last is not null && File.Exists(last) ? last : null;
        }

        try
        {
            var window = SimulatorWindow.CreateStandalone(path);
            if (window is null)
            {
                application.Shutdown();
                return;
            }

            window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/PlantSimulator;component/PlantSimulator.ico"));
            application.MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            CwcApplication.LogException("PlantSimulator.Start", ex);
            MessageBox.Show("Could not open the project: " + ex.Message, "Plant Simulator", MessageBoxButton.OK, MessageBoxImage.Error);
            application.Shutdown();
        }
    }
}
