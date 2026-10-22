using System.Text;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core;

public sealed class AudiobookRepository
{
    private readonly AudiobookShelfApi _api;
    private readonly HttpClient _http;

    public UserPreferences Preferences { get; }
    public AudiobookShelfApi Api => _api;

    /// <summary>Returns a local file URI for a downloaded book's cover, if there is one.</summary>
    public Func<string, Uri?>? LocalCoverResolver { get; set; }

    public AudiobookRepository(UserPreferences preferences, HttpClient http)
    {
        Preferences = preferences;
        _http = http;
        _api = new AudiobookShelfApi(preferences, http);
    }

    public async Task<UserResponse> Login(string server, string username, string password)
    {
        var serverUrl = await AudiobookShelfApi.ResolveServer(server, _http);
        Preferences.ServerUrl = serverUrl;
        LoginResponse response;
        try
        {
            response = await _api.Login(username, password);
        }
        catch (ApiException e) when (e.Kind == ApiErrorKind.Unauthorized)
        {
            throw new ApiException(ApiErrorKind.WrongCredentials);
        }
        // Prefer the long-lived token while servers still send it: audio URLs carry the token,
        // and a short-lived one could expire in the middle of a long listening session.
        var token = response.User.Token ?? response.User.AccessToken
            ?? throw new ApiException(ApiErrorKind.Unauthorized);
        Preferences.SaveCredentials(
            serverUrl,
            token,
            response.User.Token is null ? response.User.RefreshToken : null,
            response.User.Id,
            response.User.Username);
        return response.User;
    }

    public void Logout(bool forgetServer = false)
    {
        Preferences.ClearCredentials();
        if (forgetServer) Preferences.ServerUrl = null;
    }

    public async Task<List<Library>> GetLibraries() => (await _api.GetLibraries()).Libraries;

    public Task<LibraryItemsResponse> GetLibraryItems(string libraryId, LibrarySort sort, int page = 0, int limit = 50) =>
        _api.GetLibraryItems(libraryId, page, limit, sort.ApiField(), sort.Descending());

    /// <summary>Books by one author. The server's filter format is "authors.&lt;base64 of the author id&gt;".</summary>
    public async Task<List<LibraryItem>> GetBooks(string libraryId, Author author)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(author.Id));
        return (await _api.GetLibraryItems(libraryId, 0, 200, "media.metadata.title", false, $"authors.{encoded}")).Results;
    }

    public async Task<List<LibraryItem>> GetRecentlyAdded(string libraryId, int limit = 50) =>
        (await _api.GetLibraryItems(libraryId, 0, limit, "addedAt", true)).Results;

    public Task<SearchResponse> Search(string libraryId, string query) => _api.Search(libraryId, query);

    public Task<SeriesListResponse> GetSeries(string libraryId, int page = 0, int limit = 50) =>
        _api.GetSeries(libraryId, page, limit);

    public Task<LibraryItem> GetItem(string itemId) => _api.GetItem(itemId);

    public Task<PlaybackSessionResponse> StartPlaybackSession(string itemId)
    {
        var device = new DeviceInfo
        {
            DeviceId = Preferences.DeviceId,
            ClientName = AppConfig.ClientName,
            ClientVersion = AppConfig.Version,
            Model = AppConfig.DeviceModel,
        };
        return _api.StartPlaybackSession(itemId, new PlayItemRequest { DeviceInfo = device });
    }

    public Task SyncSession(string id, SessionSyncRequest body) => _api.SyncSession(id, body);

    public Task CloseSession(string id, SessionSyncRequest body) => _api.CloseSession(id, body);

    public Task UpdateProgress(string itemId, double currentTime, double duration)
    {
        var progress = duration > 0 ? currentTime / duration : 0;
        return _api.UpdateProgress(itemId, new ProgressUpdateRequest(currentTime, duration, progress, progress >= 0.99));
    }

    public Task<MediaProgressResponse> GetProgress(string itemId) => _api.GetProgress(itemId);

    /// <summary>Fraction listened (0...1) per library item; finished books are 1.</summary>
    public async Task<Dictionary<string, double>> GetProgressMap()
    {
        var map = new Dictionary<string, double>();
        foreach (var e in (await _api.GetMe()).MediaProgress ?? [])
            if (e.EpisodeId is null)
                map[e.LibraryItemId] = e.IsFinished == true ? 1 : e.Progress ?? 0;
        return map;
    }

    public async Task<List<Bookmark>> GetBookmarks(string itemId) =>
        ((await _api.GetMe()).Bookmarks ?? []).Where(b => b.LibraryItemId == itemId).OrderBy(b => b.Time).ToList();

    public Task<Bookmark> CreateBookmark(string itemId, int time, string title) => _api.CreateBookmark(itemId, time, title);

    public Task UpdateBookmark(string itemId, int time, string title) => _api.UpdateBookmark(itemId, time, title);

    public Task DeleteBookmark(string itemId, int time) => _api.DeleteBookmark(itemId, time);

    public async Task<List<LibraryItem>> GetContinueListening() => (await _api.GetItemsInProgress()).LibraryItems;

    public string? ServerUrl => Preferences.ServerUrl;
    public string? Token => Preferences.Token;

    /// <summary>Full, authorized URL for a track's contentUrl (which may be relative and contain spaces).</summary>
    public Uri? MediaUrl(string contentUrl)
    {
        if (ServerUrl is not { } server || Token is not { } token) return null;
        var full = contentUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? contentUrl : server + contentUrl;
        full = full.Replace(" ", "%20").Replace("#", "%23");
        var sep = full.Contains('?') ? "&" : "?";
        return Uri.TryCreate($"{full}{sep}token={Uri.EscapeDataString(token)}", UriKind.Absolute, out var u) ? u : null;
    }

    public Uri? CoverUrl(string itemId)
    {
        if (LocalCoverResolver?.Invoke(itemId) is { } local) return local;
        if (ServerUrl is not { } server) return null;
        var str = $"{server}/api/items/{itemId}/cover";
        if (Token is { } token) str += $"?token={Uri.EscapeDataString(token)}";
        return Uri.TryCreate(str, UriKind.Absolute, out var u) ? u : null;
    }
}
