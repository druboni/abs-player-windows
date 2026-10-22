using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Api;

public sealed class AudiobookShelfApi(UserPreferences preferences, HttpClient http)
{
    private static readonly JsonSerializerOptions Json = AbsJson.Options;

    private readonly Lock _refreshLock = new();
    private Task<bool>? _refreshInFlight;

    /// <summary>Raised when the server rejects our sign-in and it can't be refreshed.</summary>
    public event EventHandler? SessionExpired;

    private string BaseUrl()
    {
        var str = preferences.ServerUrl?.Trim('/');
        if (string.IsNullOrEmpty(str) || !Uri.TryCreate(str, UriKind.Absolute, out _))
            throw new ApiException(ApiErrorKind.InvalidUrl);
        return str;
    }

    private Func<HttpRequestMessage> Request(string path, HttpMethod? method = null,
        IEnumerable<(string Name, string Value)>? query = null, object? body = null, bool auth = true)
    {
        var url = $"{BaseUrl()}/{path}";
        if (query is not null)
        {
            var qs = string.Join("&", query.Select(q => $"{Uri.EscapeDataString(q.Name)}={Uri.EscapeDataString(q.Value)}"));
            if (qs.Length > 0) url += "?" + qs;
        }
        // A factory, because a request can only be sent once and a 401 retry needs a fresh copy.
        return () =>
        {
            var req = new HttpRequestMessage(method ?? HttpMethod.Get, url);
            if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
            if (auth && preferences.Token is { } token)
                req.Headers.Authorization = new("Bearer", token);
            return req;
        };
    }

