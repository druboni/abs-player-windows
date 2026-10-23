using System.ComponentModel;
using AbsPlayer.Core;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Requester;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

/// <summary>An Audible search result, with a Request button or where it stands.</summary>
public sealed class ResultRow(RequesterBook book) : INotifyPropertyChanged
{
    private string? _requested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public RequesterBook Book { get; } = book;
    public BitmapImage? Cover => Ui.Image(Services.Requester.CoverUrl(Book.Cover), 144);
    public string Title => Book.Subtitle is { Length: > 0 } s ? $"{Book.Title}: {s}" : Book.Title;
    public string SeriesLine => Book.Series is { } s ? Book.SeriesNumber is { } n ? $"{s}, Book {n}" : s : "";
    public Visibility SeriesVisibility => Ui.Show(Book.Series is not null);
    public string People
    {
        get
        {
            var by = Book.Authors.Count > 0 ? $"By {string.Join(", ", Book.Authors)}" : "";
            var read = Book.Narrators.Count > 0 ? $"Narrated by {string.Join(", ", Book.Narrators)}" : "";
            return string.Join(" · ", new[] { by, read }.Where(x => x.Length > 0));
        }
    }
    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Book.RuntimeMin is { } m) parts.Add(Format.Remaining(m * 60));
            if (Book.ReleaseDate is { Length: >= 4 } d) parts.Add(d[..4]);
            if (Book.Rating is { } r) parts.Add($"★ {r.Overall:0.0} ({r.Count:N0})");
            return string.Join(" · ", parts);
        }
    }

    public bool CanRequest => _requested is null && Book.CanRequest;
    public Visibility RequestVisibility => Ui.Show(CanRequest);
    public Visibility StatusVisibility => Ui.Show(!CanRequest);
    public string StatusText => _requested ?? Book.Status switch
    {
        "available" => "In your library",
        "requested" => Book.RequestStatus == "pending" ? "Requested, waiting for approval" : "Requested",
        "wanted" => "Being looked for",
        _ => "",
    };

    public void MarkRequested(string status)
    {
        _requested = status == "pending" ? "Requested, waiting for approval" : "Requested";
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}

/// <summary>A request in My Requests or Approvals.</summary>
public sealed class RequestRow(BookRequest request, bool approvals)
{
    public BookRequest Request { get; } = request;
    public BitmapImage? Cover => Ui.Image(Services.Requester.CoverUrl(Request.Cover), 128);
    public string Title => Request.Title;
    public string Byline
    {
        get
        {
            var by = Request.Authors.Count > 0 ? string.Join(", ", Request.Authors) : "";
            return approvals && Request.Username is { } u ? $"{by} · requested by {u}".TrimStart(' ', '·') : by;
        }
    }
    public string StatusText => Request.Status switch
    {
        "pending" => "Waiting for approval",
        "approved" => "Approved, starting soon",
        "processing" => Request.Stage?.Label ?? "In progress",
        "available" => "In your library",
        "declined" => "Declined",
        "failed" => "Couldn't be found",
        "removed" => "Removed from the library",
        _ => Request.Status,
    };
    public Brush StatusBrush => (Brush)Application.Current.Resources[Request.Status switch
    {
        "declined" or "failed" => "SystemFillColorCriticalBrush",
        "available" => "SystemFillColorSuccessBrush",
        _ => "AccentText",
    }];
    public double Percent => Request.Stage?.Percent ?? 0;
    public Visibility ProgressVisibility => Ui.Show(Request.Stage?.Key == "downloading");
    public string Message => Request.Message ?? Request.Note ?? "";
    public Visibility MessageVisibility => Ui.Show(Message.Length > 0);
    public Visibility ListenVisibility => Ui.Show(!approvals && Request.Status == "available" && Request.AbsItemId is not null);
    public Visibility CancelVisibility => Ui.Show(!approvals && Request.IsPending);
    public Visibility AdminVisibility => Ui.Show(approvals && Request.IsPending);
}

