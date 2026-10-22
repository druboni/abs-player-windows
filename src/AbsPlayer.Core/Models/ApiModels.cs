using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AbsPlayer.Core.Models;

public sealed class LoginRequest
{
    public required string Username { get; init; }
    public required string Password { get; init; }
}

public sealed class LoginResponse
{
    public required UserResponse User { get; init; }
}

public sealed class UserResponse
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    /// <summary>Long-lived token (all servers so far; deprecated in newer Audiobookshelf).</summary>
    public string? Token { get; init; }
    /// <summary>Short-lived token pair from servers that support it (sent when we ask with x-return-tokens).</summary>
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
}

public sealed class RefreshResponse
{
    public sealed class Tokens
    {
        public required string AccessToken { get; init; }
        public string? RefreshToken { get; init; }
    }

    public required Tokens User { get; init; }
}

public sealed class LibrariesResponse
{
    public required List<Library> Libraries { get; init; }
}

public sealed record Library(string Id, string Name, string? Icon, string? MediaType = null)
{
    /// <summary>Podcast libraries aren't supported (playing an episode needs an episode id).</summary>
    public bool IsBooks => MediaType is null or "book";
}

public sealed class LibraryItemsResponse
{
    [JsonConverter(typeof(LossyListConverter<LibraryItem>))]
    public required List<LibraryItem> Results { get; init; }
    public int Total { get; init; }
    public int Limit { get; init; }
    public int Page { get; init; }
}

public sealed partial class LibraryItem : IEquatable<LibraryItem>
{
    public required string Id { get; init; }
    public string? LibraryId { get; init; }
    /// <summary>"book" or "podcast".</summary>
    public string? MediaType { get; init; }
    public required Media Media { get; init; }
    /// <summary>Some series endpoints put the book's number here instead of in the metadata.</summary>
    public string? SeriesSequence { get; init; }
    /// <summary>Total size of the book's files in bytes, when the server includes it.</summary>
    public long? Size { get; init; }

