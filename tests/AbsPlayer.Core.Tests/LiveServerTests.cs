using AbsPlayer.Core.Api;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Tests;

/// <summary>Runs only when ABS_LIVE_URL and ABS_LIVE_TOKEN are set.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ABS_LIVE_URL") is null || Environment.GetEnvironmentVariable("ABS_LIVE_TOKEN") is null)
            Skip = "Set ABS_LIVE_URL and ABS_LIVE_TOKEN to run against a real server.";
    }
}

/// <summary>Runs only when REQUESTER_LIVE_URL is set (an Audiobook Requester address, e.g. host:5066).</summary>
public sealed class RequesterLiveFactAttribute : FactAttribute
{
    public RequesterLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("REQUESTER_LIVE_URL") is null)
            Skip = "Set REQUESTER_LIVE_URL to check against a real Audiobook Requester.";
    }
}

public class RequesterLiveTests
{
    /// <summary>Only the public ping: no sign-in, nothing changes.</summary>
    [RequesterLiveFact]
    public async Task Finds_requester_from_a_bare_address()
    {
        using var http = new HttpClient();
        var input = Environment.GetEnvironmentVariable("REQUESTER_LIVE_URL")!;
        var resolved = await Requester.RequesterClient.Resolve(input.Replace("http://", "").Replace("https://", ""), http);
        Assert.StartsWith("http", resolved);
        Assert.False(resolved.EndsWith('/'));
    }
}

/// <summary>
/// Read-only checks against a real Audiobookshelf server: nothing here starts a session,
/// changes progress or touches bookmarks.
/// </summary>
public class LiveServerTests
{
    private static (TestEnv Env, AudiobookRepository Repo) Connect()
    {
        var env = new TestEnv(server: null, token: null);
        var http = new HttpClient();
        env.Prefs.ServerUrl = Environment.GetEnvironmentVariable("ABS_LIVE_URL")!.TrimEnd('/');
        env.Prefs.Token = Environment.GetEnvironmentVariable("ABS_LIVE_TOKEN");
        return (env, new AudiobookRepository(env.Prefs, http));
    }

    [LiveFact]
    public async Task Server_resolves_and_reports_version()
    {
        using var http = new HttpClient();
        var url = Environment.GetEnvironmentVariable("ABS_LIVE_URL")!;
        var resolved = await AudiobookShelfApi.ResolveServer(url, http);
        Assert.StartsWith("http", resolved);
        Assert.NotNull(await AudiobookShelfApi.ServerVersion(resolved, http));
    }

