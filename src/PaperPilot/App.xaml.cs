using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PaperPilot.Core;
using PaperPilot.Diagnostics;
using PaperPilot.UI;

namespace PaperPilot;

/// <summary>
/// Paper Pilot lives in the tray. Settings opens on the very first launch, from the tray, or when
/// you start Paper Pilot again while it's already running. The bubble is the only thing on
/// screen while it's on.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "Local\\PaperPilot.SingleInstance";
    private const string ShowSettingsSignal = "Local\\PaperPilot.ShowSettings";

    private Mutex? _instance;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DebugWindow? _debugWindow;

    public AppController? Controller { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Initialize();
        Log.Info($"Paper Pilot {Updater.CurrentVersion} starting on {Environment.OSVersion}.");
        Updater.OnStartup(e.Args); // after an update: wait for the old copy to exit first

        // One copy at a time. Starting it again just opens Settings in the running copy.
        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsSignal);
        if (!first)
        {
            Log.Info("Already running; asking the running copy to open Settings.");
            _showSignal.Set();
            Shutdown();
            return;
        }
        var listener = new Thread(() =>
        {
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() => ShowSettings());
        }) { IsBackground = true, Name = "PaperPilot show-settings" };
        listener.Start();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            StartApp(e.Args);
        }
        catch (Exception ex)
        {
            // Never fail silently: say what happened and quit cleanly.
            Log.Error("Startup failed", ex);
            MessageBox.Show(
                $"Paper Pilot couldn't start.\n\n{ex.Message}\n\nThe details are in the log folder:\n{Log.LogFolder}",
                "Paper Pilot", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
        }
    }

    private void StartApp(string[] args)
    {
        Log.Info("Startup: theme.");
        ThemeManager.Initialize(Resources);
        System.Windows.Forms.Application.EnableVisualStyles(); // modern look for the tray menu
        _ = WindowCapture.RequestBorderlessAccessAsync();
        Log.Info("Startup: components.");
        Controller = new AppController();
        Controller.Notice += msg => Log.Info($"Notice: {msg}");
        Log.Info("Startup: tray icon.");
        _tray = new TrayIcon(Controller, () => ShowSettings(), Quit);

        bool background = args.Contains(StartWithWindows.BackgroundArg) || args.Contains(Updater.WaitForPidArg);
        if (!Controller.Settings.FirstRunDone)
        {
            Controller.Settings.FirstRunDone = true;
            Controller.Settings.Save();
            ShowSettings();
        }
        else if (!background || Controller.Attention.Count > 0)
        {
            ShowSettings();
        }
        if (Controller.Settings.DeveloperOptions && Controller.Settings.ShowDebugWindow) ShowDebug();
        Log.Info("Startup: ready.");
        if (args.Contains("--smoke-test")) _ = Diagnostics.SmokeTest.RunAsync(this, Controller);
    }

    public SettingsWindow? SettingsWindowForTests => _settingsWindow;

    public void ShowSettings(SettingsWindow.PageId page = SettingsWindow.PageId.Home)
    {
        if (Controller is null) return;
        if (_settingsWindow is null)
        {
            Log.Info("Opening Settings.");
            _settingsWindow = new SettingsWindow(Controller, ShowDebug, page);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else
        {
            _settingsWindow.ShowPage(page);
        }
        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    private void ShowDebug()
    {
        if (Controller is null) return;
        if (_debugWindow is null)
        {
            _debugWindow = new DebugWindow(Controller);
            _debugWindow.Closed += (_, _) => _debugWindow = null;
            _debugWindow.Show();
        }
        _debugWindow.Activate();
    }

    private void Quit()
    {
        Log.Info("Quit from the tray.");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        Controller?.Updates.InstallOnExit();
        Controller?.Dispose();
        _instance?.Dispose();
        Log.Info("Paper Pilot exiting.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
    }
}