    private async Task<byte[]> Send(Func<HttpRequestMessage> build, bool allowRefresh = true, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(build(), ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { throw new ApiException(ApiErrorKind.Network, inner: e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new ApiException(ApiErrorKind.Network, inner: e); }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Short-lived access token expired: refresh once and retry.
                if (allowRefresh && preferences.RefreshToken is not null && await RefreshOnce().ConfigureAwait(false))
                    return await Send(build, allowRefresh: false, ct).ConfigureAwait(false);
                if (preferences.Token is not null) SessionExpired?.Invoke(this, EventArgs.Empty);
                throw new ApiException(ApiErrorKind.Unauthorized, 401);
            }
            if (!response.IsSuccessStatusCode)
                throw new ApiException(ApiErrorKind.ServerError, (int)response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Several requests failing at once share a single refresh.</summary>
    private Task<bool> RefreshOnce()
    {
        lock (_refreshLock)
        {
            if (_refreshInFlight is { } running) return running;
            var task = PerformRefresh();
            _refreshInFlight = task;
            task.ContinueWith(_ => { lock (_refreshLock) _refreshInFlight = null; }, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Exchanges the refresh token for a new access token. Returns false if the server refused it.</summary>
    private async Task<bool> PerformRefresh()
    {
        if (preferences.RefreshToken is not { } refreshToken) return false;
        try
        {
            var build = Request("auth/refresh", HttpMethod.Post, auth: false);
            var data = await Send(() =>
            {
                var req = build();
                req.Headers.Add("x-refresh-token", refreshToken);
                return req;
            }, allowRefresh: false).ConfigureAwait(false);
            if (JsonSerializer.Deserialize<RefreshResponse>(data, Json) is not { } r) return false;
            if (r.User.RefreshToken is { } next) preferences.RefreshToken = next;
            preferences.Token = r.User.AccessToken;
            return true;
        }
        catch (Exception e) when (e is ApiException or JsonException)
        {
            return false;
        }
    }

    private async Task<T> Perform<T>(Func<HttpRequestMessage> build, CancellationToken ct = default)
    {
        var data = await Send(build, ct: ct).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(data, Json)
                ?? throw new JsonException("Empty response");
        }
        catch (JsonException e) { throw new ApiException(ApiErrorKind.Decoding, inner: e); }
    }

    private Task PerformVoid(Func<HttpRequestMessage> build, CancellationToken ct = default) => Send(build, ct: ct);

    private sealed record Status(bool? IsInit, string? ServerVersion);

    /// <summary>
    /// Finds the server's real base URL: adds a scheme if missing (https first), follows
    /// redirects such as http→https, and checks it really is Audiobookshelf via /status.
    /// </summary>
    public static async Task<string> ResolveServer(string input, HttpClient http)
    {
        var trimmed = input.Trim().TrimEnd('/');
        if (trimmed.Length == 0) throw new ApiException(ApiErrorKind.InvalidUrl);
        string[] candidates = trimmed.Contains("://") ? [trimmed] : [$"https://{trimmed}", $"http://{trimmed}"];

        Exception lastError = new ApiException(ApiErrorKind.NotAudiobookshelf);
        foreach (var b in candidates)
        {
            if (!Uri.TryCreate($"{b}/status", UriKind.Absolute, out var url)
                || url.Scheme is not ("http" or "https"))
            {
                lastError = new ApiException(ApiErrorKind.InvalidUrl);
                continue;
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                using var response = await http.GetAsync(url, cts.Token).ConfigureAwait(false);
                Status? status = null;
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    try { status = await response.Content.ReadFromJsonAsync<Status>(Json, cts.Token).ConfigureAwait(false); }
                    catch (JsonException) { }
                }
                if (status is null || (status.IsInit is null && status.ServerVersion is null))
                {
                    lastError = new ApiException(ApiErrorKind.NotAudiobookshelf);
                    continue;
                }
                var resolved = (response.RequestMessage?.RequestUri ?? url).AbsoluteUri;
                return resolved.EndsWith("/status") ? resolved[..^"/status".Length] : resolved;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                lastError = new ApiException(ApiErrorKind.Network, inner: e);
            }
        }
        throw lastError;
    }

    /// <summary>The server's Audiobookshelf version from /status, or null if it can't be reached.</summary>
    public static async Task<string?> ServerVersion(string baseUrl, HttpClient http)
    {
        try { return (await http.GetFromJsonAsync<Status>($"{baseUrl}/status", Json).ConfigureAwait(false))?.ServerVersion; }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }

    public async Task<LoginResponse> Login(string username, string password)
    {
        var build = Request("login", HttpMethod.Post, body: new LoginRequest { Username = username, Password = password }, auth: false);
        return await Perform<LoginResponse>(() =>
        {
            var req = build();
            // Newer servers return an access/refresh token pair when asked; older ones ignore this.
            req.Headers.Add("x-return-tokens", "true");
            return req;
        }).ConfigureAwait(false);
    }

    public Task<LibrariesResponse> GetLibraries() => Perform<LibrariesResponse>(Request("api/libraries"));

    public Task<LibraryItemsResponse> GetLibraryItems(string libraryId, int page, int limit, string sort, bool descending, string? filter = null)
    {
        var query = new List<(string, string)>
        {
            ("limit", $"{limit}"), ("page", $"{page}"), ("sort", sort), ("desc", descending ? "1" : "0"),
        };
        if (filter is not null) query.Add(("filter", filter));
        return Perform<LibraryItemsResponse>(Request($"api/libraries/{libraryId}/items", query: query));
    }

    public async Task<SearchResponse> Search(string libraryId, string query) =>
        (await Perform<SearchResponse.Raw>(Request($"api/libraries/{libraryId}/search",
            query: [("q", query), ("limit", "25")])).ConfigureAwait(false)).ToResponse();

    public Task<SeriesListResponse> GetSeries(string libraryId, int page, int limit) =>
        Perform<SeriesListResponse>(Request($"api/libraries/{libraryId}/series",
            query: [("limit", $"{limit}"), ("page", $"{page}"), ("sort", "name")]));

    public Task<LibraryItem> GetItem(string itemId) =>
        Perform<LibraryItem>(Request($"api/items/{itemId}", query: [("expanded", "1")]));

    public Task<PlaybackSessionResponse> StartPlaybackSession(string itemId, PlayItemRequest body) =>
        Perform<PlaybackSessionResponse>(Request($"api/items/{itemId}/play", HttpMethod.Post, body: body));

    public Task SyncSession(string id, SessionSyncRequest body) =>
        PerformVoid(Request($"api/session/{id}/sync", HttpMethod.Post, body: body));

    public Task CloseSession(string id, SessionSyncRequest body) =>
        PerformVoid(Request($"api/session/{id}/close", HttpMethod.Post, body: body));

    public Task UpdateProgress(string itemId, ProgressUpdateRequest body) =>
        PerformVoid(Request($"api/me/progress/{itemId}", HttpMethod.Patch, body: body));

    public Task<MediaProgressResponse> GetProgress(string itemId) =>
        Perform<MediaProgressResponse>(Request($"api/me/progress/{itemId}"));

    public Task<Bookmark> CreateBookmark(string itemId, int time, string title) =>
        Perform<Bookmark>(Request($"api/me/item/{itemId}/bookmark", HttpMethod.Post, body: new BookmarkRequest(time, title)));

    public Task UpdateBookmark(string itemId, int time, string title) =>
        PerformVoid(Request($"api/me/item/{itemId}/bookmark", HttpMethod.Patch, body: new BookmarkRequest(time, title)));

    public Task DeleteBookmark(string itemId, int time) =>
        PerformVoid(Request($"api/me/item/{itemId}/bookmark/{time}", HttpMethod.Delete));

    public Task<MeResponse> GetMe() => Perform<MeResponse>(Request("api/me"));

    public Task<InProgressResponse> GetItemsInProgress() => Perform<InProgressResponse>(Request("api/me/items-in-progress"));
}
