using Windows.Media.Core;
using Windows.Media.Playback;

namespace AbsPlayer.Core.Playback;

/// <summary>
/// <see cref="IMediaEngine"/> on Windows.Media.Playback: one <see cref="MediaPlaybackList"/> per book.
/// A seek into a file that isn't open yet is held until that file opens, and playback waits for it,
/// so the listener never hears a blip from the start of the file.
/// </summary>
public sealed class WindowsMediaEngine : IMediaEngine, IDisposable
{
    private readonly MediaPlayer _player;
    private readonly Lock _lock = new();
    private MediaPlaybackList? _list;
    private int _pendingIndex = -1;
    private double _pendingSeek;
    private bool _wantPlay;
    private bool _ended;
    private double _rate = 1;
    /// <summary>Files that failed to open, by index. A failed preload of the next file is only reported once it's reached.</summary>
    private readonly Dictionary<int, string> _failed = [];

    public event EventHandler? Ended;
    public event EventHandler<string>? Failed;

    public WindowsMediaEngine(MediaPlayer player)
    {
        _player = player;
        _player.AutoPlay = false;
        _player.AudioCategory = MediaPlayerAudioCategory.Media;
        _player.MediaOpened += (_, _) => TryApplyPending();
        _player.PlaybackSession.PlaybackStateChanged += (_, _) => TryApplyPending();
        _player.PlaybackSession.NaturalDurationChanged += (_, _) => TryApplyPending();
        _player.MediaEnded += (_, _) =>
        {
            lock (_lock) { _ended = true; _wantPlay = false; }
            Ended?.Invoke(this, EventArgs.Empty);
        };
        _player.MediaFailed += (_, e) => Failed?.Invoke(this, string.IsNullOrEmpty(e.ErrorMessage) ? e.Error.ToString() : e.ErrorMessage);
    }

    public void Open(IReadOnlyList<Uri> urls, int index, double localSeconds)
    {
        var list = new MediaPlaybackList { AutoRepeatEnabled = false, MaxPlayedItemsToKeepOpen = 1 };
        foreach (var url in urls)
            list.Items.Add(new MediaPlaybackItem(MediaSource.CreateFromUri(url)));
        list.CurrentItemChanged += (_, _) =>
        {
            // Each file starts at the session's rate on some systems; keep the listener's speed.
            double rate;
            lock (_lock) rate = _rate;
            _player.PlaybackSession.PlaybackRate = rate;
            TryApplyPending();
            ReportIfFailed(list);
        };
        list.ItemFailed += (_, e) =>
        {
            var index = list.Items.IndexOf(e.Item);
            var message = $"Can't play file {index + 1} of {list.Items.Count} ({e.Error.ErrorCode}). " +
                "It may be missing on the server, or in a format Windows can't play.";
            lock (_lock) _failed[index] = message;
            ReportIfFailed(list);
        };
        if (index > 0 && index < list.Items.Count) list.StartingItem = list.Items[index];
        lock (_lock)
        {
            _list = list;
            _failed.Clear();
            _ended = false;
            _wantPlay = false;
            _pendingIndex = Math.Clamp(index, 0, Math.Max(urls.Count - 1, 0));
            _pendingSeek = localSeconds;
        }
        _player.Source = list;
    }

    /// <summary>Raises <see cref="Failed"/> if the file being played (or waited for) is one that failed to open.</summary>
    private void ReportIfFailed(MediaPlaybackList list)
    {
        string? message;
        lock (_lock)
        {
            if (_list != list) return;
            var index = _pendingIndex >= 0 ? _pendingIndex : (int)list.CurrentItemIndex;
            if (!_failed.TryGetValue(index, out message)) return;
            _wantPlay = false;
        }
        Failed?.Invoke(this, message);
    }

    /// <summary>Applies a held seek once the target file is the current one and has opened.</summary>
    private void TryApplyPending()
    {
        bool play;
        lock (_lock)
        {
            if (_list is null || _pendingIndex < 0) return;
            if ((int)_list.CurrentItemIndex != _pendingIndex) return;
            var session = _player.PlaybackSession;
            if (session.NaturalDuration <= TimeSpan.Zero) return; // Not opened yet.
            if (_pendingSeek > 0) session.Position = TimeSpan.FromSeconds(_pendingSeek);
            session.PlaybackRate = _rate;
            _pendingIndex = -1;
            play = _wantPlay;
        }
        if (play) _player.Play();
    }

    private bool Pending
    {
        get { lock (_lock) return _pendingIndex >= 0; }
    }

    public void Play()
    {
        lock (_lock) _wantPlay = true;
        if (!Pending) _player.Play();
        else TryApplyPending();
    }

    public void Pause()
    {
        lock (_lock) _wantPlay = false;
        _player.Pause();
    }

    public void Seek(int index, double localSeconds)
    {
        MediaPlaybackList? list;
        lock (_lock)
        {
            list = _list;
            if (list is null) return;
            var current = (int)list.CurrentItemIndex;
            if (!_ended && _pendingIndex < 0 && current == index)
            {
                _player.PlaybackSession.Position = TimeSpan.FromSeconds(localSeconds);
                return;
            }
            _ended = false;
            _pendingIndex = index;
            _pendingSeek = localSeconds;
        }
        // Hold playback until the new file is open and positioned.
        if (_player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _player.Pause();
        list.MoveTo((uint)index);
        TryApplyPending();
        ReportIfFailed(list);
    }

    public void Close()
    {
        lock (_lock)
        {
            _list = null;
            _pendingIndex = -1;
            _wantPlay = false;
            _ended = false;
        }
        _player.Pause();
        _player.Source = null;
    }

    public int CurrentIndex
    {
        get
        {
            lock (_lock)
            {
                if (_ended) return -1;
                if (_pendingIndex >= 0) return _pendingIndex;
                if (_list is null) return -1;
                var i = _list.CurrentItemIndex;
                return i == uint.MaxValue ? -1 : (int)i;
            }
        }
    }

    public double LocalPosition
    {
        get
        {
            lock (_lock)
                return _pendingIndex >= 0 ? _pendingSeek : _player.PlaybackSession.Position.TotalSeconds;
        }
    }

    public double Rate
    {
        set
        {
            lock (_lock) _rate = value;
            _player.PlaybackSession.PlaybackRate = value;
        }
    }

    public double Volume { set => _player.Volume = value; }

    public void Dispose() => _player.Dispose();
}
