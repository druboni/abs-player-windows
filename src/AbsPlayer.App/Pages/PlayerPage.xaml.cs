using System.ComponentModel;
using AbsPlayer.Core;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace AbsPlayer.App.Pages;

public sealed class PlayerChapterRow(Chapter chapter, bool current)
{
    public Chapter Chapter { get; } = chapter;
    public string Title => Chapter.Title;
    public string Start => Format.Duration(Chapter.Start);
    public Visibility CurrentVisibility => Ui.Show(current);
    public Brush TitleBrush => current
        ? (Brush)Application.Current.Resources["AccentText"]
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
}

public sealed partial class PlayerPage : Page
{
    private static PlaybackController Playback => Services.Playback;
    private static AudioPlayer Audio => Services.Audio;

    private bool _settingScrubber;
    private bool _scrubbing;
    private Uri? _cover;
    private int _shownChapter = -2;
    private string? _shownItem;
    private IReadOnlyList<Bookmark>? _shownBookmarks;
    private CancellationTokenSource? _bookmarkReset;
    private bool? _narrow;

    /// <summary>Below ~880 px the chapters panel would squeeze the player, so it folds away (the Chapters button brings it back).</summary>
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 880;
        if (narrow == _narrow) return;
        _narrow = narrow;
        PanelToggle.IsChecked = !narrow;
        Render();
    }

    public PlayerPage()
    {
        InitializeComponent();
        Scrubber.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), handledEventsToo: true);
        Scrubber.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
        Scrubber.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndScrub()), handledEventsToo: true);
        foreach (var s in AudioPlayer.Speeds)
        {
            var item = new RadioMenuFlyoutItem { Text = Format.Speed(s), GroupName = "speed", Tag = s };
            item.Click += (_, _) => { Audio.SetSpeed(s); Render(); };
            SpeedMenu.Items.Add(item);
        }
    }

    public static string Time(double seconds) => Format.Duration(seconds);

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Playback.PropertyChanged += OnPlaybackChanged;
        KeyDown += OnPageKeyDown;
        _shownChapter = -2;
        _shownItem = null;
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        Playback.PropertyChanged -= OnPlaybackChanged;
        KeyDown -= OnPageKeyDown;
    }

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render()
    {
        var showPlayer = Playback.IsActive && !Playback.IsLoading && Playback.Error is null;
        LoadingRing.IsActive = Playback.IsLoading;
        StatusText.Text = Playback.Error ?? (!Playback.IsActive && !Playback.IsLoading ? "Nothing playing" : "");
        StatusText.Foreground = Playback.Error is not null
            ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        StatusText.Visibility = Ui.Show(StatusText.Text.Length > 0);
        Player.Visibility = Ui.Show(showPlayer);
        Panel.Visibility = Ui.Show(showPlayer && PanelToggle.IsChecked == true);
        PanelColumn.Width = Panel.Visibility == Visibility.Visible ? new GridLength(380) : new GridLength(0);
        if (!showPlayer) return;

        if (Playback.CoverUrl != _cover)
        {
            _cover = Playback.CoverUrl;
            Cover.Source = Ui.Image(_cover, 640);
        }
        TitleText.Text = Playback.Title;
        AuthorText.Text = Playback.Author ?? "";
        ChapterText.Text = Playback.ChapterLabel ?? "";
        ChapterButton.Visibility = Ui.Show(Playback.ChapterLabel is not null);
        NextChapterButton.IsEnabled = Playback.Chapters.Count > 0;
        PlayIcon.Glyph = Audio.IsPlaying ? "" : "";
        SkipBackText.Text = $"{Services.Prefs.SkipBack}";
        SkipForwardText.Text = $"{Services.Prefs.SkipForward}";
        SpeedText.Text = Format.Speed(Audio.Speed);
        foreach (var item in SpeedMenu.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = (double)item.Tag == Audio.Speed;

        var sleep = Playback.SleepLabel;
        SleepText.Text = sleep ?? "Sleep";
        SleepText.Foreground = sleep is null
            ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["AccentText"];

        RenderScrubber();
        RenderPanel();
    }

    private void RenderScrubber()
    {
        var (start, end) = Playback.ChapterRange;
        var span = end - start;
        var current = Math.Clamp(Audio.Position - start, 0, span);
        if (!_scrubbing)
        {
            _settingScrubber = true;
            Scrubber.Maximum = span;
            Scrubber.Value = current;
            _settingScrubber = false;
        }
        ShowTimes(_scrubbing ? Scrubber.Value : current, span);

        var parts = new List<string>();
        if (Playback.ChapterTimeLeft is { } c) parts.Add($"{Format.Remaining(c)} left in chapter");
        parts.Add($"{Format.Remaining(Playback.BookTimeLeft)} left in book");
        if (Audio.Speed != 1) parts.Add($"at {Format.Speed(Audio.Speed)}");
        TimeLeftText.Text = string.Join(" · ", parts);
    }

    private void ShowTimes(double shown, double span)
    {
        ElapsedText.Text = Format.Duration(shown);
        RemainingText.Text = "-" + Format.Duration(span - shown);
    }

    private void OnScrubberChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settingScrubber) return;
        if (_scrubbing) { ShowTimes(e.NewValue, Scrubber.Maximum); return; }
        // Keyboard on the slider: seek straight away.
        Audio.Seek(Playback.ChapterRange.Start + e.NewValue);
    }

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        Audio.Seek(Playback.ChapterRange.Start + Scrubber.Value);
        Render();
    }

    private void RenderPanel()
    {
        var chapter = Playback.ChapterIndex;
        if (chapter != _shownChapter || Playback.ItemId != _shownItem)
        {
            var firstShow = Playback.ItemId != _shownItem;
            _shownChapter = chapter;
            _shownItem = Playback.ItemId;
            var rows = Playback.Chapters.Select((c, i) => new PlayerChapterRow(c, i == chapter)).ToList();
            ChapterList.ItemsSource = rows;
            if (chapter >= 0 && chapter < rows.Count) ChapterList.ScrollIntoView(rows[chapter], firstShow ? ScrollIntoViewAlignment.Leading : ScrollIntoViewAlignment.Default);
            if (firstShow && Playback.Chapters.Count == 0) BookmarksTab.IsSelected = true;
        }
        if (!ReferenceEquals(Playback.Bookmarks, _shownBookmarks))
        {
            _shownBookmarks = Playback.Bookmarks;
            BookmarkList.ItemsSource = Playback.Bookmarks.ToList();
        }
        var bookmarks = BookmarksTab.IsSelected;
        ChapterList.Visibility = Ui.Show(!bookmarks);
        BookmarkList.Visibility = Ui.Show(bookmarks);
        PanelEmpty.Text = bookmarks
            ? Playback.Bookmarks.Count == 0 ? "No bookmarks yet. Click Add Bookmark to save your place." : ""
            : Playback.Chapters.Count == 0 ? "This book has no chapters" : "";
        PanelEmpty.Visibility = Ui.Show(PanelEmpty.Text.Length > 0);
    }

    // Transport

    private void OnToggle(object sender, RoutedEventArgs e) { Audio.Toggle(); Render(); }
    private void OnSkipBack(object sender, RoutedEventArgs e) { Audio.SkipBack(); Render(); }
    private void OnSkipForward(object sender, RoutedEventArgs e) { Audio.SkipForward(); Render(); }
    private void OnPreviousChapter(object sender, RoutedEventArgs e) => Playback.PreviousChapter();
    private void OnNextChapter(object sender, RoutedEventArgs e) => Playback.NextChapter();

    /// <summary>Space plays/pauses and the arrow keys skip, unless a text box or the slider has focus.</summary>
    private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or Slider or PasswordBox) return;
        if (e.KeyStatus.IsMenuKeyDown) return;
        switch (e.Key)
        {
            case VirtualKey.Space: Audio.Toggle(); break;
            case VirtualKey.Left: Audio.SkipBack(); break;
            case VirtualKey.Right: Audio.SkipForward(); break;
            default: return;
        }
        e.Handled = true;
        Render();
    }

    // Sleep timer

    private void OnSleepMenuOpening(object? sender, object e)
    {
        SleepMenu.Items.Clear();
        if (Playback.SleepTimer.Kind != SleepTimerKind.Off)
        {
            Add("Turn Off", () => Playback.SetSleepTimer(SleepTimer.Off));
            if (Playback.SleepTimer.Kind == SleepTimerKind.Minutes) Add("Add 5 Minutes", () => Playback.ExtendSleepTimer(5));
            SleepMenu.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var m in SleepTimer.Presets) Add($"{m} minutes", () => Playback.SetSleepTimer(SleepTimer.ForMinutes(m)));
        if (Playback.Chapters.Count > 0) Add("End of chapter", () => Playback.SetSleepTimer(SleepTimer.EndOfChapter));

        void Add(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            SleepMenu.Items.Add(item);
        }
    }

    // Chapters and bookmarks

    private void OnShowChapters(object sender, RoutedEventArgs e)
    {
        PanelToggle.IsChecked = true;
        ChaptersTab.IsSelected = true;
        Render();
    }

    private void OnPanelToggle(object sender, RoutedEventArgs e) => Render();

    private void OnPanelTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => RenderPanel();

    private void OnChapterClick(object sender, ItemClickEventArgs e) => Playback.SeekTo(((PlayerChapterRow)e.ClickedItem).Chapter);

    private void OnBookmarkClick(object sender, ItemClickEventArgs e)
    {
        Audio.Seek(((Bookmark)e.ClickedItem).Time);
        Render();
    }

    private async void OnAddBookmark(object sender, RoutedEventArgs e)
    {
        await Playback.AddBookmark();
        if (Playback.BookmarkError is { } error)
        {
            Playback.BookmarkError = null;
            await Alert("Bookmark", error);
            return;
        }
        BookmarkIcon.Glyph = "";
        BookmarkText.Text = "Bookmarked";
        _bookmarkReset?.Cancel();
        var cts = _bookmarkReset = new CancellationTokenSource();
        try { await Task.Delay(2000, cts.Token); }
        catch (TaskCanceledException) { return; }
        BookmarkIcon.Glyph = "";
        BookmarkText.Text = "Add Bookmark";
    }

    private async void OnRenameBookmark(object sender, RoutedEventArgs e)
    {
        var bookmark = (Bookmark)((FrameworkElement)sender).Tag;
        var box = new TextBox { Text = bookmark.Title, SelectionStart = 0, SelectionLength = bookmark.Title.Length };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Rename Bookmark",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Playback.RenameBookmark(bookmark, box.Text);
        _shownBookmarks = null;
        await ShowBookmarkError();
        Render();
    }

    private async void OnDeleteBookmark(object sender, RoutedEventArgs e)
    {
        await Playback.DeleteBookmark((Bookmark)((FrameworkElement)sender).Tag);
        await ShowBookmarkError();
    }

    private async Task ShowBookmarkError()
    {
        if (Playback.BookmarkError is not { } error) return;
        Playback.BookmarkError = null;
        await Alert("Bookmark", error);
    }

    private Task Alert(string title, string message) => new ContentDialog
    {
        XamlRoot = XamlRoot,
        Title = title,
        Content = message,
        CloseButtonText = "OK",
    }.ShowAsync().AsTask();
}
