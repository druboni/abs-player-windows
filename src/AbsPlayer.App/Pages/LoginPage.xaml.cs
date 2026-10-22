using AbsPlayer.Core.Api;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AbsPlayer.App.Pages;

public sealed partial class LoginPage : Page
{
    private bool _busy;

    public LoginPage()
    {
        InitializeComponent();
        Server.Text = Services.User.ServerUrl ?? "";
        Loaded += (_, _) => (Server.Text.Length == 0 ? Server : (Control)Username).Focus(FocusState.Programmatic);
    }

    private void OnInputChanged(object sender, TextChangedEventArgs e) => UpdateButton();

    private void UpdateButton() =>
        SignInButton.IsEnabled = !_busy && Server.Text.Trim().Length > 0 && Username.Text.Trim().Length > 0;

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { e.Handled = true; SignIn(); }
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => SignIn();

    private async void SignIn()
    {
        if (_busy || Server.Text.Trim().Length == 0 || Username.Text.Trim().Length == 0) return;
        _busy = true;
        UpdateButton();
        Busy.IsActive = true;
        SignInText.Opacity = 0;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            await Services.Repository.Login(Server.Text, Username.Text.Trim(), Password.Password);
            App.Window.ShowLibrary();
        }
        catch (ApiException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            Busy.IsActive = false;
            SignInText.Opacity = 1;
            UpdateButton();
        }
    }
}
