using System.ComponentModel;
using AbsPlayer.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AbsPlayer.App;

/// <summary>A book as a tile or row shows it.</summary>
public sealed class BookVm(LibraryItem item, double? progress, string? seriesName = null) : INotifyPropertyChanged
{
    private double? _progress = progress;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryItem Item { get; } = item;
    public string Title => Item.Media.Metadata.Title;
    public string Author => Item.Media.Metadata.AuthorName ?? "";
    public Uri? Cover => Services.Repository.CoverUrl(Item.Id);
    public double Progress => (_progress ?? 0) * 100;
    public Visibility ProgressVisibility => _progress is > 0 and < 1 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FinishedVisibility => _progress >= 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Picks up the latest progress from the library model.</summary>
    public void Refresh()
    {
        var latest = Services.Library.ProgressFor(Item.Id);
        if (latest == _progress) return;
        _progress = latest;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
    /// <summary>"Book 3", when shown inside a series.</summary>
    public string SeriesLabel => seriesName is not null && Item.Sequence(seriesName) is { } s ? $"Book {s}" : "";

    public static List<BookVm> From(IEnumerable<LibraryItem> items, string? seriesName = null) =>
        items.Select(i => new BookVm(i, Services.Library.ProgressFor(i.Id), seriesName)).ToList();
}

public sealed class SeriesVm(BookSeries series)
{
    public BookSeries Series { get; } = series;
    public string Name => Series.Name;
    public string CountLabel => Series.Books.Count == 1 ? "1 book" : $"{Series.Books.Count} books";
    public Uri? Cover1 => CoverAt(0);
    public Uri? Cover2 => CoverAt(1);
    public Uri? Cover3 => CoverAt(2);
    private Uri? CoverAt(int i) => i < Series.Books.Count ? Services.Repository.CoverUrl(Series.Books[i].Id) : null;
}

public sealed class AuthorVm(Author author)
{
    public Author Author { get; } = author;
    public string Name => Author.Name;
    public string CountLabel => Author.NumBooks is { } n ? (n == 1 ? "1 book" : $"{n} books") : "";
}

/// <summary>Picks the book or series tile in mixed grids.</summary>
public sealed partial class TileTemplateSelector : DataTemplateSelector
{
    public DataTemplate? BookTemplate { get; set; }
    public DataTemplate? SeriesTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item is SeriesVm ? SeriesTemplate : BookTemplate;
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}

public static class Ui
{
    /// <summary>A cover image decoded at tile size, so long grids stay light on memory.</summary>
    public static BitmapImage? Image(Uri? uri, int decodeWidth = 320) =>
        uri is null ? null : new BitmapImage(uri) { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };

    public static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}
