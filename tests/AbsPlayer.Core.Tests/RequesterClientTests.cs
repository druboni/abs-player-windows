using System.Net;
using System.Text.Json;
using AbsPlayer.Core.Requester;

namespace AbsPlayer.Core.Tests;

public class RequesterClientTests
{
    private const string Ping = """{"ok":true,"version":"0.8.0"}""";
    private const string Login = """{"token":"req-token","user":{"id":3,"username":"brian","role":"admin","autoApprove":true}}""";

    /// <summary>A fake Requester at http://req.test:5066 (https isn't answering, like a LAN box).</summary>
    private static (TestEnv Env, RequesterClient Client) Setup(Func<HttpRequestMessage, string, HttpResponseMessage?>? route = null)
    {
        var env = new TestEnv();
        var client = new RequesterClient(env.Prefs.Settings, env.Secrets, env.Http);
        env.Handler.Respond = async req =>
        {
            if (req.RequestUri!.Scheme == "https") throw new HttpRequestException("no TLS here");
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync();
            if (route?.Invoke(req, body) is { } r) return r;
            return req.RequestUri.AbsolutePath switch
            {
                "/api/ping" => StubHandler.Json(Ping),
                "/api/auth/login" => StubHandler.Json(Login),
                _ => StubHandler.Status(HttpStatusCode.NotFound),
            };
        };
        return (env, client);
    }

    private static async Task<(TestEnv, RequesterClient)> Connected(Func<HttpRequestMessage, string, HttpResponseMessage?>? route = null)
    {
        var (env, client) = Setup(route);
        await client.Connect("req.test:5066", "brian", "pw");
        return (env, client);
    }

    [Fact]
    public async Task Connect_finds_the_server_signs_in_and_keeps_the_token_secret()
    {
        var (env, client) = Setup();
        using var _ = env;
        var changed = 0;
        client.Changed += (_, _) => changed++;

        var user = await client.Connect(" req.test:5066/ ", "brian", "pw");

        Assert.True(user.IsAdmin);
        Assert.True(client.IsConnected);
        Assert.True(client.IsAdmin);
        Assert.Equal("http://req.test:5066", client.Url);
        Assert.Equal("brian", client.Username);
        Assert.Equal("req-token", env.Secrets.Get("requester_token"));
        Assert.DoesNotContain("req-token", File.ReadAllText(Path.Combine(env.Dir, "settings.json")));
        var login = env.Handler.Requests.Single(r => r.Request.RequestUri!.AbsolutePath == "/api/auth/login");
        Assert.Null(login.Request.Headers.Authorization);
        Assert.Equal("pw", JsonDocument.Parse(login.Body!).RootElement.GetProperty("password").GetString());
        Assert.Equal(1, changed);
    }

