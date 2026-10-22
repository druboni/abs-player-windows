using System.ComponentModel;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Playback;

public enum SleepTimerKind { Off, Minutes, EndOfChapter }

public readonly record struct SleepTimer(SleepTimerKind Kind, int Minutes = 0)
{
    public static readonly SleepTimer Off = new(SleepTimerKind.Off);
    public static readonly SleepTimer EndOfChapter = new(SleepTimerKind.EndOfChapter);
    public static SleepTimer ForMinutes(int m) => new(SleepTimerKind.Minutes, m);
    public static readonly int[] Presets = [5, 10, 15, 30, 45, 60];
}

/// <summary>
/// Owns the current book: opens an Audiobookshelf playback session, drives the
/// audio player, and syncs position back so every client resumes in the same place.
/// Use from the UI thread.
/// </summary>
public sealed class PlaybackController : INotifyPropertyChanged
{
    private const int ForegroundSyncSeconds = 10;
    private const int BackgroundSyncSeconds = 60;
    private const double FadeSeconds = 10;
    private const string LastItemKey = "last_item_id";

    private readonly AudiobookRepository _repository;
    private readonly LocalProgressStore _progress;
    private readonly Prefs _prefs;
    private readonly TimeProvider _clock;
    private readonly IOfflineLibrary? _offline;
    private readonly Action<Action> _dispatch;

    private string? _sessionId;
    private ITimer? _ticker;
    private double _secondsListened;
    private int _tickCount;
    private int _announcedChapter = -2;
    private int? _sleepChapterIndex;

    public AudioPlayer Audio { get; }

    public string? ItemId { get; private set; }
    public string Title { get; private set; } = "";
    public string? Author { get; private set; }
    public IReadOnlyList<Chapter> Chapters { get; private set; } = [];
    public bool IsLoading { get; private set; }
    public string? Error { get; set; }
    public IReadOnlyList<Bookmark> Bookmarks { get; private set; } = [];
    public string? BookmarkError { get; set; }
    public SleepTimer SleepTimer { get; private set; } = SleepTimer.Off;
    /// <summary>Seconds of playback left before the sleep timer pauses (minutes mode only).</summary>
    public double? SleepRemaining { get; private set; }

    /// <summary>Set by the app. When minimized we sync less often.</summary>
    public bool IsInForeground { get; set; } = true;

    /// <summary>The most recent server sync, so callers (and tests) can wait for it.</summary>
    public Task PendingSync { get; private set; } = Task.CompletedTask;

    /// <summary>Raised on every state change and once a second while a book is open; property name is empty (everything).</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="dispatch">Runs the once-a-second ticker on the UI thread.</param>
    public PlaybackController(AudiobookRepository repository, AudioPlayer audio, LocalProgressStore progress, Prefs prefs,
        TimeProvider? clock = null, IOfflineLibrary? offline = null, Action<Action>? dispatch = null)
    {
        _repository = repository;
        Audio = audio;
        _progress = progress;
        _prefs = prefs;
        _clock = clock ?? TimeProvider.System;
        _offline = offline;
        _dispatch = dispatch ?? (a => a());

        audio.Paused += (_, _) => { SyncNow(); Notify(); };
        audio.NextChapterRequested += (_, _) => NextChapter();
        audio.PreviousChapterRequested += (_, _) => PreviousChapter();
        audio.PlayWithNothingLoaded += (_, _) => _ = ResumeLastBook();
        audio.PlaybackFailed += (_, message) => { Error = message; Notify(); };
    }

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    // Derived state

    public bool IsActive => ItemId is not null;
    public Uri? CoverUrl => ItemId is { } id ? _repository.CoverUrl(id) : null;

    public int ChapterIndex
    {
        get
        {
            var pos = Audio.Position;
            for (var i = Chapters.Count - 1; i >= 0; i--)
                if (pos >= Chapters[i].Start) return i;
            return -1;
        }
    }

    public Chapter? Chapter => ChapterAt(ChapterIndex);

    private Chapter? ChapterAt(int i) => i >= 0 && i < Chapters.Count ? Chapters[i] : null;

    public string? ChapterLabel =>
        Chapter is { } c ? $"Chapter {ChapterIndex + 1} of {Chapters.Count}: {c.Title}" : null;