    [LiveFact]
    public async Task Library_browse_search_series_and_item_details()
    {
        var (env, repo) = Connect();
        using var _ = env;

        var libraries = await repo.GetLibraries();
        Assert.NotEmpty(libraries);
        var lib = libraries.First(l => l.Icon != "podcast");

        foreach (var sort in Enum.GetValues<LibrarySort>())
        {
            var page = await repo.GetLibraryItems(lib.Id, sort, limit: 10);
            Assert.True(page.Total > 0);
            Assert.NotEmpty(page.Results);
        }
        var items = (await repo.GetLibraryItems(lib.Id, LibrarySort.Title, limit: 50)).Results;
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Media.Metadata.Title)));

        var series = await repo.GetSeries(lib.Id, limit: 20);
        Assert.NotEmpty(series.Results);
        Assert.All(series.Results, s => Assert.NotEmpty(s.Books));

        var first = items[0];
        var word = first.Media.Metadata.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderByDescending(w => w.Length).First();
        var search = await repo.Search(lib.Id, word);
        Assert.False(search.IsEmpty);

        var detail = await repo.GetItem(first.Id);
        Assert.Equal(first.Id, detail.Id);
        Assert.NotEmpty(detail.Media.Tracks ?? []);
        Assert.True(detail.Media.Duration > 0);

        if (search.Authors.FirstOrDefault() is { } author)
            Assert.NotEmpty(await repo.GetBooks(lib.Id, author));

        var cover = await new HttpClient().GetAsync(repo.CoverUrl(first.Id));
        Assert.True(cover.IsSuccessStatusCode, $"cover: {(int)cover.StatusCode}");
    }

    [LiveFact]
    public async Task Me_progress_and_continue_listening_parse()
    {
        var (env, repo) = Connect();
        using var _ = env;
        await repo.GetProgressMap();
        await repo.GetContinueListening();
    }

    /// <summary>Streams a real book through the Windows engine at volume 0. No session is opened.</summary>
    [LiveFact]
    public async Task Windows_engine_streams_a_real_book()
    {
        var (env, repo) = Connect();
        using var _ = env;
        var lib = (await repo.GetLibraries()).First(l => l.Icon != "podcast");
        var first = (await repo.GetLibraryItems(lib.Id, LibrarySort.Title, limit: 1)).Results[0];
        var track = (await repo.GetItem(first.Id)).Media.Tracks!.OrderBy(t => t.Index).First();
        var url = repo.MediaUrl(track.ContentUrl!)!;

        var (engine, _) = Playback.SmtcNowPlaying.Create();
        using var __ = engine;
        string? failure = null;
        engine.Failed += (_, m) => failure = m;
        engine.Volume = 0;
        engine.Open([url], 0, 60); // Start a minute in, which needs a ranged request.
        engine.Play();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (engine.LocalPosition < 61.5 && failure is null && sw.ElapsedMilliseconds < 20_000) await Task.Delay(100);
        engine.Close();
        Assert.Null(failure);
        Assert.True(sw.ElapsedMilliseconds < 20_000, "stream didn't reach 61.5 s within 20 s");
    }

    /// <summary>
    /// Downloads a real book (ABS_LIVE_DOWNLOAD_ITEM) to a temp folder and plays it from disk at volume 0,
    /// across its first file boundary. Read-only on the server. Skipped unless the item is set.
    /// </summary>
    [LiveFact]
    public async Task Downloads_a_real_book_and_plays_it_from_disk()
    {
        if (Environment.GetEnvironmentVariable("ABS_LIVE_DOWNLOAD_ITEM") is not { } itemId) return;
        var (env, repo) = Connect();
        using var _ = env;
        var root = Path.Combine(env.Dir, "Downloads");
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var downloads = new Downloads.DownloadManager(repo, http, new Prefs(env.Prefs.Settings), root);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        downloads.Download(itemId);
        while (downloads.Status(itemId).Kind is not (Downloads.DownloadKind.Downloaded or Downloads.DownloadKind.Failed) && sw.Elapsed < TimeSpan.FromMinutes(10))
            await Task.Delay(250);
        var status = downloads.Status(itemId);
        Assert.True(status.Kind == Downloads.DownloadKind.Downloaded, $"{status.Kind}: {status.Message}");

        var item = await repo.GetItem(itemId);
        var tracks = downloads.LocalTracks(itemId)!;
        Assert.Equal(item.Media.Tracks!.Count, tracks.Count);
        Assert.All(tracks, t => Assert.True(File.Exists(t.Url.LocalPath)));
        Assert.All(tracks, t => Assert.NotEqual("", Path.GetExtension(t.Url.LocalPath)));
        Assert.Equal(item.Media.Tracks.Sum(t => t.Metadata?.Size ?? 0), downloads.Manifest(itemId)!.SizeBytes);
        Assert.NotNull(downloads.LocalCover(itemId));
        Console.WriteLine($"Downloaded {tracks.Count} files, {downloads.TotalBytes / 1e6:0.0} MB in {sw.Elapsed.TotalSeconds:0}s");

        // Play from disk, starting a second before file 1 ends, and check it moves on to file 2.
        var (engine, __) = Playback.SmtcNowPlaying.Create();
        using var ___ = engine;
        string? failure = null;
        engine.Failed += (_, m) => failure = m;
        engine.Volume = 0;
        engine.Open(tracks.Select(t => t.Url).ToList(), 0, Math.Max(tracks[0].Duration - 1, 0));
        engine.Play();
        var played = System.Diagnostics.Stopwatch.StartNew();
        var target = tracks.Count > 1 ? 1 : 0;
        while ((engine.CurrentIndex < target || engine.LocalPosition < 0.5) && failure is null && played.ElapsedMilliseconds < 15_000) await Task.Delay(100);
        engine.Close();
        Assert.Null(failure);
        Assert.True(played.ElapsedMilliseconds < 15_000, "didn't play across the first file boundary");
    }

    [LiveFact]
    public async Task Bad_token_is_unauthorized()
    {
        var (env, _) = Connect();
        using var __ = env;
        env.Prefs.Token = "not-a-real-token";
        var repo = new AudiobookRepository(env.Prefs, new HttpClient());
        var e = await Assert.ThrowsAsync<ApiException>(() => repo.GetLibraries());
        Assert.Equal(ApiErrorKind.Unauthorized, e.Kind);
    }
}
