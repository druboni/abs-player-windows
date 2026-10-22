using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Playback;

/// <summary>Plays a book made of one or more audio tracks and exposes position in whole-book seconds.</summary>
public sealed class AudioPlayer
{
    public static readonly double[] Speeds = [0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];
    private const string SpeedKey = "playback_speed";

    private readonly IMediaEngine _engine;
    private readonly INowPlaying _nowPlaying;
    private readonly Prefs _prefs;
    private readonly SettingsStore _settings;
    private readonly Action<Action> _dispatch;

    private List<TrackInfo> _tracks = [];
    private double _position;
    private bool _loaded;
    private bool _ended;
    private bool _pausedByUser;
    private double _volume = 1;
    private Uri? _artworkUrl;

    public bool IsPlaying { get; private set; }
    public double Duration { get; private set; }
    public double Speed { get; private set; }
    public bool HasLoadedItem => _loaded;

    /// <summary>Playback paused by the user, a remote button, or the book ending.</summary>
    public event EventHandler? Paused;
    public event EventHandler? NextChapterRequested;
    public event EventHandler? PreviousChapterRequested;
    /// <summary>Play pressed (e.g. on the keyboard) with no book loaded.</summary>
    public event EventHandler? PlayWithNothingLoaded;
    /// <summary>The engine couldn't play a file.</summary>
    public event EventHandler<string>? PlaybackFailed;

    /// <param name="dispatch">Runs engine and remote-control events on the UI thread.</param>
    public AudioPlayer(IMediaEngine engine, INowPlaying nowPlaying, Prefs prefs, SettingsStore settings, Action<Action>? dispatch = null)
    {
        _engine = engine;
        _nowPlaying = nowPlaying;
        _prefs = prefs;
        _settings = settings;
        _dispatch = dispatch ?? (a => a());
        var saved = settings.Get<double?>(SpeedKey) ?? 0;
        Speed = saved > 0 ? saved : 1.0;

        engine.Ended += (_, _) => _dispatch(OnEnded);
        engine.Failed += (_, message) => _dispatch(() => OnFailed(message));
        nowPlaying.Command += (_, c) => _dispatch(() => Handle(c));
    }

    public double Position
    {
        get
        {
            if (_loaded && !_ended && _engine.CurrentIndex is var i && i >= 0 && i < _tracks.Count)
                _position = _tracks[i].Offset + Math.Max(_engine.LocalPosition, 0);
            return _position;
        }
    }

    private void Handle(RemoteCommand c)
    {
        switch (c.Kind)
        {
            case RemoteCommandKind.Play: Play(); break;
            case RemoteCommandKind.Pause: Pause(); break;
            case RemoteCommandKind.Toggle: Toggle(); break;
            case RemoteCommandKind.NextChapter: NextChapterRequested?.Invoke(this, EventArgs.Empty); break;
            case RemoteCommandKind.PreviousChapter: PreviousChapterRequested?.Invoke(this, EventArgs.Empty); break;
            case RemoteCommandKind.SkipForward: SkipForward(); break;
            case RemoteCommandKind.SkipBack: SkipBack(); break;
            case RemoteCommandKind.SeekTo: Seek(c.Value); break;
            case RemoteCommandKind.ChangeRate: SetSpeed(c.Value); break;
        }
    }

    // Loading

    public void Load(IReadOnlyList<TrackInfo> tracks, double duration, double start)
    {
        _engine.Close();
        _tracks = [.. tracks];
        Duration = duration;
        _position = start;
        _ended = false;
        _pausedByUser = false;
        IsPlaying = false;
        _loaded = _tracks.Count > 0;
        if (!_loaded) return;
        var idx = TrackIndex(start);
        _engine.Open(_tracks.Select(t => t.Url).ToList(), idx, Math.Max(start - _tracks[idx].Offset, 0));
        _engine.Volume = _volume;
        StartPlayback();
    }

