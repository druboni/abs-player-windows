using AbsPlayer.App.Pages;
using AbsPlayer.Core;
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
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Closing += OnClosing;
        VisibilityChanged += OnVisibilityChanged;
        Services.Repository.Api.SessionExpired += (_, _) => Services.Dispatcher.TryEnqueue(() =>
        {
            if (Services.User.IsLoggedIn) SignOut(forgetServer: false);
        });

        Services.Requester.Changed += (_, _) => Services.Dispatcher.TryEnqueue(UpdateRequestButton);
        Services.Requester.SignedOut += (_, _) => Services.Dispatcher.TryEnqueue(UpdateRequestButton);
        UpdateRequestButton();

        if (Services.User.IsLoggedIn) ShowLibrary();
        else ShowLogin();
        _ = CheckForUpdatesSoon();
    }

    // Updates

    private AvailableUpdate? _update;

    /// <summary>The once-a-day check, a few seconds after start so it doesn't compete with loading the library.</summary>
    private async Task CheckForUpdatesSoon()
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (await Services.Updates.CheckIfDue() is { } update) ShowUpdate(update);
    }

    public void ShowUpdate(AvailableUpdate update)
    {
        _update = update;
        UpdateBar.Severity = InfoBarSeverity.Informational;
        UpdateBar.Title = "Update available";
        UpdateBar.Message = $"ABS Player {update.Version} is available. You have {AppConfig.Version}.";
        ReleaseNotesLink.NavigateUri = update.ReleasePage;
        UpdateButton.IsEnabled = true;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateBar.IsOpen = true;
    }

    /// <summary>Downloads the installer, saves the listening position, then hands over to the installer, which reopens the app.</summary>
    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_update is not { } update) return;
        UpdateButton.IsEnabled = false;
        UpdateBar.IsClosable = false;
        UpdateBar.Message = $"Downloading ABS Player {update.Version}…";
        UpdateProgress.Value = 0;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var installer = await Services.Updates.Download(update, new Progress<double>(p => UpdateProgress.Value = p));
            UpdateBar.Message = "Installing… ABS Player will reopen when it's done.";
            await Task.WhenAny(Services.Playback.Stop(), Task.Delay(TimeSpan.FromSeconds(5)));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installer, "/SILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
            });
            _closing = true;
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            UpdateBar.Severity = InfoBarSeverity.Error;
            UpdateBar.Title = "Update failed";
            UpdateBar.Message = ex.Message;
            UpdateButton.IsEnabled = true;
            UpdateBar.IsClosable = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
        }
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

    private bool _signingOut;

    public async void SignOut(bool forgetServer)
    {
        // An expired sign-in can be reported by several requests at once.
        if (_signingOut) return;
        _signingOut = true;
        try
        {
            await Services.Playback.Stop();
            Services.Repository.Logout(forgetServer);
            ShowLogin();
        }
        finally { _signingOut = false; }
    }

    /// <summary>Shows an error that didn't stop the app.</summary>
    public void ShowError(string message)
    {
        ErrorBar.Message = $@"{message} Details are in %LOCALAPPDATA%\ABSPlayer\crash.log.";
        ErrorBar.IsOpen = true;
    }

    /// <summary>Squeezes the top bar on narrow windows so the settings button never falls off the edge.</summary>
    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var w = e.NewSize.Width;
        SearchBox.Width = Math.Clamp(w - 620, 160, 320);
        AppTitle.Visibility = Ui.Show(w >= 820);
        RequestLabel.Visibility = Ui.Show(w >= 720);
        LibraryPicker.MinWidth = w >= 820 ? 160 : 110;
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

    /// <summary>Request shows once a Requester address is set up (even signed out, so it can explain how to sign back in).</summary>
    private void UpdateRequestButton() => RequestButton.Visibility = Ui.Show(Services.Requester.Url is not null);

    /// <summary>Opens the Request page, searching Audible for <paramref name="query"/> when given.</summary>
    public void OpenRequests(string? query = null) => ContentFrame.Navigate(typeof(RequestsPage), query);

    /// <summary>What's in the library search box.</summary>
    public string SearchText => SearchBox.Text;

    private void OnRequestsClick(object sender, RoutedEventArgs e)
    {
        if (ContentFrame.CurrentSourcePageType != typeof(RequestsPage)) OpenRequests();
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
