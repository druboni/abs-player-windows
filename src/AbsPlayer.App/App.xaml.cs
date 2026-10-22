using Microsoft.UI.Xaml;

namespace AbsPlayer.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App() => InitializeComponent();

    public static MainWindow Window => ((App)Current)._window!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services.Initialize();
        _window = new MainWindow();
        _window.Activate();
    }
}
