namespace AbsPlayer.Core.Storage;

public enum LibrarySort { Title, Author, RecentlyAdded, Duration, Published }

public enum SeriesSort { Name, RecentlyAdded, BookCount, Duration }

public static class LibrarySortExtensions
{
    public static string Label(this LibrarySort s) => s switch
    {
        LibrarySort.Title => "Title",
        LibrarySort.Author => "Author",
        LibrarySort.RecentlyAdded => "Recently Added",
        LibrarySort.Published => "Published",
        _ => "Length",
    };

    public static string ApiField(this LibrarySort s) => s switch
    {
        LibrarySort.Title => "media.metadata.title",
        LibrarySort.Author => "media.metadata.authorNameLF", // "Last, First", so authors sort by surname.
        LibrarySort.RecentlyAdded => "addedAt",
        LibrarySort.Published => "media.metadata.publishedYear",
        _ => "media.duration",
    };

    /// <summary>The direction a sort starts in: newest first for dates, A to Z and shortest first otherwise.</summary>
    public static bool DefaultDescending(this LibrarySort s) => s is LibrarySort.RecentlyAdded or LibrarySort.Published;

    /// <summary>How each direction reads in the menu, ascending first.</summary>
    public static (string Ascending, string Descending) DirectionLabels(this LibrarySort s) => s switch
    {
        LibrarySort.Title or LibrarySort.Author => ("A to Z", "Z to A"),
        LibrarySort.Duration => ("Shortest first", "Longest first"),
        _ => ("Oldest first", "Newest first"),
    };

    public static string Label(this SeriesSort s) => s switch
    {
        SeriesSort.Name => "Name",
        SeriesSort.RecentlyAdded => "Recently Added",
        SeriesSort.BookCount => "Number of Books",
        _ => "Length",
    };

    public static string ApiField(this SeriesSort s) => s switch
    {
        SeriesSort.Name => "name",
        SeriesSort.RecentlyAdded => "lastBookAdded", // A series with a new book counts as new.
        SeriesSort.BookCount => "numBooks",
        _ => "totalDuration",
    };

    public static bool DefaultDescending(this SeriesSort s) => s is not SeriesSort.Name;

    public static (string Ascending, string Descending) DirectionLabels(this SeriesSort s) => s switch
    {
        SeriesSort.Name => ("A to Z", "Z to A"),
        SeriesSort.RecentlyAdded => ("Oldest first", "Newest first"),
        SeriesSort.BookCount => ("Fewest first", "Most first"),
        _ => ("Shortest first", "Longest first"),
    };
}

/// <summary>User settings, stored in the settings file.</summary>
public sealed class Prefs(SettingsStore settings)
{
    private static class Key
    {
        public const string SkipBack = "skip_back";
        public const string SkipForward = "skip_forward";
        public const string RewindOnResume = "rewind_on_resume";
        public const string LibrarySort = "library_sort";
        public const string LibrarySortDescending = "library_sort_desc";
        public const string SeriesSort = "series_sort";
        public const string SeriesSortDescending = "series_sort_desc";
        public const string HideFinished = "hide_finished";
        public const string RemoveDownloadWhenFinished = "remove_download_when_finished";
        public const string DownloadOnWiFiOnly = "download_wifi_only";
    }

    public static readonly int[] SkipOptions = [10, 15, 30, 45, 60];
    public static readonly int[] RewindOptions = [0, 5, 10, 15, 30];

    public const int DefaultSkip = 30;
    public const int DefaultRewind = 0;

    public SettingsStore Settings => settings;

    public int SkipBack
    {
        get => settings.Get<int?>(Key.SkipBack) ?? DefaultSkip;
        set => settings.Set(Key.SkipBack, value);
    }

    public int SkipForward
    {
        get => settings.Get<int?>(Key.SkipForward) ?? DefaultSkip;
        set => settings.Set(Key.SkipForward, value);
    }

    public int RewindOnResume
    {
        get => settings.Get<int?>(Key.RewindOnResume) ?? DefaultRewind;
        set => settings.Set(Key.RewindOnResume, value);
    }

    public LibrarySort LibrarySort
    {
        get => Enum.TryParse<LibrarySort>(settings.GetString(Key.LibrarySort), out var s) ? s : LibrarySort.Title;
        set => settings.Set(Key.LibrarySort, value.ToString());
    }

    /// <summary>Unset means the sort's own default direction.</summary>
    public bool LibrarySortDescending
    {
        get => settings.Get<bool?>(Key.LibrarySortDescending) ?? LibrarySort.DefaultDescending();
        set => settings.Set(Key.LibrarySortDescending, value);
    }

    public SeriesSort SeriesSort
    {
        get => Enum.TryParse<SeriesSort>(settings.GetString(Key.SeriesSort), out var s) ? s : SeriesSort.Name;
        set => settings.Set(Key.SeriesSort, value.ToString());
    }

    public bool SeriesSortDescending
    {
        get => settings.Get<bool?>(Key.SeriesSortDescending) ?? SeriesSort.DefaultDescending();
        set => settings.Set(Key.SeriesSortDescending, value);
    }

    public bool HideFinished
    {
        get => settings.Get<bool?>(Key.HideFinished) ?? false;
        set => settings.Set(Key.HideFinished, value);
    }

    public bool RemoveDownloadWhenFinished
    {
        get => settings.Get<bool?>(Key.RemoveDownloadWhenFinished) ?? false;
        set => settings.Set(Key.RemoveDownloadWhenFinished, value);
    }

    public bool DownloadOnWiFiOnly
    {
        get => settings.Get<bool?>(Key.DownloadOnWiFiOnly) ?? false;
        set => settings.Set(Key.DownloadOnWiFiOnly, value);
    }
}
