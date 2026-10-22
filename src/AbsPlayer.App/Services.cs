using AbsPlayer.Core;
using AbsPlayer.Core.Playback;
using AbsPlayer.Core.Storage;
using Microsoft.UI.Dispatching;

namespace AbsPlayer.App;

/// <summary>The app's single instances, created once on the UI thread.</summary>
public static class Services
{
    public static SettingsStore Settings { get; private set; } = null!;
    public static UserPreferences User { get; private set; } = null!;
    public static Prefs Prefs { get; private set; } = null!;
    public static HttpClient Http { get; private set; } = null!;
    public static AudiobookRepository Repository { get; private set; } = null!;
    public static LocalProgressStore Progress { get; private set; } = null!;
    public static AudioPlayer Audio { get; private set; } = null!;
    public static PlaybackController Playback { get; private set; } = null!;
    public static LibraryModel Library { get; private set; } = null!;
    public static UpdateChecker Updates { get; private set; } = null!;
    public static DispatcherQueue Dispatcher { get; private set; } = null!;

    public static void Initialize()
    {
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        void Dispatch(Action a)
        {
            if (Dispatcher.HasThreadAccess) a();
            else Dispatcher.TryEnqueue(() => a());
        }

        var dir = AppConfig.DataDirectory;
        Settings = new SettingsStore(Path.Combine(dir, "settings.json"));
        User = new UserPreferences(Settings, new CredentialLockerStore());
        Prefs = new Prefs(Settings);
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd($"ABSPlayerWindows/{AppConfig.Version}");
        Repository = new AudiobookRepository(User, Http);
        Progress = new LocalProgressStore(Path.Combine(dir, "local_progress.json"));
        var (engine, nowPlaying) = SmtcNowPlaying.Create();
        Audio = new AudioPlayer(engine, nowPlaying, Prefs, Settings, Dispatch);
        Playback = new PlaybackController(Repository, Audio, Progress, Prefs, dispatch: Dispatch);
        Library = new LibraryModel(Repository, Prefs);
        Updates = new UpdateChecker(Http, Settings, AppConfig.Version);
    }
}
