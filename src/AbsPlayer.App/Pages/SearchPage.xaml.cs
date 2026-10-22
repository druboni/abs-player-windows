using System.ComponentModel;
using AbsPlayer.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

/// <summary>Results for the search box: authors, series and books in the current library.</summary>
public sealed partial class SearchPage : Page
{
    public SearchPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Services.Library.PropertyChanged += OnLibraryChanged;
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Services.Library.PropertyChanged -= OnLibraryChanged;

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LibraryModel.SearchResults) or nameof(LibraryModel.Searching)) Render();
    }

    private void Render()
    {
        var r = Services.Library.SearchResults;
        Searching.IsActive = r is null && Services.Library.Searching;
        NoResults.Visibility = Ui.Show(r is { IsEmpty: true });
        AuthorsSection.Visibility = Ui.Show(r is { Authors.Count: > 0 });
        SeriesSection.Visibility = Ui.Show(r is { Series.Count: > 0 });
        BooksSection.Visibility = Ui.Show(r is { Books.Count: > 0 });
        Authors.ItemsSource = r?.Authors.Select(a => new AuthorVm(a)).ToList();
        Series.ItemsSource = r?.Series.Select(s => new SeriesVm(s)).ToList();
        Books.ItemsSource = r is null ? null : BookVm.From(r.Books);
        var query = App.Window.SearchText.Trim();
        RequestLink.Visibility = Ui.Show(r is not null && Services.Requester.Url is not null && query.Length >= 2);
        RequestLinkText.Text = r is { IsEmpty: true }
            ? $"Not in your library. Request “{query}”"
            : $"Not what you're looking for? Request “{query}”";
    }

    private void OnRequestLink(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => App.Window.OpenRequests(App.Window.SearchText);

    private void OnAuthorClick(object sender, ItemClickEventArgs e) => App.Window.Open(((AuthorVm)e.ClickedItem).Author);
    private void OnSeriesClick(object sender, ItemClickEventArgs e) => App.Window.Open(((SeriesVm)e.ClickedItem).Series);
    private void OnBookClick(object sender, ItemClickEventArgs e) => App.Window.Open(((BookVm)e.ClickedItem).Item);
}
