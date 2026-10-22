using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AbsPlayer.Core;
using AbsPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

public sealed partial class LibraryPage : Page
{
    private readonly ObservableCollection<object> _tiles = [];
    private bool _seriesMode;
    private bool _hideFinished;
    private int _shownThisFetch;
    private static LibraryModel Model => Services.Library;

    public LibraryPage()
    {
        InitializeComponent();
        Tiles.ItemsSource = _tiles;
        _hideFinished = Services.Prefs.HideFinished;
        Model.PropertyChanged += (_, e) => OnModelChanged(e.PropertyName);
        Model.Items.CollectionChanged += OnItemsChanged;
        Model.Series.CollectionChanged += OnSeriesChanged;
        Services.Downloads.Changed += OnDownloadChanged;
        RefreshDownloaded();
        RefreshHeader();
        RefreshContinue();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        // Coming back from a book or the player: progress may have moved.
        if (e.NavigationMode == NavigationMode.Back) _ = Model.RefreshProgress();
        if (_hideFinished != Services.Prefs.HideFinished)
        {
            _hideFinished = Services.Prefs.HideFinished;
            if (!_seriesMode) RebuildBooks();
        }
    }

    private void OnModelChanged(string? name)
    {
        switch (name)
        {
            case nameof(LibraryModel.Progress):
                foreach (var t in _tiles.OfType<BookVm>()) t.Refresh();
                RefreshContinue();
                break;
            case nameof(LibraryModel.ContinueItems):
                RefreshContinue();
                break;
            case nameof(LibraryModel.Selected) when _seriesMode:
                _ = Model.LoadSeries();
                break;
            case nameof(LibraryModel.Loading) when Model.Loading:
                _shownThisFetch = 0;
                break;
            case nameof(LibraryModel.Loading):
                // With "Hide finished" on, a whole page can be finished books: nothing new appears,
                // so scrolling would never ask for more. Keep going until something shows.
                if (!_seriesMode && Model.HasMore && (_shownThisFetch == 0 || _tiles.Count < 24)) _ = Model.LoadMore();
                break;
        }
        RefreshHeader();
    }

    /// <summary>Downloaded books, playable offline. Built from what's on disk, so it shows even when the server can't be reached.</summary>
    private void RefreshDownloaded()
    {
        var books = Services.Downloads.Books;
        DownloadedList.ItemsSource = BookVm.From(books.Select(b => b.ToLibraryItem()));
        DownloadedSection.Visibility = Ui.Show(books.Count > 0);
    }

    private void OnDownloadChanged(object? sender, string itemId)
    {
        var downloaded = Services.Downloads.Status(itemId).Kind == Core.Downloads.DownloadKind.Downloaded;
        var listed = (DownloadedList.ItemsSource as IEnumerable<BookVm>)?.Any(b => b.Item.Id == itemId) ?? false;
        if (downloaded != listed) RefreshDownloaded();
        foreach (var t in _tiles.OfType<BookVm>().Where(t => t.Item.Id == itemId)) t.RefreshDownload();
        foreach (var t in (ContinueList.ItemsSource as IEnumerable<BookVm>) ?? []) if (t.Item.Id == itemId) t.RefreshDownload();
    }

    /// <summary>Rebuilt only when it changes, so it doesn't flicker or lose its scroll position.</summary>
    private void RefreshContinue()
    {
        ContinueList.ItemsSource = BookVm.From(Model.ContinueItems);
        ContinueSection.Visibility = Ui.Show(Model.ContinueItems.Count > 0);
    }

    private void RefreshHeader()
    {
        ErrorText.Text = Model.Error ?? "";
        ErrorText.Visibility = Ui.Show(Model.Error is not null);
        LibraryName.Text = Model.Selected?.Name ?? "Library";
        LoadingRing.IsActive = Model.Loading;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_seriesMode) return;
        if (e.Action == NotifyCollectionChangedAction.Reset) { _tiles.Clear(); return; }
        foreach (var item in e.NewItems?.OfType<LibraryItem>() ?? [])
            if (!_hideFinished || (Model.ProgressFor(item.Id) ?? 0) < 1)
            {
                _tiles.Add(new BookVm(item, Model.ProgressFor(item.Id)));
                _shownThisFetch++;
            }
    }

    private void OnSeriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_seriesMode) return;
        if (e.Action == NotifyCollectionChangedAction.Reset) { _tiles.Clear(); return; }
        foreach (var s in e.NewItems?.OfType<BookSeries>() ?? []) _tiles.Add(new SeriesVm(s));
    }

    private void RebuildBooks()
    {
        _tiles.Clear();
        foreach (var vm in BookVm.From(Model.VisibleItems())) _tiles.Add(vm);
    }

    private async void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _seriesMode = sender.SelectedItem?.Text == "Series";
        _tiles.Clear();
        if (!_seriesMode) { RebuildBooks(); return; }
        foreach (var s in Model.Series) _tiles.Add(new SeriesVm(s));
        if (Model.Series.Count == 0) await Model.LoadSeries();
    }

    /// <summary>Near the end of what's loaded, fetch the next page.</summary>
    private async void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemIndex < _tiles.Count - 12) return;
        if (_seriesMode) await Model.LoadSeries();
        else if (Model.HasMore && !Model.Loading) await Model.LoadMore();
    }

    private void OnTileClick(object sender, ItemClickEventArgs e)
    {
        switch (e.ClickedItem)
        {
            case BookVm b: App.Window.Open(b.Item); break;
            case SeriesVm s: App.Window.Open(s.Series); break;
        }
    }
}
