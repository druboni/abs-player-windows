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
