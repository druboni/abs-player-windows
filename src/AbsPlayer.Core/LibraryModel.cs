using System.Collections.ObjectModel;
using System.ComponentModel;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core;

/// <summary>What the library screen shows: libraries, books page by page, series, search and progress. Use from the UI thread.</summary>
public sealed class LibraryModel(AudiobookRepository repository, Prefs prefs, IsbnLookup? isbnLookup = null) : INotifyPropertyChanged
{
    private int _page;
    private bool _hasMore;
    private int _seriesPage;
    private int _seriesTotal;
    private int _searchVersion;

    public List<Library> Libraries { get; private set; } = [];
    public Library? Selected { get; private set; }
    public ObservableCollection<LibraryItem> Items { get; } = [];
    public List<LibraryItem> ContinueItems { get; private set; } = [];
    public Dictionary<string, double> Progress { get; private set; } = [];
    public bool Loading { get; private set; }
    public string? Error { get; private set; }
    public ObservableCollection<BookSeries> Series { get; } = [];
    public SearchResponse? SearchResults { get; private set; }
    public bool Searching { get; private set; }
    /// <summary>When the search was an ISBN: what it matched, or why nothing was searched.</summary>
    public string? IsbnNote { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public double? ProgressFor(string itemId) => Progress.TryGetValue(itemId, out var p) ? p : null;

    public async Task Load()
    {
        Loading = true; Error = null;
        Notify(nameof(Loading)); Notify(nameof(Error));
        try
        {
            // Only audiobook libraries: podcasts need episode handling this app doesn't have.
            Libraries = (await repository.GetLibraries()).Where(l => l.IsBooks).ToList();
            Notify(nameof(Libraries));
            if (Libraries.Count == 0) { Error = "This server has no audiobook libraries."; Notify(nameof(Error)); }
            await RefreshProgress();
            if (Libraries.FirstOrDefault() is { } first) await Select(first);
        }
        catch (ApiException e) { Error = e.Message; Notify(nameof(Error)); }
        Loading = false;
        Notify(nameof(Loading));
    }

    public async Task RefreshProgress()
    {
        try { Progress = await repository.GetProgressMap(); Notify(nameof(Progress)); }
        catch (ApiException) { }
        try
        {
            var c = await repository.GetContinueListening();
            ContinueItems = c.Where(i => i.MediaType != "podcast" && (ProgressFor(i.Id) ?? 0) < 1).ToList();
            Notify(nameof(ContinueItems));
        }
        catch (ApiException) { }
    }

    /// <summary>Searches the selected library. A slow reply to an older query is ignored.</summary>
    public async Task Search(string query)
    {
        var q = query.Trim();
        var version = ++_searchVersion;
        if (Selected is not { } lib || q.Length == 0)
        {
            SearchResults = null; Searching = false; IsbnNote = null;
            Notify(nameof(IsbnNote)); Notify(nameof(SearchResults)); Notify(nameof(Searching));
            return;
        }
        Searching = true;
        Notify(nameof(Searching));
        SearchResponse? results = null;
        string? note = null;
        // An ISBN: books rarely have one stored, so look up the title and search for that.
        if (isbnLookup is not null && IsbnLookup.Parse(q) is { } isbn)
        {
            try
            {
                if (await isbnLookup.Lookup(isbn) is { } info)
                {
                    note = $"ISBN {isbn} is {info.Description}.";
                    q = info.ShortTitle;
                }
                else
                {
                    note = $"No book found for ISBN {isbn}.";
                    results = new SearchResponse { Books = [], Series = [], Authors = [] };
                }
            }
            catch (HttpRequestException)
            {
                note = "Couldn't reach Open Library to look up that ISBN. Try the title instead.";
                results = new SearchResponse { Books = [], Series = [], Authors = [] };
            }
            if (version != _searchVersion) return;
        }
        if (results is null)
        {
            try { results = await repository.Search(lib.Id, q); }
            catch (ApiException) { }
        }
        if (version != _searchVersion) return;
        SearchResults = results;
        IsbnNote = note;
        Searching = false;
        Notify(nameof(IsbnNote)); Notify(nameof(SearchResults)); Notify(nameof(Searching));
    }

    private bool _seriesLoading;

    /// <summary>Fetches the next page of series. Calls while one is in flight are ignored.</summary>
    public async Task LoadSeries(bool reset = false)
    {
        if (Selected is not { } lib) return;
        if (reset) { Series.Clear(); _seriesPage = 0; _seriesTotal = 0; }
        if (_seriesLoading || (_seriesPage > 0 && Series.Count >= _seriesTotal)) return;
        _seriesLoading = true;
        try
        {
            var r = await repository.GetSeries(lib.Id, _seriesPage, 50);
            if (Selected != lib) return;
            foreach (var s in r.Results) Series.Add(s);
            _seriesTotal = r.Total;
            _seriesPage++;
        }
        catch (ApiException) { }
        finally { _seriesLoading = false; }
    }

    public async Task Select(Library lib)
    {
        Selected = lib;
        Items.Clear(); _page = 0; _hasMore = false;
        Series.Clear(); _seriesPage = 0; _seriesTotal = 0; _seriesLoading = false;
        SearchResults = null;
        Notify(nameof(Selected)); Notify(nameof(SearchResults));
        await Fetch();
    }

    /// <summary>Items to show, honoring the "hide finished" setting.</summary>
    public IEnumerable<LibraryItem> VisibleItems() =>
        prefs.HideFinished ? Items.Where(i => (ProgressFor(i.Id) ?? 0) < 1) : Items;

    /// <summary>Re-fetch from page 0, e.g. after the sort order changes.</summary>
    public async Task Reload()
    {
        if (Selected is { } lib) await Select(lib);
    }

    /// <summary>Fetches the next page; call when the listener scrolls near the end.</summary>
    public async Task LoadMore()
    {
        if (!_hasMore || Loading) return;
        _page++;
        await Fetch();
    }

    public bool HasMore => _hasMore;

    private async Task Fetch()
    {
        if (Selected is not { } lib) return;
        Loading = true;
        Notify(nameof(Loading));
        try
        {
            var r = await repository.GetLibraryItems(lib.Id, prefs.LibrarySort, _page);
            if (Selected == lib)
            {
                foreach (var i in r.Results) Items.Add(i);
                _hasMore = Items.Count < r.Total;
            }
        }
        catch (ApiException e) { Error = e.Message; Notify(nameof(Error)); }
        Loading = false;
        Notify(nameof(Loading));
    }
}
