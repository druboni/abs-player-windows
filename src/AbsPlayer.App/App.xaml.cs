using System.Runtime.InteropServices;
using AbsPlayer.Core;
using Microsoft.UI.Xaml;

namespace AbsPlayer.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        // Registered before anything else so even a XAML load failure leaves a trace instead of a silent exit.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        UnhandledException += (_, e) => Crash(e.Exception);
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
            Crash(e);
            throw;
        }
    }

    private static bool _reported;

    /// <summary>Writes crash.log next to the settings and tells the user where it is.</summary>
    private static void Crash(Exception? e)
    {
        if (_reported) return;
        _reported = true;
        var log = Path.Combine(AppConfig.DataDirectory, "crash.log");
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            File.AppendAllText(log, $"{DateTimeOffset.Now:u}  ABS Player {AppConfig.Version}  Windows {Environment.OSVersion.Version}\n{e}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        MessageBox(IntPtr.Zero, $"ABS Player ran into a problem and has to close.\n\n{e?.Message}\n\nDetails were saved to:\n{log}",
            "ABS Player", 0x10 /* MB_ICONERROR */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
