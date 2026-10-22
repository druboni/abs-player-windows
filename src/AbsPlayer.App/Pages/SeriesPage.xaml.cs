using AbsPlayer.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

/// <summary>A series' books in reading order.</summary>
public sealed partial class SeriesPage : Page
{
    public SeriesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var series = (BookSeries)e.Parameter;
        TitleText.Text = series.Name;
        Books.ItemsSource = BookVm.From(series.Books, series.Name);
    }

    private void OnBookClick(object sender, ItemClickEventArgs e) => App.Window.Open(((BookVm)e.ClickedItem).Item);
}
