using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Requester;

/// <summary>An error from Audiobook Requester, with its own message ("You've already requested this book").</summary>
public sealed class RequesterException(string message, int? status = null, Exception? inner = null) : Exception(message, inner)
{
    public int? Status { get; } = status;
}

/// <summary>
/// Talks to an Audiobook Requester server. Signing in uses the listener's Audiobookshelf username and
/// password (Requester checks them with Audiobookshelf) and returns a 30-day token kept in the secret store.
/// </summary>
public sealed class RequesterClient(SettingsStore settings, ISecretStore secrets, HttpClient http)
{
    private const string UrlKey = "requester_url";
    private const string UsernameKey = "requester_username";
    private const string RoleKey = "requester_role";
    private const string TokenKey = "requester_token";

    private static readonly JsonSerializerOptions Json = AbsJson.Options;

    /// <summary>Raised when Requester no longer accepts the token (expired or account disabled).</summary>
    public event EventHandler? SignedOut;
    /// <summary>Raised when connecting or disconnecting.</summary>
    public event EventHandler? Changed;

    public string? Url => settings.GetString(UrlKey);
    public string? Username => settings.GetString(UsernameKey);
    public bool IsAdmin => settings.GetString(RoleKey) == "admin";
    public bool IsConnected => Url is not null && secrets.Get(TokenKey) is not null;

    /// <summary>
    /// Finds Requester at what the user typed: adds a scheme if missing (https first),
    /// follows redirects, and checks /api/ping answers like Audiobook Requester.
    /// </summary>
    public static async Task<string> Resolve(string input, HttpClient http)
    {
        var trimmed = input.Trim().TrimEnd('/');
        if (trimmed.Length == 0) throw new RequesterException("Enter the Audiobook Requester address");
        string[] candidates = trimmed.Contains("://") ? [trimmed] : [$"https://{trimmed}", $"http://{trimmed}"];
        Exception last = new RequesterException("Couldn't find Audiobook Requester at that address");
        foreach (var b in candidates)
        {
            if (!Uri.TryCreate($"{b}/api/ping", UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https")) continue;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await http.GetAsync(url, cts.Token);
                if (response.StatusCode == HttpStatusCode.OK
                    && await response.Content.ReadFromJsonAsync<JsonElement>(cts.Token) is { ValueKind: JsonValueKind.Object } ping
                    && ping.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True
                    && ping.TryGetProperty("version", out _))
                {
                    var resolved = (response.RequestMessage?.RequestUri ?? url).AbsoluteUri;
                    return resolved[..resolved.LastIndexOf("/api/ping", StringComparison.Ordinal)];
                }
                last = new RequesterException("That address doesn't look like Audiobook Requester");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
            {
                last = new RequesterException($"Couldn't reach {b}: {e.Message}", inner: e);
            }
        }
        throw last;
    }

    /// <summary>Signs in with Audiobookshelf credentials and remembers the server and token.</summary>
    public async Task<RequesterUser> Connect(string address, string username, string password)
    {
        var url = await Resolve(address, http);
        var login = await Send<RequesterLogin>(HttpMethod.Post, $"{url}/api/auth/login", new { username, password }, auth: false);
        settings.Set(UrlKey, url);
        settings.Set(UsernameKey, login.User.Username);
        settings.Set(RoleKey, login.User.Role);
        secrets.Set(TokenKey, login.Token);
        Changed?.Invoke(this, EventArgs.Empty);
        return login.User;
    }

    public void Disconnect(bool forgetUrl = false)
    {
        secrets.Set(TokenKey, null);
        settings.Remove(RoleKey);
        if (forgetUrl) { settings.Remove(UrlKey); settings.Remove(UsernameKey); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<RequesterMe> Me()
    {
        var me = await Send<RequesterMe>(HttpMethod.Get, Api("auth/me"));
        settings.Set(RoleKey, me.User.Role); // An admin may have changed it.
        return me;
    }

    public async Task<List<RequesterBook>> Search(string query) =>
        query.Trim().Length < 2 ? [] : await Send<List<RequesterBook>>(HttpMethod.Get, Api($"search?q={Uri.EscapeDataString(query.Trim())}"));

    /// <summary>What an ISBN is (Requester looks it up on Open Library), or null when nobody knows it.</summary>
    public async Task<IsbnInfo?> LookupIsbn(string isbn)
    {
        try { return await Send<IsbnInfo>(HttpMethod.Get, Api($"isbn/{Uri.EscapeDataString(isbn)}")); }
        catch (RequesterException e) when (e.Status is 404 or 400) { return null; }
    }

    /// <summary>The caller's own requests, newest first (admins: everyone's when <paramref name="everyone"/>).</summary>
    public Task<List<BookRequest>> Requests(string filter = "all", bool everyone = false) =>
        Send<List<BookRequest>>(HttpMethod.Get, Api($"requests?filter={Uri.EscapeDataString(filter)}{(everyone ? "" : "&mine=1")}"));

    public Task<BookRequest> Request(RequesterBook book, string? note = null) =>
        Send<BookRequest>(HttpMethod.Post, Api("requests"), new { asin = book.Asin, title = book.Title, note });

    public Task Cancel(int requestId) => Send<JsonElement>(HttpMethod.Delete, Api($"requests/{requestId}"));

    public Task<BookRequest> Approve(int requestId) => Send<BookRequest>(HttpMethod.Post, Api($"requests/{requestId}/approve"), new { });

    public Task<BookRequest> Decline(int requestId, string? reason) =>
        Send<BookRequest>(HttpMethod.Post, Api($"requests/{requestId}/decline"), new { reason });

    /// <summary>Search results carry Audible cover URLs; Requester's own covers are relative.</summary>
    public Uri? CoverUrl(string? cover)
    {
        if (string.IsNullOrEmpty(cover)) return null;
        if (Uri.TryCreate(cover, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https") return abs;
        return Url is { } url && Uri.TryCreate($"{url}/{cover.TrimStart('/')}", UriKind.Absolute, out var rel) ? rel : null;
    }

    private string Api(string path) =>
        Url is { } url ? $"{url}/api/{path}" : throw new RequesterException("Audiobook Requester isn't set up. Add it in Settings.");

    private async Task<T> Send<T>(HttpMethod method, string url, object? body = null, bool auth = true)
    {
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        if (auth)
        {
            var token = secrets.Get(TokenKey) ?? throw new RequesterException("Sign in to Audiobook Requester in Settings.", 401);
            req.Headers.Authorization = new("Bearer", token);
        }
        HttpResponseMessage response;
        try { response = await http.SendAsync(req); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new RequesterException($"Couldn't reach Audiobook Requester: {e.Message}", inner: e);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && auth)
                {
                    secrets.Set(TokenKey, null);
                    SignedOut?.Invoke(this, EventArgs.Empty);
                }
                throw new RequesterException(ErrorMessage(text) ?? $"Audiobook Requester answered {(int)response.StatusCode}", (int)response.StatusCode);
            }
            try
            {
                return JsonSerializer.Deserialize<T>(text.Length == 0 ? "{}" : text, Json)
                    ?? throw new JsonException("Empty response");
            }
            catch (JsonException e) { throw new RequesterException("Audiobook Requester sent something unexpected", inner: e); }
        }
    }

    /// <summary>Requester errors are {"error": "..."}.</summary>
    private static string? ErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
