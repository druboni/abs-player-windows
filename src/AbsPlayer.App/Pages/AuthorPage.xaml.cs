using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

/// <summary>Every book by one author in the current library.</summary>
public sealed partial class AuthorPage : Page
{
    public AuthorPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        var author = (Author)e.Parameter;
        TitleText.Text = author.Name;
        List<LibraryItem> books = [];
        if (Services.Library.Selected is { } lib)
        {
            try { books = await Services.Repository.GetBooks(lib.Id, author); }
            catch (ApiException) { }
        }
        Books.ItemsSource = BookVm.From(books);
        LoadingRing.IsActive = false;
        EmptyText.Visibility = Ui.Show(books.Count == 0);
    }

    private void OnBookClick(object sender, ItemClickEventArgs e) => App.Window.Open(((BookVm)e.ClickedItem).Item);
}
