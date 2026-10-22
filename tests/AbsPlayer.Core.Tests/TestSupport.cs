using System.Net;
using System.Text;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Tests;

/// <summary>Fake HTTP server: each request goes to <see cref="Respond"/>, and every request is recorded.</summary>
public sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Respond { get; set; } =
        _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        lock (Requests) Requests.Add((request, body));
        var response = await Respond(request);
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}

/// <summary>A temp folder with fresh settings and an in-memory secret store, deleted afterwards.</summary>
public sealed class TestEnv : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "absplayer-tests", Guid.NewGuid().ToString("N"));
    public StubHandler Handler { get; } = new();
    public InMemorySecretStore Secrets { get; } = new();
    public UserPreferences Prefs { get; }
    public HttpClient Http { get; }
    public AudiobookRepository Repo { get; }

    public TestEnv(string? server = "http://abs.test/audiobookshelf", string? token = "tok")
    {
        Prefs = new UserPreferences(new SettingsStore(Path.Combine(Dir, "settings.json")), Secrets);
        if (server is not null) Prefs.ServerUrl = server;
        if (token is not null) Prefs.Token = token;
        Http = new HttpClient(Handler);
        Repo = new AudiobookRepository(Prefs, Http);
    }

    public void Dispose()
    {
        Http.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}
