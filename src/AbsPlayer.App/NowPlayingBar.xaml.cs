using AbsPlayer.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AbsPlayer.App;

/// <summary>The strip along the bottom: what's playing, play/pause and skips. Click it for the full player.</summary>
public sealed partial class NowPlayingBar : UserControl
{
    private Uri? _cover;

    public NowPlayingBar()
    {
        InitializeComponent();
        Services.Playback.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>Set while a page that has its own controls (the full player) is showing.</summary>
    public bool HiddenByPage { get; set; }

    public void Refresh()
    {
        var p = Services.Playback;
        var show = !HiddenByPage && Services.User.IsLoggedIn && (p.IsActive || p.IsLoading);
        Visibility = Ui.Show(show);
        if (!show) return;

        LoadingRing.IsActive = p.IsLoading;
        TitleText.Text = p.IsLoading && p.Title.Length == 0 ? "Loading…" : p.Title;
        DetailText.Text = p.Error ?? p.Chapter?.Title ?? p.Author ?? "";
        PlayIcon.Glyph = Services.Audio.IsPlaying ? "" : "";
        Progress.Value = Services.Audio.Duration > 0 ? Services.Audio.Position / Services.Audio.Duration : 0;
        if (p.CoverUrl != _cover)
        {
            _cover = p.CoverUrl;
            Cover.Source = Ui.Image(_cover, 96);
        }
    }

    private void OnOpenPlayer(object sender, RoutedEventArgs e) => App.Window.OpenPlayer();
    private void OnToggle(object sender, RoutedEventArgs e) { Services.Audio.Toggle(); Refresh(); }
    private void OnSkipBack(object sender, RoutedEventArgs e) { Services.Audio.SkipBack(); Refresh(); }
    private void OnSkipForward(object sender, RoutedEventArgs e) { Services.Audio.SkipForward(); Refresh(); }
}
