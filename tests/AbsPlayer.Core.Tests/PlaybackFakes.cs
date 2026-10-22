using System.Net;
using System.Text.Json;
using AbsPlayer.Core.Playback;
using AbsPlayer.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace AbsPlayer.Core.Tests;

/// <summary>An engine that "plays" by moving a cursor when <see cref="Advance"/> is called.</summary>
public sealed class FakeMediaEngine : IMediaEngine
{
    public List<Uri> Urls { get; private set; } = [];
    /// <summary>Length of each file, used by <see cref="Advance"/>.</summary>
    public double[] Durations { get; set; } = [];
    public bool Playing { get; private set; }
    public double CurrentRate { get; private set; } = 1;
    public double CurrentVolume { get; private set; } = 1;
    public int Opens { get; private set; }
    public int CurrentIndex { get; set; } = -1;
    public double LocalPosition { get; set; }

    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    public void Open(IReadOnlyList<Uri> urls, int index, double localSeconds)
    {
        Urls = [.. urls];
        CurrentIndex = index;
        LocalPosition = localSeconds;
        Playing = false;
        Opens++;
    }

    public void Play() => Playing = true;
    public void Pause() => Playing = false;
    public void Seek(int index, double localSeconds) { CurrentIndex = index; LocalPosition = localSeconds; }
    public void Close() { Urls = []; CurrentIndex = -1; LocalPosition = 0; Playing = false; }
    public double Rate { set => CurrentRate = value; }
    public double Volume { set => CurrentVolume = value; }

