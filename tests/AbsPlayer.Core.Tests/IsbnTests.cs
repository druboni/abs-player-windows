using System.Net;
using AbsPlayer.Core.Requester;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Tests;

public class IsbnTests
{
    [Theory]
    [InlineData("9780553418026", "9780553418026")]
    [InlineData("978-0-553-41802-6", "9780553418026")]
    [InlineData(" 978 0553 418026 ", "9780553418026")]
    [InlineData("0553418025", "0553418025")]
    [InlineData("080442957X", "080442957X")]
    [InlineData("080442957x", "080442957X")]
    public void Valid_isbns_parse(string input, string expected) => Assert.Equal(expected, IsbnLookup.Parse(input));

    [Theory]
    [InlineData("9780553418027")]   // Wrong check digit.
    [InlineData("0553418026")]      // Wrong check digit.
    [InlineData("1234567890123")]   // Not 978/979.
    [InlineData("the martian")]
    [InlineData("12345")]
    [InlineData("")]
    public void Other_text_isnt_an_isbn(string input) => Assert.Null(IsbnLookup.Parse(input));

    /// <summary>Open Library's books API, as the stub answers it.</summary>
    private static void OpenLibrary(TestEnv env, Func<HttpRequestMessage, HttpResponseMessage?>? abs = null)
    {
        env.Handler.Respond = req =>
        {
            if (req.RequestUri!.Host == "openlibrary.test")
            {
                var key = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query)["bibkeys"];
                return Task.FromResult(key == "ISBN:9780553418026"
                    ? StubHandler.Json("""{"ISBN:9780553418026":{"title":"The Martian: A Novel","authors":[{"name":"Andy Weir"},{"name":"Andy Weir"}]}}""")
                    : StubHandler.Json("{}"));
            }
            return Task.FromResult(abs?.Invoke(req) ?? StubHandler.Status(HttpStatusCode.NotFound));
        };
    }

    [Fact]
    public async Task Lookup_reads_open_library_and_caches()
    {
        using var env = new TestEnv();
        OpenLibrary(env);
        var lookup = new IsbnLookup(env.Http, "https://openlibrary.test");

        var info = await lookup.Lookup("9780553418026");
        Assert.Equal("The Martian: A Novel by Andy Weir", info!.Description);
        Assert.Equal("The Martian", info.ShortTitle);
        Assert.Null(await lookup.Lookup("9781491544617"));

        await lookup.Lookup("9780553418026");
        await lookup.Lookup("9781491544617");
        Assert.Equal(2, env.Handler.Requests.Count); // Both answers cached, including "unknown".
    }

    [Fact]
    public async Task Library_search_for_an_isbn_searches_the_books_title()
    {
        using var env = new TestEnv();
        OpenLibrary(env, req => req.RequestUri!.AbsolutePath switch
        {
            "/audiobookshelf/api/libraries" => StubHandler.Json("""{"libraries":[{"id":"lib1","name":"Books","mediaType":"book"}]}"""),
            "/audiobookshelf/api/libraries/lib1/search" => StubHandler.Json("""{"book":[{"libraryItem":{"id":"m1","media":{"metadata":{"title":"The Martian"}}}}]}"""),
            "/audiobookshelf/api/libraries/lib1/items" => StubHandler.Json("""{"results":[],"total":0,"limit":50,"page":0}"""),
            _ => StubHandler.Json("{}"),
        });
        var model = new LibraryModel(env.Repo, new Prefs(env.Prefs.Settings), new IsbnLookup(env.Http, "https://openlibrary.test"));
        await model.Load();

        await model.Search("978-0-553-41802-6");

        Assert.Equal("ISBN 9780553418026 is The Martian: A Novel by Andy Weir.", model.IsbnNote);
        Assert.Equal("m1", model.SearchResults!.Books.Single().Id);
        var search = env.Handler.Requests.Last(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/search")).Request;
        Assert.Contains("q=The%20Martian&", search.RequestUri!.Query); // Title only: Audiobookshelf matches one field at a time.

        await model.Search("9781491544617");
        Assert.Equal("No book found for ISBN 9781491544617.", model.IsbnNote);
        Assert.True(model.SearchResults!.IsEmpty);

        await model.Search("the martian"); // Ordinary text: no note.
        Assert.Null(model.IsbnNote);
    }

    [Fact]
    public async Task Library_search_says_when_open_library_is_down()
    {
        using var env = new TestEnv();
        env.Handler.Respond = req => req.RequestUri!.Host == "openlibrary.test"
            ? throw new HttpRequestException("down")
            : Task.FromResult(req.RequestUri.AbsolutePath.EndsWith("/libraries")
                ? StubHandler.Json("""{"libraries":[{"id":"lib1","name":"Books","mediaType":"book"}]}""")
                : StubHandler.Json("""{"results":[],"total":0,"limit":50,"page":0}"""));
        var model = new LibraryModel(env.Repo, new Prefs(env.Prefs.Settings), new IsbnLookup(env.Http, "https://openlibrary.test"));
        await model.Load();
        await model.Search("9780553418026");
        Assert.StartsWith("Couldn't reach Open Library", model.IsbnNote);
        Assert.True(model.SearchResults!.IsEmpty);
    }

    /// <summary>The real Open Library. Runs only with ISBN_LIVE=1.</summary>
    [Fact]
    public async Task Live_open_library_lookup()
    {
        if (Environment.GetEnvironmentVariable("ISBN_LIVE") != "1") return;
        using var http = new HttpClient();
        var lookup = new IsbnLookup(http);
        Assert.Equal("The Martian by Andy Weir", (await lookup.Lookup("9780553418026"))!.Description);
        Assert.Equal("Project Hail Mary", (await lookup.Lookup("9780593135204"))!.ShortTitle);
        Assert.Null(await lookup.Lookup("9781491544617"));
    }

    [Fact]
    public async Task Requester_says_what_an_isbn_is_and_null_when_unknown()
    {
        using var env = new TestEnv();
        env.Handler.Respond = req => Task.FromResult(req.RequestUri!.AbsolutePath switch
        {
            "/api/ping" => StubHandler.Json("""{"ok":true,"version":"0.9.0"}"""),
            "/api/auth/login" => StubHandler.Json("""{"token":"t","user":{"id":1,"username":"b","role":"user"}}"""),
            "/api/isbn/9780553418026" => StubHandler.Json("""{"isbn":"9780553418026","title":"The Martian","authors":["Andy Weir"]}"""),
            "/api/isbn/9781491544617" => StubHandler.Json("""{"error":"No book found for ISBN 9781491544617"}""", HttpStatusCode.NotFound),
            _ => StubHandler.Status(HttpStatusCode.NotFound),
        });
        var client = new RequesterClient(env.Prefs.Settings, env.Secrets, env.Http);
        await client.Connect("http://req.test", "b", "pw");

        Assert.Equal("The Martian by Andy Weir", (await client.LookupIsbn("9780553418026"))!.Description);
        Assert.Null(await client.LookupIsbn("9781491544617"));
    }
}
