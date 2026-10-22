namespace AbsPlayer.Core.Storage;

public enum LibrarySort { Title, Author, RecentlyAdded, Duration }

public static class LibrarySortExtensions
{
    public static string Label(this LibrarySort s) => s switch
    {
        LibrarySort.Title => "Title",
        LibrarySort.Author => "Author",
        LibrarySort.RecentlyAdded => "Recently Added",
        _ => "Length",
    };

    public static string ApiField(this LibrarySort s) => s switch
    {
        LibrarySort.Title => "media.metadata.title",
        LibrarySort.Author => "media.metadata.authorName",
        LibrarySort.RecentlyAdded => "addedAt",
        _ => "media.duration",
    };

    public static bool Descending(this LibrarySort s) => s == LibrarySort.RecentlyAdded;
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