    public void Advance(double seconds)
    {
        if (!Playing || CurrentIndex < 0) return;
        LocalPosition += seconds * CurrentRate;
        while (CurrentIndex >= 0 && CurrentIndex < Durations.Length && LocalPosition >= Durations[CurrentIndex])
        {
            LocalPosition -= Durations[CurrentIndex];
            CurrentIndex++;
            if (CurrentIndex >= Durations.Length)
            {
                CurrentIndex = -1;
                LocalPosition = 0;
                Playing = false;
                Ended?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public void Fail(string message) { Playing = false; Failed?.Invoke(this, message); }
}

public sealed class FakeNowPlaying : INowPlaying
{
    public (string Title, string? Album, string? Artist)? Metadata { get; private set; }
    public Uri? Artwork { get; private set; }
    public (bool Playing, double Position, double Duration, double Rate)? State { get; private set; }
    public bool Cleared { get; private set; }

    public event EventHandler<RemoteCommand>? Command;

    public void SetMetadata(string title, string? album, string? artist) => Metadata = (title, album, artist);
    public void SetArtwork(Uri? uri) => Artwork = uri;
    public void SetState(bool playing, double position, double duration, double rate) { State = (playing, position, duration, rate); Cleared = false; }
    public void Clear() { Cleared = true; Metadata = null; State = null; }

    public void Press(RemoteCommandKind kind, double value = 0) => Command?.Invoke(this, new RemoteCommand(kind, value));
}

public sealed class FakeOfflineLibrary : IOfflineLibrary
{
    public Dictionary<string, (DownloadedBook Book, List<TrackInfo> Tracks)> Books { get; } = [];
    public List<string> Removed { get; } = [];
    public DownloadedBook? Book(string itemId) => Books.TryGetValue(itemId, out var b) ? b.Book : null;
    public IReadOnlyList<TrackInfo>? LocalTracks(string itemId) => Books.TryGetValue(itemId, out var b) ? b.Tracks : null;
    public void Remove(string itemId) { Removed.Add(itemId); Books.Remove(itemId); }
}

/// <summary>
/// A controller wired to fakes, with a fake Audiobookshelf server for book "b1":
/// two files (0–100 s, 100–200 s), three chapters (0–60, 60–150, 150–200), resuming at 30 s.
/// </summary>
public sealed class PlaybackHarness : IDisposable
{
    public TestEnv Env { get; } = new(server: "http://abs.test");
    public FakeMediaEngine Engine { get; } = new() { Durations = [100, 100] };
    public FakeNowPlaying NowPlaying { get; } = new();
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
    public FakeOfflineLibrary Offline { get; } = new();
    public Prefs Prefs { get; }
    public LocalProgressStore Progress { get; }
    public AudioPlayer Audio { get; }
    public PlaybackController Controller { get; }

    /// <summary>Set to make the server unreachable.</summary>
    public bool Offline_ { get; set; }
    /// <summary>Status code for session sync/close calls.</summary>
    public HttpStatusCode SessionStatus { get; set; } = HttpStatusCode.OK;
    public double ResumeAt { get; set; } = 30;

    public PlaybackHarness()
    {
        Prefs = new Prefs(Env.Prefs.Settings);
        Progress = new LocalProgressStore(Path.Combine(Env.Dir, "progress.json"), Clock);
        Audio = new AudioPlayer(Engine, NowPlaying, Prefs, Env.Prefs.Settings);
        Controller = new PlaybackController(Env.Repo, Audio, Progress, Prefs, Clock, Offline);
        Env.Handler.Respond = Route;
    }

    private Task<HttpResponseMessage> Route(HttpRequestMessage req)
    {
        if (Offline_) throw new HttpRequestException("offline");
        var path = req.RequestUri!.AbsolutePath;
        var method = req.Method.Method;
        HttpResponseMessage r = (method, path) switch
        {
            ("POST", "/api/items/b1/play") => StubHandler.Json(JsonSerializer.Serialize(new
            {
                id = "sess1",
                libraryItemId = "b1",
                displayTitle = "The Book",
                displayAuthor = "An Author",
                duration = 200.0,
                currentTime = ResumeAt,
                audioTracks = new[]
                {
                    new { index = 2, startOffset = 100.0, duration = 100.0, title = "part 2", contentUrl = "/api/items/b1/file/2" },
                    new { index = 1, startOffset = 0.0, duration = 100.0, title = "part 1", contentUrl = "/api/items/b1/file/1" },
                },
                chapters = new[]
                {
                    new { id = 0, start = 0.0, end = 60.0, title = "One" },
                    new { id = 1, start = 60.0, end = 150.0, title = "Two" },
                    new { id = 2, start = 150.0, end = 200.0, title = "Three" },
                },
            })),
            ("POST", "/api/items/empty/play") => StubHandler.Json("""{"id":"sess2","libraryItemId":"empty","duration":0,"currentTime":0,"audioTracks":[],"chapters":[]}"""),
            ("POST", var p) when p.StartsWith("/api/session/") => StubHandler.Json("{}", SessionStatus),
            ("PATCH", var p) when p.StartsWith("/api/me/progress/") => StubHandler.Json("{}"),
            ("GET", var p) when p.StartsWith("/api/me/progress/") => StubHandler.Status(HttpStatusCode.NotFound),
            ("GET", "/api/me") => StubHandler.Json("""{"bookmarks":[{"libraryItemId":"b1","title":"Saved","time":75}]}"""),
            ("POST", "/api/me/item/b1/bookmark") => StubHandler.Json(BookmarkEcho(req)),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        };
        return Task.FromResult(r);
    }

    private static string BookmarkEcho(HttpRequestMessage req)
    {
        var body = JsonDocument.Parse(req.Content!.ReadAsStringAsync().Result).RootElement;
        return JsonSerializer.Serialize(new { libraryItemId = "b1", title = body.GetProperty("title").GetString(), time = body.GetProperty("time").GetInt32() });
    }

    /// <summary>Lets audio and the once-a-second ticker run for the given seconds.</summary>
    public async Task Run(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            Engine.Advance(1);
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Controller.PendingSync;
        }
    }

    public IEnumerable<(HttpRequestMessage Request, string? Body)> Calls(string method, string pathPart) =>
        Env.Handler.Requests.Where(r => r.Request.Method.Method == method && r.Request.RequestUri!.AbsolutePath.Contains(pathPart));

    public void Dispose() => Env.Dispose();
}