    /// <summary>The span the scrubber covers: the current chapter, or the whole book without chapters.</summary>
    public (double Start, double End) ChapterRange
    {
        get
        {
            var i = ChapterIndex;
            if (ChapterAt(i) is not { } c) return (0, Math.Max(Audio.Duration, 1));
            var end = c.End > c.Start ? c.End : ChapterAt(i + 1)?.Start ?? Audio.Duration;
            if (Audio.Duration > 0) end = Math.Min(end, Audio.Duration);
            return (c.Start, Math.Max(end, c.Start + 1));
        }
    }

    /// <summary>Remaining time at the current playback speed.</summary>
    public double BookTimeLeft => Math.Max(Audio.Duration - Audio.Position, 0) / Audio.Speed;

    public double? ChapterTimeLeft => Chapter is null ? null : Math.Max(ChapterRange.End - Audio.Position, 0) / Audio.Speed;

    /// <summary>The most recently played book, kept so the play key can pick it back up.</summary>
    public string? LastItemId
    {
        get => _prefs.Settings.GetString(LastItemKey);
        private set => _prefs.Settings.Set(LastItemKey, value);
    }

    public async Task ResumeLastBook()
    {
        if (IsActive || !_repository.Preferences.IsLoggedIn || LastItemId is not { } id) return;
        await Play(id);
    }

    // Starting and stopping

    /// <summary>Starts (or resumes) a book. <paramref name="startAt"/> jumps to a specific spot, e.g. a chapter picked on the book page.</summary>
    public async Task Play(string id, double? startAt = null)
    {
        if (ItemId == id)
        {
            if (IsLoading) return;
            if (startAt is { } s) Audio.Seek(s);
            if (!Audio.IsPlaying) Audio.Play();
            Notify();
            return;
        }
        await Stop();
        ItemId = id;
        IsLoading = true;
        Error = null;
        Notify();
        await _progress.Flush(_repository);
        var downloaded = _offline?.Book(id);
        try
        {
            double startTime, duration;
            IReadOnlyList<TrackInfo> tracks;
            try
            {
                var s = await _repository.StartPlaybackSession(id);
                if (ItemId != id)
                {
                    // Another book was picked while this one loaded: don't leave its session open on the server.
                    _ = CloseQuietly(s.Id);
                    return;
                }
                _sessionId = s.Id;
                Title = s.DisplayTitle ?? downloaded?.Title ?? "";
                Author = s.DisplayAuthor ?? downloaded?.Author;
                if (Title.Length == 0)
                {
                    try
                    {
                        var item = await _repository.GetItem(id);
                        Title = item.Media.Metadata.Title;
                        Author = item.Media.Metadata.AuthorName;
                    }
                    catch (ApiException) { }
                }
                Chapters = s.Chapters;
                startTime = s.CurrentTime;
                duration = s.Duration;
                tracks = _offline?.LocalTracks(id) ?? s.AudioTracks.OrderBy(t => t.Index)
                    .Select(t => t.ContentUrl is { } raw && _repository.MediaUrl(raw) is { } url
                        ? new TrackInfo(url, t.StartOffset, t.Duration) : null)
                    .OfType<TrackInfo>().ToList();
            }
            catch (ApiException)
            {
                // Offline (or the server is unreachable): a downloaded book still plays.
                if (downloaded is null || _offline?.LocalTracks(id) is not { } local) throw;
                if (ItemId != id) return;
                _sessionId = null;
                Title = downloaded.Title;
                Author = downloaded.Author;
                Chapters = downloaded.Chapters;
                startTime = _progress.Get(id)?.CurrentTime ?? 0;
                duration = downloaded.Duration;
                tracks = local;
            }
            LastItemId = id;
            if (tracks.Count == 0) throw new InvalidOperationException("No playable audio in this item");

            Audio.Load(tracks, duration, startAt ?? startTime);
            _ = LoadBookmarks();
            Audio.SetArtwork(CoverUrl);
            _announcedChapter = -2;
            UpdateNowPlayingTitle();
            StartTicker();
            IsLoading = false;
            Notify();
        }
        catch (Exception e) when (e is ApiException or InvalidOperationException)
        {
            if (ItemId != id) return;
            if (_sessionId is { } sid) _ = CloseQuietly(sid);
            _sessionId = null;
            ItemId = null;
            IsLoading = false;
            Error = e.Message;
            Notify();
        }
    }

