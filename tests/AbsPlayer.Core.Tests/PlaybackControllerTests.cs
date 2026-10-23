using System.Net;
using System.Text.Json;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Playback;

namespace AbsPlayer.Core.Tests;

public class PlaybackControllerTests
{
    private static JsonElement Body(string? json) => JsonDocument.Parse(json!).RootElement;

    [Fact]
    public async Task Play_opens_a_session_and_resumes_where_the_server_says()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");

        var (play, body) = h.Calls("POST", "/api/items/b1/play").Single();
        var device = Body(body).GetProperty("deviceInfo");
        Assert.Equal("Homeshelf Windows", device.GetProperty("clientName").GetString());
        Assert.Equal(h.Env.Prefs.DeviceId, device.GetProperty("deviceId").GetString());

        Assert.Equal("b1", h.Controller.ItemId);
        Assert.Equal("The Book", h.Controller.Title);
        Assert.Equal("An Author", h.Controller.Author);
        Assert.False(h.Controller.IsLoading);
        // Files are put in index order and carry the token.
        Assert.Equal(["http://abs.test/api/items/b1/file/1?token=tok", "http://abs.test/api/items/b1/file/2?token=tok"],
            h.Engine.Urls.Select(u => u.AbsoluteUri));
        Assert.Equal(30, h.Audio.Position);
        Assert.True(h.Audio.IsPlaying);
        Assert.Equal("b1", h.Controller.LastItemId);
        // Overlay shows the chapter large and the book as the album.
        Assert.Equal(("One", "The Book", "An Author"), h.NowPlaying.Metadata);
        Assert.Equal(new Uri("http://abs.test/api/items/b1/cover?token=tok"), h.NowPlaying.Artwork);
    }

    [Fact]
    public async Task Play_with_start_at_and_replaying_the_same_book()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 155);
        Assert.Equal(155, h.Audio.Position);
        Assert.Equal(2, h.Controller.ChapterIndex);

        h.Audio.Pause();
        await h.Controller.Play("b1", startAt: 70);
        Assert.Equal(70, h.Audio.Position);
        Assert.True(h.Audio.IsPlaying);
        Assert.Single(h.Calls("POST", "/play")); // No second session.
    }

    [Fact]
    public async Task Syncs_every_10_seconds_while_playing_with_time_listened()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        await h.Run(25);

        var syncs = h.Calls("POST", "/api/session/sess1/sync").Select(c => Body(c.Body)).ToList();
        Assert.Equal(2, syncs.Count);
        Assert.Equal(10, syncs[0].GetProperty("timeListened").GetDouble());
        Assert.Equal(40, syncs[0].GetProperty("currentTime").GetDouble());
        Assert.Equal(50, syncs[1].GetProperty("currentTime").GetDouble());
        Assert.Equal(200, syncs[1].GetProperty("duration").GetDouble());
    }

    [Fact]
    public async Task Syncs_every_60_seconds_when_minimized()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 70); // Chapter "Two" runs to 150, so no boundary in the way.
        h.Controller.IsInForeground = false;
        await h.Run(59);
        Assert.Empty(h.Calls("POST", "/sync"));
        await h.Run(1);
        var sync = Body(h.Calls("POST", "/sync").Single().Body);
        Assert.Equal(130, sync.GetProperty("currentTime").GetDouble());
        Assert.Equal(60, sync.GetProperty("timeListened").GetDouble());
    }

    [Fact]
    public async Task Pausing_syncs_and_paused_time_is_not_counted()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        await h.Run(4);
        h.Audio.Pause();
        await h.Controller.PendingSync;
        var sync = Body(h.Calls("POST", "/sync").Single().Body);
        Assert.Equal(4, sync.GetProperty("timeListened").GetDouble());

        await h.Run(30); // Paused: nothing more is sent.
        Assert.Single(h.Calls("POST", "/sync"));
    }

    [Fact]
    public async Task Crossing_a_chapter_syncs_and_updates_the_overlay_title()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 55);
        await h.Run(7);
        Assert.Single(h.Calls("POST", "/sync"));
        Assert.Equal("Two", h.NowPlaying.Metadata!.Value.Title);
        Assert.Equal("Chapter 2 of 3: Two", h.Controller.ChapterLabel);
    }

    [Fact]
    public async Task Position_is_saved_locally_every_5_seconds()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        await h.Run(5);
        Assert.Equal(35, h.Progress.Get("b1")!.CurrentTime);
    }

    [Fact]
    public async Task Stop_closes_the_session_with_the_final_position()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        await h.Run(3);
        await h.Controller.Stop();

        var close = Body(h.Calls("POST", "/api/session/sess1/close").Single().Body);
        Assert.Equal(33, close.GetProperty("currentTime").GetDouble());
        Assert.Null(h.Controller.ItemId);
        Assert.False(h.Audio.HasLoadedItem);
        Assert.True(h.NowPlaying.Cleared);

        await h.Run(20); // Ticker is gone.
        Assert.Empty(h.Calls("POST", "/sync"));
    }

    [Fact]
    public async Task Lost_session_falls_back_to_plain_progress()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        h.SessionStatus = HttpStatusCode.NotFound;
        await h.Run(10);
        var patch = h.Calls("PATCH", "/api/me/progress/b1").Single();
        Assert.Equal(40, Body(patch.Body).GetProperty("currentTime").GetDouble());

        h.SessionStatus = HttpStatusCode.OK;
        await h.Run(10);
        Assert.Single(h.Calls("POST", "/sync")); // Session forgotten; progress PATCH from now on.
        Assert.Equal(2, h.Calls("PATCH", "/api/me/progress/b1").Count());
    }

    [Fact]
    public async Task Offline_sync_is_kept_for_later()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        h.Offline_ = true;
        await h.Run(10);
        var saved = h.Progress.Get("b1")!;
        Assert.True(saved.NeedsSync);
        Assert.Equal(40, saved.CurrentTime);
    }

    [Fact]
    public async Task Downloaded_book_plays_when_the_server_is_unreachable()
    {
        using var h = new PlaybackHarness();
        var chapters = new List<Chapter> { new(0, 0, 50, "Only") };
        h.Offline.Books["b1"] = (new DownloadedBook("Saved Book", "Writer", chapters, 50),
            [new TrackInfo(new Uri("file:///C:/books/b1/1.mp3"), 0, 50)]);
        h.Progress.Save("b1", 12, 50, needsSync: false);
        h.Offline_ = true;

        await h.Controller.Play("b1");

        Assert.Null(h.Controller.Error);
        Assert.Equal("Saved Book", h.Controller.Title);
        Assert.Equal(12, h.Audio.Position);
        Assert.Equal("file:///C:/books/b1/1.mp3", h.Engine.Urls.Single().AbsoluteUri);
    }

    [Fact]
    public async Task Server_unreachable_and_not_downloaded_is_an_error()
    {
        using var h = new PlaybackHarness();
        h.Offline_ = true;
        await h.Controller.Play("b1");
        Assert.Null(h.Controller.ItemId);
        Assert.NotNull(h.Controller.Error);
        Assert.False(h.Controller.IsLoading);
    }

    [Fact]
    public async Task Item_without_audio_is_an_error_and_session_is_closed()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("empty");
        Assert.Equal("No playable audio in this item", h.Controller.Error);
        Assert.Null(h.Controller.ItemId);
        await Task.Delay(50);
        Assert.Single(h.Calls("POST", "/api/session/sess2/close"));
    }

    [Fact]
    public async Task Switching_books_while_loading_closes_the_abandoned_session()
    {
        using var h = new PlaybackHarness();
        var gate = new TaskCompletionSource();
        var inner = h.Env.Handler.Respond;
        h.Env.Handler.Respond = async req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/items/b1/play") await gate.Task; // b1 is slow to start.
            if (req.RequestUri.AbsolutePath == "/api/items/empty/play")
                return StubHandler.Json("""{"id":"sess2","libraryItemId":"empty","duration":10,"currentTime":0,"audioTracks":[{"index":1,"startOffset":0,"duration":10,"contentUrl":"/f"}],"chapters":[]}""");
            return await inner(req);
        };

        var first = h.Controller.Play("b1");
        await h.Controller.Play("empty"); // Picked another book before b1 finished loading.
        gate.SetResult();
        await first;
        await Task.Delay(50);

        Assert.Equal("empty", h.Controller.ItemId);
        Assert.Single(h.Calls("POST", "/api/session/sess1/close")); // b1's session closed, not left open.
    }

    [Fact]
    public async Task Overlay_position_is_refreshed_while_playing()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 100);
        await h.Run(5);
        Assert.Equal(105, h.NowPlaying.State!.Value.Position);
    }

    [Fact]
    public async Task Chapter_buttons()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 100);

        h.Controller.NextChapter();
        Assert.Equal(150, h.Audio.Position);
        h.Controller.NextChapter(); // Last chapter: stays.
        Assert.Equal(150, h.Audio.Position);

        h.Controller.PreviousChapter(); // Within 3 s of the start: previous chapter.
        Assert.Equal(60, h.Audio.Position);
        h.Audio.Seek(90);
        h.Controller.PreviousChapter(); // Further in: back to this chapter's start.
        Assert.Equal(60, h.Audio.Position);

        h.NowPlaying.Press(RemoteCommandKind.NextChapter); // Keyboard "next" is a chapter, not a file.
        Assert.Equal(150, h.Audio.Position);
    }

    [Fact]
    public async Task Chapter_range_and_time_left_follow_speed()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 100);
        h.Audio.SetSpeed(2);
        Assert.Equal((60.0, 150.0), h.Controller.ChapterRange);
        Assert.Equal(25, h.Controller.ChapterTimeLeft);
        Assert.Equal(50, h.Controller.BookTimeLeft);
    }

    [Fact]
    public async Task Sleep_timer_counts_only_while_playing_fades_and_pauses()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 0);
        h.Controller.SetSleepTimer(SleepTimer.ForMinutes(1));
        Assert.Equal("1:00", h.Controller.SleepLabel);

        await h.Run(30);
        Assert.Equal(30, h.Controller.SleepRemaining);
        h.Audio.Pause();
        await h.Run(20);
        Assert.Equal(30, h.Controller.SleepRemaining);

        h.Audio.Play();
        await h.Run(25);
        Assert.Equal(0.5, h.Engine.CurrentVolume, 3); // 5 s left of a 10 s fade.
        await h.Run(5);
        Assert.False(h.Audio.IsPlaying);
        Assert.Equal(SleepTimerKind.Off, h.Controller.SleepTimer.Kind);
        Assert.Equal(1, h.Engine.CurrentVolume); // Ready for next time.
    }

    [Fact]
    public async Task Sleep_timer_can_be_extended()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 0);
        h.Controller.SetSleepTimer(SleepTimer.ForMinutes(5));
        await h.Run(10);
        h.Controller.ExtendSleepTimer(5);
        Assert.Equal(590, h.Controller.SleepRemaining);
    }

    [Fact]
    public async Task Sleep_at_end_of_chapter_stops_at_the_next_chapter_start()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 140);
        h.Controller.SetSleepTimer(SleepTimer.EndOfChapter);
        await h.Run(12);
        Assert.False(h.Audio.IsPlaying);
        Assert.Equal(150, h.Audio.Position);
        Assert.Equal(SleepTimerKind.Off, h.Controller.SleepTimer.Kind);
    }

    [Fact]
    public async Task Sleep_at_end_of_chapter_follows_the_listener_seeking()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 10);
        h.Controller.SetSleepTimer(SleepTimer.EndOfChapter);
        h.Audio.Seek(155); // Jumped two chapters ahead: now waits for the end of "Three".
        await h.Run(2);
        Assert.True(h.Audio.IsPlaying);
        Assert.Equal(SleepTimerKind.EndOfChapter, h.Controller.SleepTimer.Kind);
    }

    [Fact]
    public async Task Bookmarks_load_add_and_are_titled_with_the_chapter()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1", startAt: 125);
        await h.Controller.LoadBookmarks();
        Assert.Equal("Saved", h.Controller.Bookmarks.Single().Title);

        await h.Controller.AddBookmark();
        Assert.Equal(["Saved", "Two · 1:05"], h.Controller.Bookmarks.Select(b => b.Title));
        Assert.Equal(125, Body(h.Calls("POST", "/bookmark").Single().Body).GetProperty("time").GetInt32());
    }

    [Fact]
    public async Task Play_key_with_nothing_loaded_resumes_the_last_book()
    {
        using var h = new PlaybackHarness();
        await h.Controller.Play("b1");
        await h.Controller.Stop();

        h.NowPlaying.Press(RemoteCommandKind.Play);
        await Task.Delay(100);

        Assert.Equal("b1", h.Controller.ItemId);
        Assert.True(h.Audio.IsPlaying);
    }

    [Fact]
    public async Task Finished_download_is_removed_on_stop_when_asked()
    {
        using var h = new PlaybackHarness();
        h.Prefs.RemoveDownloadWhenFinished = true;
        await h.Controller.Play("b1", startAt: 199);
        await h.Controller.Stop();
        Assert.Equal(["b1"], h.Offline.Removed);
    }

    [Fact]
    public async Task Offline_progress_is_flushed_before_a_new_session()
    {
        using var h = new PlaybackHarness();
        h.Progress.Save("other", 500, 1000, needsSync: true);
        await h.Controller.Play("b1");
        Assert.Single(h.Calls("PATCH", "/api/me/progress/other"));
        Assert.False(h.Progress.Get("other")!.NeedsSync);
    }
}
