using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CwcConfigurator;

public partial class CwcApplication : Application
{
    private static readonly object LogLock = new();
    private static string? _logFilePath;
    private static BitmapFrame? _appIcon;

    public static string LogFilePath => _logFilePath ?? "";

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        InitializeLogFile();
        InitializeAppIcon();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogException("AppDomain.UnhandledException", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Log($"Started Plant Configurator. PID={Environment.ProcessId}.");
    }

    private static void InitializeAppIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/PlantConfigurator.ico"));
            if (stream is null)
            {
                return;
            }
            using var iconStream = stream.Stream;
            var decoder = new IconBitmapDecoder(iconStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            _appIcon = decoder.Frames[0];
            _appIcon.Freeze();

            EventManager.RegisterClassHandler(
                typeof(Window),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnWindowLoaded));
        }
        catch (Exception ex)
        {
            LogException("InitializeAppIcon", ex);
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window && window.Icon is null && _appIcon is not null)
        {
            window.Icon = _appIcon;
        }
    }

    private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException("Dispatcher.UnhandledException", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nDetails written to:\n{_logFilePath}",
            "Plant Configurator",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void InitializeLogFile()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PlantConfigurator",
                "logs");
            Directory.CreateDirectory(directory);
            var fileName = $"plantconfigurator-{DateTime.Now:yyyyMMdd}.log";
            _logFilePath = Path.Combine(directory, fileName);
        }
        catch
        {
            _logFilePath = null;
        }
    }

    public static void Log(string message)
    {
        if (string.IsNullOrEmpty(_logFilePath))
        {
            return;
        }

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff}  {1}{2}",
            DateTime.Now,
            message,
            Environment.NewLine);

        lock (LogLock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }

    public static void LogException(string source, Exception? exception)
    {
        if (exception is null)
        {
            Log($"{source}: (no exception object)");
            return;
        }

        var builder = new StringBuilder();
        builder.Append(source).Append(": ").AppendLine(exception.GetType().FullName);
        builder.AppendLine(exception.ToString());
        Log(builder.ToString());
    }
}
