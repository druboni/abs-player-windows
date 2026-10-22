using System.Net;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Tests;

public class LibraryModelTests
{
    private static string Item(string id) => "{\"id\":\"" + id + "\",\"media\":{\"metadata\":{\"title\":\"Title " + id + "\"}}}";

    /// <summary>A server with two libraries; "lib1" has 120 books served 50 at a time.</summary>
    private static (TestEnv Env, LibraryModel Model, Prefs Prefs) Setup(Func<HttpRequestMessage, Task<HttpResponseMessage>>? extra = null)
    {
        var env = new TestEnv();
        var prefs = new Prefs(env.Prefs.Settings);
        env.Handler.Respond = async req =>
        {
            if (extra is not null && await extra(req) is { } r) return r;
            var path = req.RequestUri!.AbsolutePath;
            var query = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
            if (path.EndsWith("/api/libraries"))
                return StubHandler.Json("""{"libraries":[{"id":"lib1","name":"Books","icon":"database","mediaType":"book"},{"id":"pods","name":"Pods","icon":"podcast","mediaType":"podcast"},{"id":"lib2","name":"More Books","mediaType":"book"}]}""");
            if (path.EndsWith("/api/me"))
                return StubHandler.Json("""{"mediaProgress":[{"libraryItemId":"i1","progress":1,"isFinished":true},{"libraryItemId":"i2","progress":0.4,"isFinished":false}]}""");
            if (path.EndsWith("/items-in-progress"))
                return StubHandler.Json("{\"libraryItems\":[" + Item("i1") + "," + Item("i2") + ",{\"id\":\"ep\",\"mediaType\":\"podcast\",\"media\":{\"metadata\":{\"title\":\"Pod\"}}}]}");
            if (path.EndsWith("/lib1/items"))
            {
                var page = int.Parse(query["page"]!);
                var ids = Enumerable.Range(page * 50, Math.Max(0, Math.Min(50, 120 - page * 50))).Select(n => Item($"i{n}"));
                return StubHandler.Json($$"""{"results":[{{string.Join(",", ids)}}],"total":120,"limit":50,"page":{{page}}}""");
            }
            if (path.EndsWith("/lib2/items"))
                return StubHandler.Json($$"""{"results":[{{Item("p1")}}],"total":1,"limit":50,"page":0}""");
            if (path.EndsWith("/lib1/series"))
                return StubHandler.Json($$"""{"results":[{"id":"s1","name":"Saga","books":[{{Item("i5")}}]}],"total":1}""");
            return StubHandler.Status(HttpStatusCode.NotFound);
        };
        return (env, new LibraryModel(env.Repo, prefs), prefs);
    }

    [Fact]
    public async Task Load_picks_the_first_library_and_continue_listening_skips_finished()
    {
        var (env, model, _) = Setup();
        using var _e = env;
        await model.Load();

        Assert.Equal(["Books", "More Books"], model.Libraries.Select(l => l.Name)); // Podcast library left out.
        Assert.Equal("lib1", model.Selected!.Id);
        Assert.Equal(50, model.Items.Count);
        Assert.Equal(["i2"], model.ContinueItems.Select(i => i.Id)); // Finished book and podcast episode left out.
        Assert.Equal(0.4, model.ProgressFor("i2"));
        Assert.Null(model.ProgressFor("nope"));
        Assert.False(model.Loading);
    }

