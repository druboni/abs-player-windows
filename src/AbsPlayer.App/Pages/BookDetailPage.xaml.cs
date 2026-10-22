using System.ComponentModel;
using AbsPlayer.Core;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace AbsPlayer.App.Pages;

/// <summary>One chapter in a book's list.</summary>
public sealed class ChapterRow(int index, Chapter chapter, bool current, bool playing)
{
    public Chapter Chapter { get; } = chapter;
    public string Number => $"{index + 1}";
    public string Title => Chapter.Title;
    public string Length => Format.Duration(Chapter.End - Chapter.Start);
    public string Glyph => playing ? "" : ""; // Speaker while playing, bookmark for "you left off here".
    public Visibility CurrentVisibility => Ui.Show(current);
    public Visibility NumberVisibility => Ui.Show(!current);
    public Brush TitleBrush => current
        ? (Brush)Application.Current.Resources["AccentText"]
        : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
}

public sealed partial class BookDetailPage : Page
{
    private LibraryItem _item = null!;
    private LibraryItem? _full;
    private MediaProgressResponse? _progress;
    private int _shownChapter = -2;
    private bool _shownCurrent;

    public BookDetailPage() => InitializeComponent();

    private Media Media => (_full ?? _item).Media;
    private bool IsCurrent => Services.Playback.ItemId == _item.Id;
    private IReadOnlyList<Chapter> Chapters => Media.Chapters ?? [];

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _item = (LibraryItem)e.Parameter;
        Services.Playback.PropertyChanged += OnPlaybackChanged;
        Cover.Source = Ui.Image(Services.Repository.CoverUrl(_item.Id), 600);
        Render();
        try { _full ??= await Services.Repository.GetItem(_item.Id); }
        catch (ApiException) { }
        try { _progress = await Services.Repository.GetProgress(_item.Id); }
        catch (ApiException) { _progress = null; } // 404 until the book is started.
        Render();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => Services.Playback.PropertyChanged -= OnPlaybackChanged;

    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdatePlayButton();
        UpdateChapters();
    }

    private void Render()
    {
        var m = Media.Metadata;
        TitleText.Text = m.Title;
        AuthorText.Text = m.AuthorName ?? "";
        SeriesText.Text = m.SeriesName ?? "";
        DurationText.Text = Media.Duration is { } d ? Format.Remaining(d) : "";
        DescriptionText.Text = m.Description is { } desc ? Format.StripHtml(desc) : "";
        ChaptersExpander.Visibility = Ui.Show(Chapters.Count > 0);
        ChapterCount.Text = $"{Chapters.Count}";
        UpdatePlayButton();
        _shownChapter = -2;
        UpdateChapters();
    }

    private void UpdatePlayButton()
    {
        var resume = Format.ResumeDescription(_progress, Chapters);
        ResumeText.Text = !IsCurrent ? resume ?? "" : "";
        PlayText.Text = IsCurrent ? "Now Playing" : resume is null ? "Play" : "Resume";
        PlayIcon.Glyph = IsCurrent ? "" : "";
        ErrorText.Text = IsCurrent ? Services.Playback.Error ?? "" : "";
        ErrorText.Visibility = Ui.Show(ErrorText.Text.Length > 0);
    }

    /// <summary>Where the listener is: live position if this book is playing, otherwise their saved spot.</summary>
    private double? CurrentTime => IsCurrent ? Services.Audio.Position : _progress?.CurrentTime;

    private void UpdateChapters()
    {
        var current = -1;
        if (CurrentTime is { } t)
            for (var i = Chapters.Count - 1; i >= 0; i--)
                if (t >= Chapters[i].Start) { current = i; break; }
        if (current == _shownChapter && IsCurrent == _shownCurrent) return;
        _shownChapter = current;
        _shownCurrent = IsCurrent;
        ChapterList.ItemsSource = Chapters.Select((c, i) => new ChapterRow(i, c, i == current, IsCurrent)).ToList();
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        App.Window.OpenPlayer();
        if (!IsCurrent) await Services.Playback.Play(_item.Id);
    }

    private async void OnChapterClick(object sender, ItemClickEventArgs e)
    {
        var row = (ChapterRow)e.ClickedItem;
        App.Window.OpenPlayer();
        await Services.Playback.Play(_item.Id, row.Chapter.Start);
    }
}