    /// <summary>
    /// The book's number within a series, from whichever field the server filled in.
    /// Falls back to parsing "Series Name #3" out of the display name.
    /// </summary>
    public string? Sequence(string seriesName)
    {
        var s = SeriesSequence ?? Media.Metadata.SeriesSequence;
        if (!string.IsNullOrEmpty(s)) return s;
        if (Media.Metadata.SeriesName is { } names)
        {
            var prefix = seriesName + " #";
            foreach (var part in names.Split(", "))
                if (part.StartsWith(prefix, StringComparison.Ordinal)) return part[prefix.Length..];
        }
        // Last resort: titles like "Dungeon Crawler Carl, Book 3".
        var m = BookNumber().Match(Media.Metadata.Title);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\bBook (\d+(\.\d+)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BookNumber();

    public bool Equals(LibraryItem? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as LibraryItem);
    public override int GetHashCode() => Id.GetHashCode();
}

public sealed class Media
{
    public required Metadata Metadata { get; init; }
    public string? CoverPath { get; init; }
    public double? Duration { get; init; }
    [JsonConverter(typeof(LossyListConverter<Chapter>))]
    public List<Chapter>? Chapters { get; init; }
    /// <summary>Present on expanded items; used to download the audio files.</summary>
    public List<AudioTrack>? Tracks { get; init; }
}

public sealed class Metadata
{
    public required string Title { get; init; }
    public string? AuthorName { get; init; }
    public string? Description { get; init; }
    public string? SeriesName { get; init; }

    /// <summary>The series list returns one series object; expanded items return an array.</summary>
    [JsonConverter(typeof(OneOrManyConverter<SeriesRef>))]
    public List<SeriesRef>? Series { get; init; }

    /// <summary>Position in its series ("1", "2.5"), when the server includes it.</summary>
    [JsonIgnore]
    public string? SeriesSequence => Series?.FirstOrDefault()?.Sequence;

    public sealed record SeriesRef(string? Sequence);
}

public sealed record AudioTrack(int Index, double StartOffset, double Duration, string? Title, string? ContentUrl,
    string? MimeType = null, TrackFile? Metadata = null);

/// <summary>The audio file behind a track: its name, extension (".m4b") and size in bytes.</summary>
public sealed record TrackFile(string? Filename, string? Ext, long? Size);

public sealed record Chapter(int Id, double Start, double End, string Title);

public sealed class PlaybackSessionResponse
{
    public required string Id { get; init; }
    public required string LibraryItemId { get; init; }
    public string? DisplayTitle { get; init; }
    public string? DisplayAuthor { get; init; }
    [JsonConverter(typeof(LossyListConverter<AudioTrack>))]
    public required List<AudioTrack> AudioTracks { get; init; }
    [JsonConverter(typeof(LossyListConverter<Chapter>))]
    public required List<Chapter> Chapters { get; init; }
    public double Duration { get; init; }
    public double CurrentTime { get; init; }
}

public sealed class PlayItemRequest
{
    public required DeviceInfo DeviceInfo { get; init; }
    public bool ForceDirectPlay => true;
    public bool ForceTranscode => false;
    public string MediaPlayer => "WindowsMediaPlayer";
    public string[] SupportedMimeTypes =>
        ["audio/mpeg", "audio/mp4", "audio/aac", "audio/x-m4a", "audio/x-m4b", "audio/flac", "audio/wav"];
}

public sealed class DeviceInfo
{
    public required string DeviceId { get; init; }
    public required string ClientName { get; init; }
    public required string ClientVersion { get; init; }
    public string Manufacturer => "Microsoft";
    public required string Model { get; init; }
}

/// <summary>Body for /api/session/:id/sync and /close. TimeListened is wall-clock seconds since the last sync.</summary>
public sealed record SessionSyncRequest(double CurrentTime, double TimeListened, double Duration);

public sealed class MediaProgressResponse
{
    public string? Id { get; init; }
    public required string LibraryItemId { get; init; }
    public double CurrentTime { get; init; }
    public double Duration { get; init; }
    public double Progress { get; init; }
    public bool IsFinished { get; init; }
    /// <summary>Milliseconds since 1970.</summary>
    public double? LastUpdate { get; init; }
}

public sealed record ProgressUpdateRequest(double CurrentTime, double Duration, double Progress, bool IsFinished);

public sealed class MeResponse
{
    [JsonConverter(typeof(LossyListConverter<ProgressEntry>))]
    public List<ProgressEntry>? MediaProgress { get; init; }
    [JsonConverter(typeof(LossyListConverter<Bookmark>))]
    public List<Bookmark>? Bookmarks { get; init; }
}

public sealed class Bookmark
{
    public required string LibraryItemId { get; init; }
    public required string Title { get; set; }
    public double Time { get; init; }
    public double? CreatedAt { get; init; }

    [JsonIgnore]
    public string Id => $"{LibraryItemId}-{Time}";
}

public sealed record BookmarkRequest(int Time, string Title);

public sealed class ProgressEntry
{
    public required string LibraryItemId { get; init; }
    public string? EpisodeId { get; init; }
    public double? Progress { get; init; }
    public bool? IsFinished { get; init; }
}

/// <summary>Items the user has started; elements that don't parse as a book are skipped.</summary>
public sealed class InProgressResponse
{
    [JsonConverter(typeof(LossyListConverter<LibraryItem>))]
    public required List<LibraryItem> LibraryItems { get; init; }
}

public sealed class BookSeries : IEquatable<BookSeries>
{
    public string Id { get; }
    public string Name { get; }
    /// <summary>Books in reading order.</summary>
    [JsonConverter(typeof(LossyListConverter<LibraryItem>))]
    public List<LibraryItem> Books { get; }

    [JsonConstructor]
    public BookSeries(string id, string name, List<LibraryItem>? books)
    {
        Id = id;
        Name = name;
        Books = (books ?? []).OrderBy(b => Order(b, name)).ToList();
    }

    private static double Order(LibraryItem item, string series) =>
        double.TryParse(item.Sequence(series), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue;

    public bool Equals(BookSeries? other) => other is not null && other.Id == Id;
    public override bool Equals(object? obj) => Equals(obj as BookSeries);
    public override int GetHashCode() => Id.GetHashCode();
}

public sealed class SeriesListResponse
{
    [JsonConverter(typeof(LossyListConverter<BookSeries>))]
    public required List<BookSeries> Results { get; init; }
    public int Total { get; init; }
}

public sealed record Author(string Id, string Name, int? NumBooks);

public sealed class SearchResponse
{
    public required List<LibraryItem> Books { get; init; }
    public required List<BookSeries> Series { get; init; }
    public required List<Author> Authors { get; init; }

    public bool IsEmpty => Books.Count == 0 && Series.Count == 0 && Authors.Count == 0;

    /// <summary>The server's search result shape, before it's flattened into <see cref="SearchResponse"/>.</summary>
    internal sealed class Raw
    {
        [JsonConverter(typeof(LossyListConverter<BookHit>))]
        public List<BookHit>? Book { get; init; }
        [JsonConverter(typeof(LossyListConverter<SeriesHit>))]
        public List<SeriesHit>? Series { get; init; }
        [JsonConverter(typeof(LossyListConverter<Author>))]
        public List<Author>? Authors { get; init; }

        public SearchResponse ToResponse() => new()
        {
            Books = (Book ?? []).Select(b => b.LibraryItem).ToList(),
            Series = (Series ?? []).Select(s => new BookSeries(s.Series.Id, s.Series.Name, s.Books)).ToList(),
            Authors = Authors ?? [],
        };
    }

    internal sealed class BookHit
    {
        public required LibraryItem LibraryItem { get; init; }
    }

    internal sealed class SeriesHit
    {
        public required SeriesInfo Series { get; init; }
        [JsonConverter(typeof(LossyListConverter<LibraryItem>))]
        public List<LibraryItem>? Books { get; init; }
    }

    internal sealed record SeriesInfo(string Id, string Name);
}
