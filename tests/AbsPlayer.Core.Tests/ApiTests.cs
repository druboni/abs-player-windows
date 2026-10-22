using System.Net;
using System.Text.Json;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;

namespace AbsPlayer.Core.Tests;

public class ApiTests
{
    [Fact]
    public async Task Requests_carry_bearer_token_and_keep_server_subpath()
    {
        using var env = new TestEnv();
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Json("""{"libraries":[{"id":"l1","name":"Books","icon":"audiobookshelf"}]}"""));

        var libs = await env.Repo.GetLibraries();

        Assert.Equal("Books", libs.Single().Name);
        var req = env.Handler.Requests.Single().Request;
        Assert.Equal("http://abs.test/audiobookshelf/api/libraries", req.RequestUri!.ToString());
        Assert.Equal("Bearer tok", req.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task Library_items_query_is_built_from_sort()
    {
        using var env = new TestEnv();
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Json("""{"results":[],"total":0,"limit":50,"page":0}"""));

        await env.Repo.GetLibraryItems("lib", Storage.LibrarySort.RecentlyAdded, page: 2);
        await env.Repo.GetBooks("lib", new Author("au_1", "X", null));

        var urls = env.Handler.Requests.Select(r => r.Request.RequestUri!.Query).ToList();
        Assert.Equal("?limit=50&page=2&sort=addedAt&desc=1", urls[0]);
        Assert.Contains("filter=authors.YXVfMQ%3D%3D", urls[1]); // base64("au_1")
    }