public sealed partial class RequestsPage : Page
{
    private static RequesterClient Requester => Services.Requester;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(15) };
    private string _lastQuery = "";

    public RequestsPage()
    {
        InitializeComponent();
        // My Requests shows download progress, so keep it fresh while it's on screen.
        _refresh.Tick += async (_, _) => { if (!SearchTab.IsSelected) await LoadList(quiet: true); };
    }

    /// <summary>Opened with a query (from library search) to search Audible for it straight away.</summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _refresh.Start();
        Requester.SignedOut += OnSignedOut;
        await Render();
        if (e.Parameter is string q && q.Trim().Length > 0)
        {
            SearchTab.IsSelected = true;
            QueryBox.Text = q;
            await Search(q);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _refresh.Stop();
        Requester.SignedOut -= OnSignedOut;
    }

    private void OnSignedOut(object? sender, EventArgs e) => Services.Dispatcher.TryEnqueue(() => _ = Render());

    private async Task Render()
    {
        var connected = Requester.IsConnected;
        NotConnected.Visibility = Ui.Show(!connected);
        NotConnectedText.Text = Requester.Url is null
            ? "Ask for books that aren't in your library yet. Add your Audiobook Requester address in Settings."
            : "Your Audiobook Requester sign-in has expired. Sign in again in Settings.";
        Tabs.Visibility = Ui.Show(connected);
        ApprovalsTab.Visibility = Ui.Show(connected && Requester.IsAdmin);
        SearchPanel.Visibility = Ui.Show(connected && SearchTab.IsSelected);
        ListPanel.Visibility = Ui.Show(connected && !SearchTab.IsSelected);
        if (!connected) { SubtitleText.Text = ""; return; }

        try
        {
            var me = await Requester.Me();
            SubtitleText.Text = string.Join(" · ", new[] { $"Signed in to Audiobook Requester as {me.User.Username}", me.Quota?.Label }.Where(x => x is not null));
            ApprovalsTab.Visibility = Ui.Show(me.User.IsAdmin);
        }
        catch (RequesterException ex) { SubtitleText.Text = ex.Message; }
        if (!SearchTab.IsSelected) await LoadList();
    }

    private async void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (!Requester.IsConnected) return;
        SearchPanel.Visibility = Ui.Show(SearchTab.IsSelected);
        ListPanel.Visibility = Ui.Show(!SearchTab.IsSelected);
        if (!SearchTab.IsSelected) await LoadList();
    }

    // Search

    private async void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => await Search(args.QueryText);

    private async Task Search(string query)
    {
        _lastQuery = query.Trim();
        if (_lastQuery.Length < 2) { SearchEmpty.Text = "Type at least two letters."; Results.ItemsSource = null; return; }
        var mine = _lastQuery;
        Searching.IsActive = true;
        SearchEmpty.Text = "";
        IsbnNote.Visibility = Visibility.Collapsed;
        Results.ItemsSource = null;
        try
        {
            // Requester searches an ISBN as the book it belongs to; ask which book that is, to say so.
            var isbn = IsbnLookup.Parse(mine);
            var lookup = isbn is null ? Task.FromResult<IsbnInfo?>(null) : LookupQuietly(isbn);
            var books = await Requester.Search(mine);
            var info = await lookup;
            if (mine != _lastQuery) return; // A newer search started.
            Results.ItemsSource = books.Select(b => new ResultRow(b)).ToList();
            if (info is not null)
            {
                IsbnNote.Text = $"ISBN {info.Isbn} is {info.Description}. Results on Audible:";
                IsbnNote.Visibility = Visibility.Visible;
                Results.Margin = new Thickness(0, 28, 0, 0);
            }
            else Results.Margin = new Thickness(0);
            if (books.Count == 0)
                SearchEmpty.Text = isbn is not null && info is null
                    ? $"No book found for ISBN {isbn}. Check the number, or search by title. The ISBN printed on a paper book works best; audiobook editions' own ISBNs often aren't listed."
                    : $"Nothing found on Audible for “{mine}”.";
        }
        catch (RequesterException ex) { SearchEmpty.Text = ex.Message; }
        finally { Searching.IsActive = false; }
    }

    /// <summary>Older Requester servers don't have /isbn: then there's just no note.</summary>
    private static async Task<IsbnInfo?> LookupQuietly(string isbn)
    {
        try { return await Requester.LookupIsbn(isbn); }
        catch (RequesterException) { return null; }
    }

    private async void OnRequestClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var row = (ResultRow)button.Tag;
        var note = new TextBox { PlaceholderText = "Optional note for whoever approves it", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 1000, Height = 80 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Request “{row.Book.Title}”?",
            Content = note,
            PrimaryButtonText = "Request",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        button.IsEnabled = false;
        try
        {
            var request = await Requester.Request(row.Book, note.Text.Trim().Length > 0 ? note.Text.Trim() : null);
            row.MarkRequested(request.Status);
        }
        catch (RequesterException ex)
        {
            button.IsEnabled = true;
            await Alert("Couldn't request it", ex.Message);
        }
    }

    // My requests and approvals

    private async Task LoadList(bool quiet = false)
    {
        var approvals = ApprovalsTab.IsSelected;
        if (!quiet) { ListLoading.IsActive = true; ListEmpty.Text = ""; }
        try
        {
            var list = approvals ? await Requester.Requests("pending", everyone: true) : await Requester.Requests();
            if (approvals != ApprovalsTab.IsSelected) return;
            RequestList.ItemsSource = list.Select(r => new RequestRow(r, approvals)).ToList();
            ListEmpty.Text = list.Count > 0 ? "" : approvals ? "Nothing is waiting for approval." : "You haven't requested any books yet.";
        }
        catch (RequesterException ex) { if (!quiet) ListEmpty.Text = ex.Message; }
        finally { ListLoading.IsActive = false; }
    }

    private async void OnListenClick(object sender, RoutedEventArgs e)
    {
        var row = (RequestRow)((FrameworkElement)sender).Tag;
        try { App.Window.Open(await Services.Repository.GetItem(row.Request.AbsItemId!)); }
        catch (ApiException ex) { await Alert("Couldn't open the book", ex.Message); }
    }

    private async void OnCancelClick(object sender, RoutedEventArgs e)
    {
        var row = (RequestRow)((FrameworkElement)sender).Tag;
        if (!await Confirm($"Cancel your request for “{row.Title}”?", "Cancel Request")) return;
        await Run(() => Requester.Cancel(row.Request.Id));
    }

    private async void OnApproveClick(object sender, RoutedEventArgs e)
    {
        var row = (RequestRow)((FrameworkElement)sender).Tag;
        await Run(() => Requester.Approve(row.Request.Id));
    }

    private async void OnDeclineClick(object sender, RoutedEventArgs e)
    {
        var row = (RequestRow)((FrameworkElement)sender).Tag;
        var reason = new TextBox { PlaceholderText = "Reason (optional, shown to them)", MaxLength = 500 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Decline “{row.Title}”?",
            Content = reason,
            PrimaryButtonText = "Decline",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Run(() => Requester.Decline(row.Request.Id, reason.Text.Trim().Length > 0 ? reason.Text.Trim() : null));
    }

    /// <summary>Runs an action on a request, then reloads the list.</summary>
    private async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (RequesterException ex) { await Alert("Audiobook Requester", ex.Message); }
        await LoadList();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => App.Window.Frame.Navigate(typeof(SettingsPage));

    private Task Alert(string title, string message) =>
        new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync().AsTask();

    private async Task<bool> Confirm(string title, string action) =>
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            PrimaryButtonText = action,
            CloseButtonText = "Keep It",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync() == ContentDialogResult.Primary;
}