    private int TrackIndex(double t)
    {
        var idx = _tracks.FindLastIndex(x => t >= x.Offset);
        return idx < 0 ? 0 : idx;
    }

    private void OnEnded()
    {
        // Ran off the end of the last track.
        _ended = true;
        _position = Duration;
        if (!IsPlaying) return;
        IsPlaying = false;
        RefreshNowPlaying();
        Paused?.Invoke(this, EventArgs.Empty);
    }

    private void OnFailed(string message)
    {
        var wasPlaying = IsPlaying;
        IsPlaying = false;
        RefreshNowPlaying();
        PlaybackFailed?.Invoke(this, message);
        if (wasPlaying) Paused?.Invoke(this, EventArgs.Empty);
    }

    // Transport

    private void StartPlayback()
    {
        _engine.Rate = Speed;
        _engine.Play();
        IsPlaying = true;
        RefreshNowPlaying();
    }

    public void Play()
    {
        if (!_loaded) { PlayWithNothingLoaded?.Invoke(this, EventArgs.Empty); return; }
        if (_ended)
        {
            // Finished: start over from the beginning.
            _ended = false;
            _engine.Seek(0, 0);
            _position = 0;
        }
        else if (_pausedByUser && _prefs.RewindOnResume > 0)
        {
            Seek(Position - _prefs.RewindOnResume);
        }
        _pausedByUser = false;
        StartPlayback();
    }

    public void Pause(bool notify = true)
    {
        if (IsPlaying) _pausedByUser = true;
        _ = Position; // Capture where we stopped.
        _engine.Pause();
        IsPlaying = false;
        RefreshNowPlaying();
        if (notify) Paused?.Invoke(this, EventArgs.Empty);
    }

    public void Toggle()
    {
        if (IsPlaying) Pause(); else Play();
    }

    public void Seek(double global)
    {
        if (!_loaded) return;
        var t = Math.Min(Math.Max(global, 0), Math.Max(Duration - 0.5, 0));
        var idx = TrackIndex(t);
        _ended = false;
        _engine.Seek(idx, t - _tracks[idx].Offset);
        _position = t;
        RefreshNowPlaying();
    }

    public void Skip(double delta) => Seek(Position + delta);
    public void SkipBack() => Skip(-_prefs.SkipBack);
    public void SkipForward() => Skip(_prefs.SkipForward);

    public void SetSpeed(double s)
    {
        Speed = s;
        _settings.Set(SpeedKey, s);
        _engine.Rate = s;
        RefreshNowPlaying();
    }

    public void CycleSpeed()
    {
        var i = Array.IndexOf(Speeds, Speed);
        SetSpeed(Speeds[(i < 0 ? 1 : i + 1) % Speeds.Length]);
    }

    /// <summary>Used by the sleep timer to fade out.</summary>
    public void SetVolume(double v)
    {
        _volume = Math.Clamp(v, 0, 1);
        _engine.Volume = _volume;
    }

    public double Volume => _volume;

    // Now Playing

    /// <summary>Title is shown large (we use the chapter), album is the book.</summary>
    public void SetNowPlaying(string title, string? album, string? artist)
    {
        _nowPlaying.SetMetadata(title, album, artist);
        RefreshNowPlaying();
    }

    public void SetArtwork(Uri? url)
    {
        if (url is null || url == _artworkUrl) return;
        _artworkUrl = url;
        _nowPlaying.SetArtwork(url);
    }

    private void RefreshNowPlaying()
    {
        if (!_loaded) return;
        _nowPlaying.SetState(IsPlaying, Position, Duration, Speed);
    }

    // Teardown

    public void Stop()
    {
        _engine.Close();
        _loaded = false;
        _ended = false;
        IsPlaying = false;
        _tracks = [];
        _position = 0;
        Duration = 0;
        _artworkUrl = null;
        _nowPlaying.Clear();
    }
}