    [Fact]
    public async Task Expired_access_token_is_refreshed_once_for_concurrent_requests()
    {
        using var env = new TestEnv(token: "old");
        env.Prefs.RefreshToken = "r1";
        var refreshes = 0;
        env.Handler.Respond = async req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("/auth/refresh"))
            {
                Interlocked.Increment(ref refreshes);
                Assert.Equal("r1", req.Headers.GetValues("x-refresh-token").Single());
                Assert.Null(req.Headers.Authorization);
                await Task.Delay(50); // Let the other requests pile up behind this refresh.
                return StubHandler.Json("""{"user":{"accessToken":"new","refreshToken":"r2"}}""");
            }
            return req.Headers.Authorization?.Parameter == "new"
                ? StubHandler.Json("""{"libraries":[]}""")
                : StubHandler.Status(HttpStatusCode.Unauthorized);
        };

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => env.Repo.GetLibraries()));

        Assert.Equal(1, refreshes);
        Assert.Equal("new", env.Prefs.Token);
        Assert.Equal("r2", env.Prefs.RefreshToken);
    }

    [Fact]
    public async Task Unauthorized_without_refresh_token_raises_session_expired()
    {
        using var env = new TestEnv();
        var expired = 0;
        env.Repo.Api.SessionExpired += (_, _) => expired++;
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Status(HttpStatusCode.Unauthorized));

        var e = await Assert.ThrowsAsync<ApiException>(() => env.Repo.GetLibraries());

        Assert.Equal(ApiErrorKind.Unauthorized, e.Kind);
        Assert.Equal(1, expired);
    }

    [Fact]
    public async Task Rejected_refresh_raises_session_expired()
    {
        using var env = new TestEnv();
        env.Prefs.RefreshToken = "dead";
        var expired = 0;
        env.Repo.Api.SessionExpired += (_, _) => expired++;
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Status(HttpStatusCode.Unauthorized));

        var e = await Assert.ThrowsAsync<ApiException>(() => env.Repo.GetLibraries());

        Assert.Equal(ApiErrorKind.Unauthorized, e.Kind);
        Assert.True(expired >= 1);
        Assert.Equal("tok", env.Prefs.Token); // Not cleared here; the app decides what to do.
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ApiErrorKind.ServerError)]
    [InlineData(HttpStatusCode.NotFound, ApiErrorKind.ServerError)]
    public async Task Http_errors_map_to_server_error(HttpStatusCode code, ApiErrorKind kind)
    {
        using var env = new TestEnv();
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Status(code));
        var e = await Assert.ThrowsAsync<ApiException>(() => env.Repo.GetLibraries());
        Assert.Equal(kind, e.Kind);
        Assert.Equal((int)code, e.StatusCode);
    }

    [Fact]
    public async Task Bad_json_is_a_decoding_error_and_network_failure_is_network_error()
    {
        using var env = new TestEnv();
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Json("<html>"));
        Assert.Equal(ApiErrorKind.Decoding, (await Assert.ThrowsAsync<ApiException>(() => env.Repo.GetLibraries())).Kind);

        env.Handler.Respond = _ => throw new HttpRequestException("no route");
        Assert.Equal(ApiErrorKind.Network, (await Assert.ThrowsAsync<ApiException>(() => env.Repo.GetLibraries())).Kind);
    }

    [Fact]
    public async Task Missing_server_url_is_invalid_url()
    {
        using var env = new TestEnv(server: null);
        Assert.Equal(ApiErrorKind.InvalidUrl, (await Assert.ThrowsAsync<ApiException>(() => env.Repo.GetLibraries())).Kind);
    }

    private const string StatusJson = """{"app":"audiobookshelf","serverVersion":"2.37.1","isInit":true}""";

    [Fact]
    public async Task Resolve_server_tries_https_then_http_and_follows_redirects()
    {
        using var env = new TestEnv(server: null);
        env.Handler.Respond = req => Task.FromResult(req.RequestUri!.Scheme == "https"
            ? throw new HttpRequestException("tls")
            : StubHandler.Json(StatusJson));
        Assert.Equal("http://box:13378", await AudiobookShelfApi.ResolveServer(" box:13378/ ", env.Http));

        // Redirected (e.g. http→https behind a proxy): use where we ended up.
        env.Handler.Respond = req =>
        {
            var r = StubHandler.Json(StatusJson);
            r.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://books.example.com/abs/status");
            return Task.FromResult(r);
        };
        Assert.Equal("https://books.example.com/abs", await AudiobookShelfApi.ResolveServer("http://books.example.com/abs", env.Http));
    }

    [Fact]
    public async Task Resolve_server_rejects_things_that_are_not_audiobookshelf()
    {
        using var env = new TestEnv(server: null);
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Json("""{"hello":"world"}"""));
        Assert.Equal(ApiErrorKind.NotAudiobookshelf, (await Assert.ThrowsAsync<ApiException>(() => AudiobookShelfApi.ResolveServer("http://x", env.Http))).Kind);

        Assert.Equal(ApiErrorKind.InvalidUrl, (await Assert.ThrowsAsync<ApiException>(() => AudiobookShelfApi.ResolveServer("   ", env.Http))).Kind);
        Assert.Equal(ApiErrorKind.InvalidUrl, (await Assert.ThrowsAsync<ApiException>(() => AudiobookShelfApi.ResolveServer("ftp://x", env.Http))).Kind);
    }

    [Fact]
    public async Task Login_prefers_long_lived_token_and_asks_for_token_pair()
    {
        using var env = new TestEnv(server: null, token: null);
        env.Handler.Respond = req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/status")
            ? StubHandler.Json(StatusJson)
            : StubHandler.Json("""{"user":{"id":"u1","username":"brian","token":"long","accessToken":"short","refreshToken":"r"}}"""));

        var user = await env.Repo.Login("http://abs.test", "brian", "pw");

        Assert.Equal("u1", user.Id);
        Assert.Equal("long", env.Prefs.Token);
        Assert.Null(env.Prefs.RefreshToken);
        Assert.Equal("http://abs.test", env.Prefs.ServerUrl);
        var (login, body) = env.Handler.Requests.Last();
        Assert.Equal("true", login.Headers.GetValues("x-return-tokens").Single());
        Assert.Null(login.Headers.Authorization);
        Assert.Equal("pw", JsonDocument.Parse(body!).RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public async Task Login_keeps_refresh_token_when_only_short_lived_tokens_are_issued()
    {
        using var env = new TestEnv(server: null, token: null);
        env.Handler.Respond = req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/status")
            ? StubHandler.Json(StatusJson)
            : StubHandler.Json("""{"user":{"id":"u1","username":"brian","accessToken":"short","refreshToken":"r"}}"""));

        await env.Repo.Login("http://abs.test", "brian", "pw");

        Assert.Equal("short", env.Prefs.Token);
        Assert.Equal("r", env.Prefs.RefreshToken);
        Assert.True(env.Prefs.IsLoggedIn);

        env.Repo.Logout();
        Assert.False(env.Prefs.IsLoggedIn);
        Assert.Null(env.Prefs.RefreshToken);
        Assert.Equal("http://abs.test", env.Prefs.ServerUrl); // Server is remembered.
    }

    [Fact]
    public async Task Wrong_password_is_reported_as_wrong_credentials()
    {
        using var env = new TestEnv(server: null, token: null);
        env.Handler.Respond = req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("/status")
            ? StubHandler.Json(StatusJson)
            : StubHandler.Status(HttpStatusCode.Unauthorized));

        var e = await Assert.ThrowsAsync<ApiException>(() => env.Repo.Login("http://abs.test", "brian", "nope"));
        Assert.Equal(ApiErrorKind.WrongCredentials, e.Kind);
    }

    [Fact]
    public async Task Session_sync_and_progress_bodies()
    {
        using var env = new TestEnv();
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Json("{}"));

        await env.Repo.SyncSession("s1", new SessionSyncRequest(120.5, 10, 3600));
        await env.Repo.UpdateProgress("i1", 3590, 3600);

        var (sync, syncBody) = env.Handler.Requests[0];
        Assert.Equal(HttpMethod.Post, sync.Method);
        Assert.EndsWith("/api/session/s1/sync", sync.RequestUri!.AbsolutePath);
        Assert.Equal(120.5, JsonDocument.Parse(syncBody!).RootElement.GetProperty("currentTime").GetDouble());

        var (patch, patchBody) = env.Handler.Requests[1];
        Assert.Equal(HttpMethod.Patch, patch.Method);
        Assert.True(JsonDocument.Parse(patchBody!).RootElement.GetProperty("isFinished").GetBoolean()); // ≥ 99%
    }

    [Fact]
    public async Task Progress_map_and_bookmarks_come_from_me()
    {
        using var env = new TestEnv();
        env.Handler.Respond = _ => Task.FromResult(StubHandler.Json("""
            {"mediaProgress":[
               {"libraryItemId":"a","progress":0.25,"isFinished":false},
               {"libraryItemId":"b","progress":0.5,"isFinished":true},
               {"libraryItemId":"pod","episodeId":"e1","progress":0.9}],
             "bookmarks":[
               {"libraryItemId":"a","title":"later","time":500,"createdAt":1},
               {"libraryItemId":"z","title":"other","time":1},
               {"libraryItemId":"a","title":"earlier","time":20}]}
            """));

        var map = await env.Repo.GetProgressMap();
        Assert.Equal(0.25, map["a"]);
        Assert.Equal(1, map["b"]);
        Assert.False(map.ContainsKey("pod"));

        var marks = await env.Repo.GetBookmarks("a");
        Assert.Equal(["earlier", "later"], marks.Select(m => m.Title));
        Assert.Equal("a-20", marks[0].Id);
    }

    [Fact]
    public void Media_and_cover_urls_carry_the_token()
    {
        using var env = new TestEnv(token: "a+b");
        Assert.Equal("http://abs.test/audiobookshelf/api/items/i/file/1?token=a%2Bb",
            env.Repo.MediaUrl("/api/items/i/file/1")!.AbsoluteUri);
        Assert.Equal("http://abs.test/audiobookshelf/s/item/My%20Book/01%20%23Intro.mp3?x=1&token=a%2Bb",
            env.Repo.MediaUrl("/s/item/My Book/01 #Intro.mp3?x=1")!.AbsoluteUri);
        Assert.Equal("https://cdn.example/f.mp3?token=a%2Bb", env.Repo.MediaUrl("https://cdn.example/f.mp3")!.AbsoluteUri);
        Assert.Equal("http://abs.test/audiobookshelf/api/items/i/cover?token=a%2Bb", env.Repo.CoverUrl("i")!.AbsoluteUri);

        env.Repo.LocalCoverResolver = id => new Uri($"file:///C:/covers/{id}.jpg");
        Assert.Equal("file:///C:/covers/i.jpg", env.Repo.CoverUrl("i")!.AbsoluteUri);
    }
}
