using AbsPlayer.Core.Models;

namespace AbsPlayer.Core.Playback;

/// <summary>One audio file of a book. Offset is where it starts in whole-book seconds.</summary>
public sealed record TrackInfo(Uri Url, double Offset, double Duration);

/// <summary>
/// The low-level audio engine: a queue of files played back to back.
/// Positions here are within the current file; <see cref="AudioPlayer"/> turns them into whole-book time.
/// Events may arrive on any thread.
/// </summary>
public interface IMediaEngine
{
    /// <summary>Queues all files, paused, starting at file <paramref name="index"/> and <paramref name="localSeconds"/> into it.</summary>
    void Open(IReadOnlyList<Uri> urls, int index, double localSeconds);
    void Play();
    void Pause();
    /// <summary>Moves to a spot in any file, keeping the current play/pause state.</summary>
    void Seek(int index, double localSeconds);
    void Close();

    /// <summary>Index of the file playing (or about to), or -1 once the last one has finished.</summary>
    int CurrentIndex { get; }
    /// <summary>Seconds into the current file. Before a file opens, the position it will open at.</summary>
    double LocalPosition { get; }
    double Rate { set; }
    double Volume { set; }

    /// <summary>The last file finished.</summary>
    event EventHandler? Ended;
    event EventHandler<string>? Failed;
}

public enum RemoteCommandKind { Play, Pause, Toggle, NextChapter, PreviousChapter, SkipForward, SkipBack, SeekTo, ChangeRate }

public sealed record RemoteCommand(RemoteCommandKind Kind, double Value = 0);

/// <summary>The system's Now Playing surface: on Windows, the media overlay and the keyboard media keys.</summary>
public interface INowPlaying
{
    void SetMetadata(string title, string? album, string? artist);
    void SetArtwork(Uri? uri);
    void SetState(bool playing, double position, double duration, double rate);
    void Clear();
    /// <summary>Buttons pressed in the overlay or on the keyboard. May arrive on any thread.</summary>
    event EventHandler<RemoteCommand>? Command;
}

/// <summary>Books saved on this PC (filled in by the downloads step).</summary>
public sealed record DownloadedBook(string Title, string? Author, List<Chapter> Chapters, double Duration);

public interface IOfflineLibrary
{
    DownloadedBook? Book(string itemId);
    IReadOnlyList<TrackInfo>? LocalTracks(string itemId);
    void Remove(string itemId);
}
