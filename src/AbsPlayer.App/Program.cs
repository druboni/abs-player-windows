using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AbsPlayer.App;

/// <summary>
/// Custom entry point so only one copy runs: opening the app again brings the existing window
/// forward instead of starting a second player that would fight the first over the same book.
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToRunningCopy()) return 0;
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectedToRunningCopy()
    {
        var main = AppInstance.FindOrRegisterForKey("ABSPlayer"); // Internal key, unchanged by the rename to Homeshelf.
        if (main.IsCurrent)
        {
            main.Activated += (_, _) => App.BringToFront();
            return false;
        }
        // Redirecting blocks on a COM call, which mustn't run on this STA thread.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        Task.Run(() => main.RedirectActivationToAsync(activation).AsTask().Wait()).Wait();
        return true;
    }
}