    [Fact]
    public async Task Something_that_is_not_requester_is_refused()
    {
        var (env, client) = Setup((req, _) => req.RequestUri!.AbsolutePath == "/api/ping" ? StubHandler.Json("""{"app":"audiobookshelf"}""") : null);
        using var _ = env;
        var e = await Assert.ThrowsAsync<RequesterException>(() => client.Connect("req.test", "brian", "pw"));
        Assert.Contains("doesn't look like Audiobook Requester", e.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Wrong_password_shows_requesters_message()
    {
        var (env, client) = Setup((req, _) => req.RequestUri!.AbsolutePath == "/api/auth/login"
            ? StubHandler.Json("""{"error":"Wrong Audiobookshelf username or password"}""", HttpStatusCode.Unauthorized) : null);
        using var _ = env;
        var e = await Assert.ThrowsAsync<RequesterException>(() => client.Connect("req.test:5066", "brian", "nope"));
        Assert.Equal("Wrong Audiobookshelf username or password", e.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Search_parses_books_and_covers()
    {
        var (env, client) = await Connected((req, _) => req.RequestUri!.AbsolutePath == "/api/search"
            ? StubHandler.Json("""
                [{"asin":"B0ABCDEFGH","title":"Dungeon Crawler Carl","authors":["Matt Dinniman"],"narrators":["Jeff Hays"],"series":"DCC","seriesNumber":"1",
                  "cover":"https://m.media-amazon.com/images/I/x.jpg","runtimeMin":812.5,"status":"none","requestId":null,"requestStatus":null,
                  "rating":{"overall":4.8,"performance":4.9,"story":4.7,"count":120345},"genres":["Fantasy"]},
                 {"asin":"B0ZZZZZZZZ","title":"In Library","authors":[],"narrators":[],"cover":"/api/cover/B0ZZZZZZZZ","status":"available","rating":null}]
                """) : null);
        using var _ = env;

        var books = await client.Search("dungeon crawler");

        Assert.Equal(2, books.Count);
        var dcc = books[0];
        Assert.True(dcc.CanRequest);
        Assert.Equal(4.8, dcc.Rating!.Overall);
        Assert.Equal(812.5, dcc.RuntimeMin);
        Assert.Equal("https://m.media-amazon.com/images/I/x.jpg", client.CoverUrl(dcc.Cover)!.AbsoluteUri);
        Assert.False(books[1].CanRequest);
        Assert.Equal("http://req.test:5066/api/cover/B0ZZZZZZZZ", client.CoverUrl(books[1].Cover)!.AbsoluteUri);

        var call = env.Handler.Requests.Last().Request;
        Assert.Equal("?q=dungeon%20crawler", call.RequestUri!.Query);
        Assert.Equal("Bearer req-token", call.Headers.Authorization!.ToString());
        Assert.Empty(await client.Search("a")); // Too short: no request.
    }

    [Fact]
    public async Task Requesting_sends_asin_title_and_note_and_shows_conflicts()
    {
        var first = true;
        var (env, client) = await Connected((req, body) =>
        {
            if (req.RequestUri!.AbsolutePath != "/api/requests" || req.Method != HttpMethod.Post) return null;
            if (first) { first = false; return StubHandler.Json("""{"id":7,"userId":3,"asin":"B0ABCDEFGH","title":"DCC","authors":["Matt"],"status":"approved"}""", HttpStatusCode.Created); }
            return StubHandler.Json("""{"error":"You've already requested this book"}""", HttpStatusCode.Conflict);
        });
        using var _ = env;
        var book = new RequesterBook { Asin = "B0ABCDEFGH", Title = "DCC" };

        var r = await client.Request(book, "please!");
        Assert.Equal(7, r.Id);
        Assert.Equal("approved", r.Status);
        var sent = JsonDocument.Parse(env.Handler.Requests.First(x => x.Request.Method == HttpMethod.Post && x.Request.RequestUri!.AbsolutePath == "/api/requests").Body!).RootElement;
        Assert.Equal("B0ABCDEFGH", sent.GetProperty("asin").GetString());
        Assert.Equal("please!", sent.GetProperty("note").GetString());

        var e = await Assert.ThrowsAsync<RequesterException>(() => client.Request(book));
        Assert.Equal("You've already requested this book", e.Message);
        Assert.Equal(409, e.Status);
    }

    [Fact]
    public async Task My_requests_include_progress()
    {
        var (env, client) = await Connected((req, _) => req.RequestUri!.AbsolutePath == "/api/requests" && req.Method == HttpMethod.Get
            ? StubHandler.Json("""
                [{"id":9,"userId":3,"username":"brian","asin":"B01","title":"Downloading","authors":["A"],"status":"processing",
                  "stage":{"key":"downloading","label":"Downloading","percent":42,"eta":"5m"},"note":null,"absItemId":null},
                 {"id":8,"userId":3,"asin":"B02","title":"Ready","authors":[],"status":"available","absItemId":"li_123","stage":null}]
                """) : null);
        using var _ = env;

        var list = await client.Requests();

        Assert.Equal(42, list[0].Stage!.Percent);
        Assert.Equal("li_123", list[1].AbsItemId);
        Assert.Equal("?filter=all&mine=1", env.Handler.Requests.Last().Request.RequestUri!.Query);
        await client.Requests("pending", everyone: true);
        Assert.Equal("?filter=pending", env.Handler.Requests.Last().Request.RequestUri!.Query);
    }

    [Fact]
    public async Task Expired_token_signs_out()
    {
        var (env, client) = await Connected((req, _) => req.RequestUri!.AbsolutePath == "/api/auth/me"
            ? StubHandler.Json("""{"error":"Not signed in"}""", HttpStatusCode.Unauthorized) : null);
        using var _ = env;
        var signedOut = 0;
        client.SignedOut += (_, _) => signedOut++;

        await Assert.ThrowsAsync<RequesterException>(() => client.Me());

        Assert.Equal(1, signedOut);
        Assert.False(client.IsConnected);
        Assert.Equal("http://req.test:5066", client.Url); // Address kept so reconnecting is quick.
    }

    [Fact]
    public async Task Admin_approve_decline_and_cancel_hit_the_right_endpoints()
    {
        var (env, client) = await Connected((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/api/requests/")
            ? StubHandler.Json(req.Method == HttpMethod.Delete ? """{"ok":true}""" : """{"id":5,"asin":"B05","title":"T","status":"approved"}""") : null);
        using var _ = env;

        await client.Approve(5);
        await client.Decline(5, "Not available in English");
        await client.Cancel(5);

        var calls = env.Handler.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.StartsWith("/api/requests/")).ToList();
        Assert.Equal(["POST /api/requests/5/approve", "POST /api/requests/5/decline", "DELETE /api/requests/5"],
            calls.Select(r => $"{r.Request.Method} {r.Request.RequestUri!.AbsolutePath}"));
        Assert.Equal("Not available in English", JsonDocument.Parse(calls[1].Body!).RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Disconnect_forgets_the_token()
    {
        var (env, client) = await Connected();
        using var _ = env;
        client.Disconnect();
        Assert.False(client.IsConnected);
        Assert.Null(env.Secrets.Get("requester_token"));
        await Assert.ThrowsAsync<RequesterException>(() => client.Search("anything"));

        client.Disconnect(forgetUrl: true);
        Assert.Null(client.Url);
    }
}