    [Fact]
    public async Task Pages_load_until_the_total_is_reached()
    {
        var (env, model, _) = Setup();
        using var _e = env;
        await model.Load();
        await model.LoadMore();
        await model.LoadMore();
        Assert.Equal(120, model.Items.Count);
        Assert.False(model.HasMore);
        await model.LoadMore(); // Nothing more to ask for.
        Assert.Equal(3, env.Handler.Requests.Count(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/lib1/items")));
        Assert.Equal(120, model.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public async Task Sort_comes_from_settings_and_reload_starts_over()
    {
        var (env, model, prefs) = Setup();
        using var _e = env;
        await model.Load();
        await model.LoadMore();
        prefs.LibrarySort = LibrarySort.Author;
        await model.Reload();
        Assert.Equal(50, model.Items.Count);
        var last = env.Handler.Requests.Last(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/items")).Request.RequestUri!.Query;
        Assert.Contains("sort=media.metadata.authorName", last);
        Assert.Contains("page=0", last);
    }

    [Fact]
    public async Task Hide_finished_filters_visible_items()
    {
        var (env, model, prefs) = Setup();
        using var _e = env;
        await model.Load();
        Assert.Contains(model.VisibleItems(), i => i.Id == "i1");
        prefs.HideFinished = true;
        Assert.DoesNotContain(model.VisibleItems(), i => i.Id == "i1");
    }

    [Fact]
    public async Task Switching_library_resets_books_and_series()
    {
        var (env, model, _) = Setup();
        using var _e = env;
        await model.Load();
        await model.LoadSeries();
        Assert.Single(model.Series);

        await model.Select(model.Libraries[1]);
        Assert.Equal(["p1"], model.Items.Select(i => i.Id));
        Assert.Empty(model.Series);
    }

    [Fact]
    public async Task Series_are_fetched_once_when_all_are_loaded()
    {
        var (env, model, _) = Setup();
        using var _e = env;
        await model.Load();
        await model.LoadSeries();
        await model.LoadSeries();
        Assert.Single(model.Series);
        Assert.Single(env.Handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/series"));
    }

    [Fact]
    public async Task Overlapping_series_loads_fetch_each_page_once()
    {
        var (env, model, _) = Setup();
        using var _e = env;
        await model.Load();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => model.LoadSeries()));
        Assert.Single(model.Series);
        Assert.Single(env.Handler.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("/series"));
    }

    [Fact]
    public async Task A_broken_item_doesnt_break_the_page()
    {
        var (env, model, _) = Setup((req) => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/lib1/items")
            ? StubHandler.Json("{\"results\":[" + Item("ok1") + ",{\"id\":\"bad\",\"media\":{\"metadata\":{\"title\":null}}}," + Item("ok2") + "],\"total\":3,\"limit\":50,\"page\":0}")
            : null!));
        using var _e = env;
        await model.Load();
        Assert.Null(model.Error);
        Assert.Equal(["ok1", "ok2"], model.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Server_with_only_podcasts_says_so()
    {
        var (env, model, _) = Setup(req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/api/libraries")
            ? StubHandler.Json("""{"libraries":[{"id":"p","name":"Pods","mediaType":"podcast"}]}""") : null!));
        using var _e = env;
        await model.Load();
        Assert.Empty(model.Libraries);
        Assert.Equal("This server has no audiobook libraries.", model.Error);
    }

    [Fact]
    public async Task Older_search_reply_is_ignored()
    {
        var slow = new TaskCompletionSource();
        var (env, model, _) = Setup(async req =>
        {
            if (!req.RequestUri!.AbsolutePath.EndsWith("/search")) return null!;
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["q"];
            if (q == "old") await slow.Task;
            return StubHandler.Json($$"""{"book":[{"libraryItem":{{Item(q!)}}}]}""");
        });
        using var _e = env;
        await model.Load();

        var older = model.Search("old");
        await model.Search("new");
        slow.SetResult();
        await older;

        Assert.Equal("new", model.SearchResults!.Books.Single().Id);
        Assert.False(model.Searching);

        await model.Search("   ");
        Assert.Null(model.SearchResults);
    }

    [Fact]
    public async Task Server_error_is_shown()
    {
        var (env, model, _) = Setup(req => Task.FromResult(StubHandler.Status(HttpStatusCode.InternalServerError)));
        using var _e = env;
        await model.Load();
        Assert.Equal("Server error: 500", model.Error);
        Assert.False(model.Loading);
    }
}