    private async Task CloseQuietly(string sessionId)
    {
        try { await _repository.CloseSession(sessionId, new SessionSyncRequest(0, 0, 0)); }
        catch (ApiException) { }
    }

    /// <summary>Saves position, closes the server session and stops audio.</summary>
    public async Task Stop()
    {
        if (ItemId is null) return;
        SetSleepTimer(SleepTimer.Off);
        _ticker?.Dispose();
        _ticker = null;
        Audio.Pause(notify: false);
        await Sync(close: true);
        if (ItemId is { } id && _prefs.RemoveDownloadWhenFinished && Audio.Duration > 0 && Audio.Position / Audio.Duration >= 0.99)
            _offline?.Remove(id);
        Audio.Stop();
        ItemId = null;
        _sessionId = null;
        Title = "";
        Author = null;
        Chapters = [];
        Bookmarks = [];
        IsLoading = false;
        Notify();
    }

    // Bookmarks

    public async Task LoadBookmarks()
    {
        if (ItemId is not { } id) return;
        try
        {
            var b = await _repository.GetBookmarks(id);
            if (ItemId == id) { Bookmarks = b; Notify(); }
        }
        catch (ApiException) { }
    }

    /// <summary>Saves the current spot, titled with the chapter so the list is easy to scan.</summary>
    public async Task AddBookmark()
    {
        if (ItemId is not { } id) return;
        var pos = Audio.Position;
        var time = (int)pos;
        var title = Chapter is { } c ? $"{c.Title} · {Format.Duration(pos - c.Start)}" : Format.Duration(pos);
        try
        {
            var b = await _repository.CreateBookmark(id, time, title);
            Bookmarks = Bookmarks.Where(x => (int)x.Time != time).Append(b).OrderBy(x => x.Time).ToList();
        }
        catch (ApiException e)
        {
            BookmarkError = $"Couldn't save bookmark: {e.Message}";
        }
        Notify();
    }

    public async Task RenameBookmark(Bookmark b, string title)
    {
        var t = title.Trim();
        if (t.Length == 0) return;
        try
        {
            await _repository.UpdateBookmark(b.LibraryItemId, (int)b.Time, t);
            if (Bookmarks.FirstOrDefault(x => x.Id == b.Id) is { } existing) existing.Title = t;
        }
        catch (ApiException e)
        {
            BookmarkError = $"Couldn't rename bookmark: {e.Message}";
        }
        Notify();
    }

    public async Task DeleteBookmark(Bookmark b)
    {
        try
        {
            await _repository.DeleteBookmark(b.LibraryItemId, (int)b.Time);
            Bookmarks = Bookmarks.Where(x => x.Id != b.Id).ToList();
        }
        catch (ApiException e)
        {
            BookmarkError = $"Couldn't delete bookmark: {e.Message}";
        }
        Notify();
    }

    // Sleep timer

    public string? SleepLabel => SleepTimer.Kind switch
    {
        SleepTimerKind.EndOfChapter => "End of chapter",
        SleepTimerKind.Minutes => SleepRemaining is { } r ? Format.Duration(r) : null,
        _ => null,
    };

    public void SetSleepTimer(SleepTimer t)
    {
        SleepTimer = t;
        Audio.SetVolume(1);
        switch (t.Kind)
        {
            case SleepTimerKind.Off:
                SleepRemaining = null;
                _sleepChapterIndex = null;
                break;
            case SleepTimerKind.Minutes:
                SleepRemaining = t.Minutes * 60;
                _sleepChapterIndex = null;
                break;
            case SleepTimerKind.EndOfChapter:
                SleepRemaining = null;
                // Without chapters there's no chapter end to wait for.
                if (Chapters.Count == 0) { SleepTimer = SleepTimer.Off; _sleepChapterIndex = null; }
                else _sleepChapterIndex = ChapterIndex;
                break;
        }
        Notify();
    }

    public void ExtendSleepTimer(int minutes)
    {
        if (SleepTimer.Kind != SleepTimerKind.Minutes || SleepRemaining is not { } r) return;
        SleepRemaining = r + minutes * 60;
        Audio.SetVolume(1);
        Notify();
    }

