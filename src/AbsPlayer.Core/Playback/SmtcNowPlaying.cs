using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace AbsPlayer.Core.Playback;

/// <summary>
/// The Windows media overlay and keyboard media keys (System Media Transport Controls).
/// MediaPlayer's own handling is turned off so next/previous move between chapters, not files.
/// </summary>
public sealed class SmtcNowPlaying : INowPlaying
{
    private readonly SystemMediaTransportControls _smtc;

    public event EventHandler<RemoteCommand>? Command;

    public SmtcNowPlaying(MediaPlayer player)
    {
        player.CommandManager.IsEnabled = false;
        _smtc = player.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsFastForwardEnabled = true;
        _smtc.IsRewindEnabled = true;
        _smtc.ButtonPressed += (_, e) =>
        {
            RemoteCommandKind? kind = e.Button switch
            {
                SystemMediaTransportControlsButton.Play => RemoteCommandKind.Play,
                SystemMediaTransportControlsButton.Pause => RemoteCommandKind.Pause,
                SystemMediaTransportControlsButton.Next => RemoteCommandKind.NextChapter,
                SystemMediaTransportControlsButton.Previous => RemoteCommandKind.PreviousChapter,
                SystemMediaTransportControlsButton.FastForward => RemoteCommandKind.SkipForward,
                SystemMediaTransportControlsButton.Rewind => RemoteCommandKind.SkipBack,
                _ => null,
            };
            if (kind is { } k) Command?.Invoke(this, new RemoteCommand(k));
        };
        _smtc.PlaybackPositionChangeRequested += (_, e) =>
            Command?.Invoke(this, new RemoteCommand(RemoteCommandKind.SeekTo, e.RequestedPlaybackPosition.TotalSeconds));
        _smtc.PlaybackRateChangeRequested += (_, e) =>
            Command?.Invoke(this, new RemoteCommand(RemoteCommandKind.ChangeRate, e.RequestedPlaybackRate));
    }

    public void SetMetadata(string title, string? album, string? artist)
    {
        var d = _smtc.DisplayUpdater;
        d.Type = MediaPlaybackType.Music;
        d.MusicProperties.Title = title;
        d.MusicProperties.AlbumTitle = album ?? "";
        d.MusicProperties.Artist = artist ?? "";
        d.Update();
    }

    public async void SetArtwork(Uri? uri)
    {
        try
        {
            RandomAccessStreamReference? thumb = uri switch
            {
                null => null,
                { IsFile: true } => RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(uri.LocalPath)),
                _ => RandomAccessStreamReference.CreateFromUri(uri),
            };
            _smtc.DisplayUpdater.Thumbnail = thumb;
            _smtc.DisplayUpdater.Update();
        }
        catch (Exception) { } // Missing artwork isn't worth surfacing.
    }

    public void SetState(bool playing, double position, double duration, double rate)
    {
        _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        _smtc.PlaybackRate = rate;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = TimeSpan.FromSeconds(Math.Max(position, 0)),
            MaxSeekTime = TimeSpan.FromSeconds(Math.Max(duration, 0)),
            EndTime = TimeSpan.FromSeconds(Math.Max(duration, 0)),
        });
    }

    public void Clear()
    {
        _smtc.DisplayUpdater.ClearAll();
        _smtc.DisplayUpdater.Update();
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
    }

    /// <summary>One MediaPlayer shared by the engine and the overlay, as Windows expects.</summary>
    public static (WindowsMediaEngine Engine, SmtcNowPlaying NowPlaying) Create()
    {
        var player = new MediaPlayer();
        return (new WindowsMediaEngine(player), new SmtcNowPlaying(player));
    }
}
