using AbsPlayer.Core.Playback;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Tests;

public class AudioPlayerTests : IDisposable
{
    private readonly TestEnv _env = new();
    private readonly FakeMediaEngine _engine = new() { Durations = [100, 50, 100] };
    private readonly FakeNowPlaying _np = new();
    private readonly Prefs _prefs;
    private readonly AudioPlayer _audio;

    private static readonly List<TrackInfo> Tracks =
    [
        new(new Uri("http://abs.test/1.mp3"), 0, 100),
        new(new Uri("http://abs.test/2.mp3"), 100, 50),
        new(new Uri("http://abs.test/3.mp3"), 150, 100),
    ];

    public AudioPlayerTests()
    {
        _prefs = new Prefs(_env.Prefs.Settings);
        _audio = new AudioPlayer(_engine, _np, _prefs, _env.Prefs.Settings);
    }

    public void Dispose() => _env.Dispose();

    [Fact]
    public void Load_opens_the_right_file_and_spot_and_plays()
    {
        _audio.Load(Tracks, 250, 120);

        Assert.Equal(3, _engine.Urls.Count);
        Assert.Equal(1, _engine.CurrentIndex);
        Assert.Equal(20, _engine.LocalPosition);
        Assert.True(_engine.Playing);
        Assert.True(_audio.IsPlaying);
        Assert.Equal(120, _audio.Position);
        Assert.Equal((true, 120.0, 250.0, 1.0), _np.State);
    }

    [Fact]
    public void Position_is_whole_book_time_across_files()
    {
        _audio.Load(Tracks, 250, 90);
        _engine.Advance(15); // Crosses from file 1 into file 2.
        Assert.Equal(1, _engine.CurrentIndex);
        Assert.Equal(105, _audio.Position);
        _engine.Advance(50);
        Assert.Equal(155, _audio.Position);
    }

    [Fact]
    public void Seek_is_clamped_and_lands_in_the_right_file()
    {
        _audio.Load(Tracks, 250, 0);
        _audio.Seek(160);
        Assert.Equal((2, 10.0), (_engine.CurrentIndex, _engine.LocalPosition));
        _audio.Seek(-5);
        Assert.Equal((0, 0.0), (_engine.CurrentIndex, _engine.LocalPosition));
        _audio.Seek(9999);
        Assert.Equal(249.5, _audio.Position);
    }

    [Fact]
    public void Skips_use_the_settings()
    {
        _prefs.SkipBack = 10;
        _prefs.SkipForward = 45;
        _audio.Load(Tracks, 250, 100);
        _audio.SkipForward();
        Assert.Equal(145, _audio.Position);
        _audio.SkipBack();
        Assert.Equal(135, _audio.Position);
    }

    [Fact]
    public void Rewind_on_resume_only_after_a_pause_by_the_listener()
    {
        _prefs.RewindOnResume = 5;
        _audio.Load(Tracks, 250, 100);
        _audio.Play(); // Already playing: no rewind.
        Assert.Equal(100, _audio.Position);
        _audio.Pause();
        _audio.Play();
        Assert.Equal(95, _audio.Position);
    }

    [Fact]
    public void Pause_notifies_unless_asked_not_to()
    {
        var paused = 0;
        _audio.Paused += (_, _) => paused++;
        _audio.Load(Tracks, 250, 0);
        _audio.Pause();
        _audio.Play();
        _audio.Pause(notify: false);
        Assert.Equal(1, paused);
        Assert.False(_engine.Playing);
    }

    [Fact]
    public void Reaching_the_end_pauses_and_play_starts_over()
    {
        var paused = 0;
        _audio.Paused += (_, _) => paused++;
        _audio.Load(Tracks, 250, 240);
        _engine.Advance(20);

        Assert.False(_audio.IsPlaying);
        Assert.Equal(250, _audio.Position);
        Assert.Equal(1, paused);

        _audio.Play();
        Assert.Equal(0, _audio.Position);
        Assert.True(_engine.Playing);
    }

    [Fact]
    public void Engine_failure_stops_and_reports()
    {
        string? failure = null;
        _audio.PlaybackFailed += (_, m) => failure = m;
        _audio.Load(Tracks, 250, 0);
        _engine.Fail("404");
        Assert.Equal("404", failure);
        Assert.False(_audio.IsPlaying);
    }

    [Fact]
    public void Speed_is_remembered_and_cycles()
    {
        _audio.Load(Tracks, 250, 0);
        _audio.SetSpeed(1.5);
        Assert.Equal(1.5, _engine.CurrentRate);

        var again = new AudioPlayer(new FakeMediaEngine(), new FakeNowPlaying(), _prefs, _env.Prefs.Settings);
        Assert.Equal(1.5, again.Speed);

        _audio.SetSpeed(3.0);
        _audio.CycleSpeed();
        Assert.Equal(0.75, _audio.Speed);
        _audio.CycleSpeed();
        Assert.Equal(1.0, _audio.Speed);
    }

    [Fact]
    public void Remote_buttons_drive_the_player()
    {
        int next = 0, previous = 0, nothingLoaded = 0;
        _audio.NextChapterRequested += (_, _) => next++;
        _audio.PreviousChapterRequested += (_, _) => previous++;
        _audio.PlayWithNothingLoaded += (_, _) => nothingLoaded++;

        _np.Press(RemoteCommandKind.Play);
        Assert.Equal(1, nothingLoaded);

        _audio.Load(Tracks, 250, 50);
        _np.Press(RemoteCommandKind.Toggle);
        Assert.False(_audio.IsPlaying);
        _np.Press(RemoteCommandKind.Play);
        Assert.True(_audio.IsPlaying);
        _np.Press(RemoteCommandKind.SeekTo, 200);
        Assert.Equal(200, _audio.Position);
        _np.Press(RemoteCommandKind.SkipBack);
        Assert.Equal(170, _audio.Position);
        _np.Press(RemoteCommandKind.ChangeRate, 2);
        Assert.Equal(2, _audio.Speed);
        _np.Press(RemoteCommandKind.NextChapter);
        _np.Press(RemoteCommandKind.PreviousChapter);
        Assert.Equal((1, 1), (next, previous));
    }

    [Fact]
    public void Volume_is_clamped_and_kept_across_loads()
    {
        _audio.SetVolume(1.7);
        Assert.Equal(1, _audio.Volume);
        _audio.SetVolume(0.3);
        _audio.Load(Tracks, 250, 0);
        Assert.Equal(0.3, _engine.CurrentVolume);
    }

    [Fact]
    public void Stop_clears_everything()
    {
        _audio.Load(Tracks, 250, 50);
        _audio.SetArtwork(new Uri("http://abs.test/cover"));
        _audio.Stop();
        Assert.False(_audio.HasLoadedItem);
        Assert.Equal(0, _audio.Position);
        Assert.Equal(0, _audio.Duration);
        Assert.True(_np.Cleared);
        Assert.Empty(_engine.Urls);
    }
}
