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
    private static LibraryModel Model => Services.Library;

    public LibraryPage()
    {
        InitializeComponent();
        Tiles.ItemsSource = _tiles;
        _hideFinished = Services.Prefs.HideFinished;
        Model.PropertyChanged += (_, e) => OnModelChanged(e.PropertyName);
        Model.Items.CollectionChanged += OnItemsChanged;
        Model.Series.CollectionChanged += OnSeriesChanged;
        RefreshHeader();
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
                break;
            case nameof(LibraryModel.Selected) when _seriesMode:
                _ = Model.LoadSeries();
                break;
        }
        RefreshHeader();
    }

    private void RefreshHeader()
    {
        ErrorText.Text = Model.Error ?? "";
        ErrorText.Visibility = Ui.Show(Model.Error is not null);
        LibraryName.Text = Model.Selected?.Name ?? "Library";
        LoadingRing.IsActive = Model.Loading;
        ContinueList.ItemsSource = BookVm.From(Model.ContinueItems);
        ContinueSection.Visibility = Ui.Show(Model.ContinueItems.Count > 0);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_seriesMode) return;
        if (e.Action == NotifyCollectionChangedAction.Reset) { _tiles.Clear(); return; }
        foreach (var item in e.NewItems?.OfType<LibraryItem>() ?? [])
            if (!_hideFinished || (Model.ProgressFor(item.Id) ?? 0) < 1)
                _tiles.Add(new BookVm(item, Model.ProgressFor(item.Id)));
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
