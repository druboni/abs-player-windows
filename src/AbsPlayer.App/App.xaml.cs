using System.Runtime.InteropServices;
using AbsPlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AbsPlayer.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        // Registered before anything else so even a XAML load failure leaves a trace instead of a silent exit.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    public static MainWindow Window => ((App)Current)._window!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services.Initialize();
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception e)
        {
            Fatal(e);
            throw;
        }
    }

    /// <summary>Another copy was started: show this one instead. Called off the UI thread.</summary>
    public static void BringToFront()
    {
        if (Current is not App { _window: { } window }) return;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            if (window.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
            window.Activate();
            SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(window));
        });
    }

    /// <summary>
    /// A UI-thread error once the app is up: log it, tell the listener, and keep going, so one bad
    /// response doesn't close the player mid-book. Before the window exists, it's fatal.
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        if (_window is null) { Fatal(e.Exception); return; }
        e.Handled = true;
        Log(e.Exception);
        _window.ShowError(e.Exception.Message);
    }

    private static string LogPath => Path.Combine(AppConfig.DataDirectory, "crash.log");

    private static void Log(Exception? e)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:u}  Homeshelf {AppConfig.Version}  Windows {Environment.OSVersion.Version}\n{e}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool _reported;

    /// <summary>Writes crash.log and tells the user the app has to close.</summary>
    private static void Fatal(Exception? e)
    {
        if (_reported) return;
        _reported = true;
        Log(e);
        MessageBox(IntPtr.Zero, $"Homeshelf ran into a problem and has to close.\n\n{e?.Message}\n\nDetails were saved to:\n{LogPath}",
            "Homeshelf", 0x10 /* MB_ICONERROR */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
