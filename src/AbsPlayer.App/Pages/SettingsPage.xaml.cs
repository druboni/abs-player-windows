using AbsPlayer.Core;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Playback;
using AbsPlayer.Core.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

public sealed partial class SettingsPage : Page
{
    private sealed record Option<T>(string Label, T Value)
    {
        public override string ToString() => Label;
    }

    private bool _loading;
    private static Prefs Prefs => Services.Prefs;

    public SettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        ServerText.Text = Services.User.ServerUrl ?? "—";
        UserText.Text = Services.User.Username ?? "—";
        AppVersionText.Text = $"ABS Player for Windows {AppConfig.Version}";

        Fill(SkipBack, Prefs.SkipOptions.Select(s => new Option<int>($"{s} seconds", s)), Prefs.SkipBack);
        Fill(SkipForward, Prefs.SkipOptions.Select(s => new Option<int>($"{s} seconds", s)), Prefs.SkipForward);
        Fill(Rewind, Prefs.RewindOptions.Select(s => new Option<int>(s == 0 ? "Off" : $"{s} seconds", s)), Prefs.RewindOnResume);
        Fill(Speed, AudioPlayer.Speeds.Select(s => new Option<double>(Format.Speed(s), s)), Services.Audio.Speed);
        Fill(Sort, Enum.GetValues<LibrarySort>().Select(s => new Option<LibrarySort>(s.Label(), s)), Prefs.LibrarySort);
        HideFinished.IsOn = Prefs.HideFinished;
        AutoUpdate.IsOn = Services.Updates.Enabled;
        RequesterUrl.Text = Services.Requester.Url ?? "";
        RenderRequester();
        _loading = false;

        if (Services.User.ServerUrl is { } server)
            VersionText.Text = await AudiobookShelfApi.ServerVersion(server, Services.Http) ?? "Unreachable";
    }

    private static void Fill<T>(ComboBox box, IEnumerable<Option<T>> options, T current)
    {
        var list = options.ToList();
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, current));
    }

    private static T? Picked<T>(ComboBox box) => box.SelectedItem is Option<T> o ? o.Value : default;

    private void OnSkipBackChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) Prefs.SkipBack = Picked<int>(SkipBack); }
    private void OnSkipForwardChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) Prefs.SkipForward = Picked<int>(SkipForward); }
    private void OnRewindChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) Prefs.RewindOnResume = Picked<int>(Rewind); }
    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) Services.Audio.SetSpeed(Picked<double>(Speed)); }
    private void OnHideFinishedToggled(object sender, RoutedEventArgs e) { if (!_loading) Prefs.HideFinished = HideFinished.IsOn; }

    // Audiobook Requester

    private void RenderRequester(string? error = null)
    {
        var r = Services.Requester;
        RequesterConnect.Content = r.IsConnected ? "Reconnect…" : "Connect…";
        RequesterDisconnect.Visibility = Ui.Show(r.Url is not null);
        RequesterStatus.Text = error ?? (r.IsConnected
            ? $"Connected as {r.Username}{(r.IsAdmin ? " (admin)" : "")}. Use Request in the top bar."
            : r.Url is not null ? "Signed out. Click Connect to sign in again." : "Not connected.");
        RequesterStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            error is not null ? "SystemFillColorCriticalBrush" : r.IsConnected ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush"];
    }

    /// <summary>Asks for the Audiobookshelf password (the username is the one signed in here), then connects.</summary>
    private async void OnRequesterConnect(object sender, RoutedEventArgs e)
    {
        if (RequesterUrl.Text.Trim().Length == 0)
        {
            RenderRequester("Enter the Requester address first.");
            RequesterUrl.Focus(FocusState.Programmatic);
            return;
        }
        var username = new TextBox { Header = "Audiobookshelf username", Text = Services.Requester.Username ?? Services.User.Username ?? "" };
        var password = new PasswordBox { Header = "Audiobookshelf password" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Sign in to Audiobook Requester",
            Content = new StackPanel { Spacing = 12, Children = { username, password } },
            PrimaryButtonText = "Connect",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => password.Focus(FocusState.Programmatic);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        RequesterConnect.IsEnabled = false;
        RequesterBusy.IsActive = true;
        try
        {
            await Services.Requester.Connect(RequesterUrl.Text, username.Text.Trim(), password.Password);
            RequesterUrl.Text = Services.Requester.Url ?? RequesterUrl.Text;
            RenderRequester();
        }
        catch (Core.Requester.RequesterException ex) { RenderRequester(ex.Message); }
        finally
        {
            RequesterConnect.IsEnabled = true;
            RequesterBusy.IsActive = false;
        }
    }

    private void OnRequesterDisconnect(object sender, RoutedEventArgs e)
    {
        Services.Requester.Disconnect(forgetUrl: true);
        RequesterUrl.Text = "";
        RenderRequester();
    }

    private void OnAutoUpdateToggled(object sender, RoutedEventArgs e) { if (!_loading) Services.Updates.Enabled = AutoUpdate.IsOn; }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        CheckingRing.IsActive = true;
        UpdateStatus.Text = "";
        try
        {
            if (await Services.Updates.Check() is { } update)
            {
                UpdateStatus.Text = $"Version {update.Version} is available.";
                App.Window.ShowUpdate(update);
            }
            else
            {
                UpdateStatus.Text = $"You have the latest version ({AppConfig.Version}).";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            UpdateStatus.Text = "Couldn't reach GitHub. Try again later.";
        }
        CheckingRing.IsActive = false;
        CheckNowButton.IsEnabled = true;
    }

    private async void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Prefs.LibrarySort = Picked<LibrarySort>(Sort);
        await Services.Library.Reload();
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        if (await Confirm($"Sign out of {Services.User.Username ?? "this account"}?", null, "Sign Out"))
            App.Window.SignOut(forgetServer: false);
    }

    private async void OnSwitchServer(object sender, RoutedEventArgs e)
    {
        if (await Confirm("Switch to a different server?", "You'll be signed out and can enter a new server address.", "Sign Out and Switch"))
            App.Window.SignOut(forgetServer: true);
    }

    private async Task<bool> Confirm(string title, string? message, string action) =>
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        }.ShowAsync() == ContentDialogResult.Primary;
}
