using Velopack;

namespace CwcConfigurator;

/// <summary>
/// Entry point. Velopack must run first: during install, update and uninstall it starts the
/// exe with special arguments, handles them and exits before any window is created.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var application = new CwcApplication();
        application.InitializeComponent();
        application.Run();
    }
}