    /// <summary>Runs every second from the ticker; counts down only while audio is playing.</summary>
    private void TickSleepTimer()
    {
        switch (SleepTimer.Kind)
        {
            case SleepTimerKind.Minutes:
                if (!Audio.IsPlaying || SleepRemaining is not { } r) return;
                r -= 1;
                SleepRemaining = r;
                if (r <= 0)
                {
                    Audio.Pause();
                    SetSleepTimer(SleepTimer.Off);
                }
                else if (r < FadeSeconds)
                {
                    Audio.SetVolume(r / FadeSeconds);
                }
                break;
            case SleepTimerKind.EndOfChapter:
                if (_sleepChapterIndex is not { } start) return;
                var now = ChapterIndex;
                // A jump of more than one chapter (or backwards) is the listener seeking: follow them.
                if (now < start || now > start + 1)
                {
                    _sleepChapterIndex = now;
                }
                else if (now == start + 1 && Chapter is { } c)
                {
                    Audio.Seek(c.Start);
                    Audio.Pause();
                    SetSleepTimer(SleepTimer.Off);
                }
                break;
        }
    }

    // Chapters

    public void PreviousChapter()
    {
        var i = ChapterIndex;
        if (ChapterAt(i) is not { } c) { Audio.SkipBack(); return; }
        Audio.Seek(Audio.Position - c.Start > 3 || i == 0 ? c.Start : Chapters[i - 1].Start);
        Notify();
    }

    public void NextChapter()
    {
        if (ChapterAt(ChapterIndex + 1) is { } n) { Audio.Seek(n.Start); Notify(); }
    }

    public void SeekTo(Chapter chapter)
    {
        Audio.Seek(chapter.Start);
        Notify();
    }

    // Sync

    public void SyncNow() => PendingSync = Sync(close: false);

    private void StartTicker()
    {
        _ticker?.Dispose();
        _secondsListened = 0;
        _tickCount = 0;
        _ticker = _clock.CreateTimer(_ => _dispatch(Tick), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void Tick()
    {
        if (ItemId is not { } id) return;
        if (Audio.IsPlaying) _secondsListened += 1;
        TickSleepTimer();
        var chapter = ChapterIndex;
        if (chapter != _announcedChapter)
        {
            // Save position at chapter boundaries too, since background syncs are spaced out.
            if (_announcedChapter >= 0 && Audio.IsPlaying) SyncNow();
            _announcedChapter = chapter;
            UpdateNowPlayingTitle();
        }
        _tickCount += 1;
        if (_tickCount % 5 == 0 && Audio.IsPlaying)
        {
            _progress.Save(id, Audio.Position, Audio.Duration, needsSync: false);
            Audio.RefreshNowPlaying(); // Keeps the Windows media overlay's progress bar moving.
        }
        var interval = IsInForeground ? ForegroundSyncSeconds : BackgroundSyncSeconds;
        if (_tickCount % interval == 0 && Audio.IsPlaying) SyncNow();
        Notify();
    }

    private void UpdateNowPlayingTitle() => Audio.SetNowPlaying(Chapter?.Title ?? Title, Title, Author);

    private async Task Sync(bool close)
    {
        if (ItemId is not { } id || Audio.Duration <= 0) return;
        var body = new SessionSyncRequest(Audio.Position, _secondsListened, Audio.Duration);
        _secondsListened = 0;
        _progress.Save(id, body.CurrentTime, body.Duration, needsSync: false);
        if (_sessionId is { } sid)
        {
            try
            {
                if (close) await _repository.CloseSession(sid, body);
                else await _repository.SyncSession(sid, body);
                _progress.MarkSynced(id);
                return;
            }
            catch (ApiException e) when (e.Kind == ApiErrorKind.ServerError && e.StatusCode == 404)
            {
                // The server dropped the session (e.g. after a long pause); fall back to plain progress.
                if (_sessionId == sid) _sessionId = null;
            }
            catch (ApiException) { }
        }
        try
        {
            await _repository.UpdateProgress(id, body.CurrentTime, body.Duration);
            _progress.MarkSynced(id);
        }
        catch (ApiException)
        {
            // Offline: keep it and send it later.
            _progress.Save(id, body.CurrentTime, body.Duration, needsSync: true);
        }
    }
}
