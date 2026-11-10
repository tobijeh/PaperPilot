using System.Windows;
using System.Windows.Threading;
using PaperPilot.Diagnostics;

namespace PaperPilot;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize();
        Log.Info($"Paper Pilot {Core.Updater.CurrentVersion} starting on {Environment.OSVersion}.");
        Core.Updater.OnStartup(e.Args);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        _ = Core.WindowCapture.RequestBorderlessAccessAsync();
        Controller = new Core.AppController();

        // Until the tray icon arrives (Milestone 5) the debug window is the app's main window.
        var window = new DebugWindow(Controller);
        MainWindow = window;
        window.Show();
    }

    public Core.AppController? Controller { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        Controller?.Dispose();
        Log.Info("Paper Pilot exiting.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }
}
