using AbsPlayer.App.Pages;
using AbsPlayer.Core.Models;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;

namespace AbsPlayer.App;

public sealed partial class MainWindow : Window
{
    private CancellationTokenSource? _searchDelay;
    private bool _closing;
    private bool _pickingLibrary;

    public MainWindow()
    {
        InitializeComponent();
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(1100 * scale), (int)(780 * scale)));
        AppWindow.Closing += OnClosing;
        VisibilityChanged += OnVisibilityChanged;
        Services.Repository.Api.SessionExpired += (_, _) => Services.Dispatcher.TryEnqueue(() =>
        {
            if (Services.User.IsLoggedIn) SignOut(forgetServer: false);
        });

        if (Services.User.IsLoggedIn) ShowLibrary();
        else ShowLogin();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public Frame Frame => ContentFrame;

    // Signing in and out

    public void ShowLogin()
    {
        TopBar.Visibility = Visibility.Collapsed;
        NowPlaying.Visibility = Visibility.Collapsed;
        ContentFrame.Navigate(typeof(LoginPage));
        ContentFrame.BackStack.Clear();
    }

    public void ShowLibrary()
    {
        TopBar.Visibility = Visibility.Visible;
        NowPlaying.Refresh();
        ContentFrame.Navigate(typeof(LibraryPage));
        ContentFrame.BackStack.Clear();
        _ = LoadLibraries();
    }

    private async Task LoadLibraries()
    {
        await Services.Library.Load();
        _ = Services.Progress.Flush(Services.Repository);
        _pickingLibrary = true;
        LibraryPicker.ItemsSource = Services.Library.Libraries;
        LibraryPicker.SelectedItem = Services.Library.Selected;
        LibraryPicker.Visibility = Ui.Show(Services.Library.Libraries.Count > 1);
        _pickingLibrary = false;
    }

    public async void SignOut(bool forgetServer)
    {
        await Services.Playback.Stop();
        Services.Repository.Logout(forgetServer);
        ShowLogin();
    }

    // Navigation

    public void Open(LibraryItem item) => ContentFrame.Navigate(typeof(BookDetailPage), item);
    public void Open(BookSeries series) => ContentFrame.Navigate(typeof(SeriesPage), series);
    public void Open(Author author) => ContentFrame.Navigate(typeof(AuthorPage), author);

    public void OpenPlayer()
    {
        if (ContentFrame.CurrentSourcePageType != typeof(PlayerPage)) ContentFrame.Navigate(typeof(PlayerPage));
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        // The full player has its own controls, so the bar hides there.
        NowPlaying.HiddenByPage = e.SourcePageType == typeof(PlayerPage) || e.SourcePageType == typeof(LoginPage);
        NowPlaying.Refresh();
        if (e.SourcePageType != typeof(SearchPage) && SearchBox.Text.Length > 0 && e.NavigationMode == NavigationMode.Back)
            SearchBox.Text = "";
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => GoBack();

    private void GoBack()
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(SettingsPage)) ContentFrame.Navigate(typeof(SettingsPage));
    }

    private async void OnLibraryPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_pickingLibrary || LibraryPicker.SelectedItem is not Library lib || lib == Services.Library.Selected) return;
        await Services.Library.Select(lib);
        if (ContentFrame.CurrentSourcePageType != typeof(LibraryPage))
        {
            ContentFrame.Navigate(typeof(LibraryPage));
        }
    }

    // Search: results page while there's text, back to where you were when it's cleared.

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        var text = sender.Text;
        if (text.Trim().Length == 0)
        {
            if (ContentFrame.CurrentSourcePageType == typeof(SearchPage)) GoBack();
            await Services.Library.Search("");
            return;
        }
        if (ContentFrame.CurrentSourcePageType != typeof(SearchPage)) ContentFrame.Navigate(typeof(SearchPage));
        try { await Task.Delay(300, cts.Token); }
        catch (TaskCanceledException) { return; }
        await Services.Library.Search(text);
    }

    // Keyboard shortcuts (the Mac app's Playback menu, with Ctrl for Cmd)

    private void OnTogglePlay(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Services.Audio.Toggle(); e.Handled = true; }
    private void OnSkipBack(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Services.Audio.SkipBack(); e.Handled = true; }
    private void OnSkipForward(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Services.Audio.SkipForward(); e.Handled = true; }
    private void OnPreviousChapter(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Services.Playback.PreviousChapter(); e.Handled = true; }
    private void OnNextChapter(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Services.Playback.NextChapter(); e.Handled = true; }
    private void OnCycleSpeed(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { Services.Audio.CycleSpeed(); e.Handled = true; }
    private void OnBack(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { GoBack(); e.Handled = true; }

    private void OnFocusSearch(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (TopBar.Visibility == Visibility.Visible) SearchBox.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    // Window state

    private void OnVisibilityChanged(object sender, WindowVisibilityChangedEventArgs args)
    {
        // Minimized: sync less often. Back in view: send anything saved offline.
        Services.Playback.IsInForeground = args.Visible;
        if (args.Visible) _ = Services.Progress.Flush(Services.Repository);
        else Services.Playback.SyncNow();
    }

    /// <summary>Closes the server session (saving the final position) before the window goes away.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing || !Services.Playback.IsActive) return;
        args.Cancel = true;
        _closing = true;
        await Task.WhenAny(Services.Playback.Stop(), Task.Delay(TimeSpan.FromSeconds(5)));
        Close();
    }
}
