using System.Diagnostics;
using AbsPlayer.Core.Playback;

namespace AbsPlayer.Core.Tests;

/// <summary>
/// The real Windows.Media.Playback engine on generated silent WAV files, at volume 0.
/// Nothing is audible and no window opens. Skipped on CI (Category=Audio), where build machines may lack Media Foundation.
/// </summary>
[Trait("Category", "Audio")]
public class WindowsEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "absplayer-engine", Guid.NewGuid().ToString("N"));
    private readonly List<Uri> _files;

    public WindowsEngineTests()
    {
        Directory.CreateDirectory(_dir);
        _files = [WriteSilentWav("1.wav", 3), WriteSilentWav("2.wav", 3)];
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private Uri WriteSilentWav(string name, int seconds)
    {
        const int rate = 8000;
        var dataBytes = rate * 2 * seconds;
        var path = Path.Combine(_dir, name);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes); w.Write(new byte[dataBytes]);
        return new Uri(path);
    }

    private static async Task WaitFor(Func<bool> condition, string what, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Timed out waiting for: {what}");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Plays_seeks_across_files_advances_and_ends()
    {
        var (engine, _) = SmtcNowPlaying.Create();
        using var _e = engine;
        var ended = 0;
        engine.Ended += (_, _) => Interlocked.Increment(ref ended);
        engine.Volume = 0;

        // Start 1 s into the second file.
        engine.Open(_files, 1, 1.0);
        Assert.Equal(1, engine.CurrentIndex);
        Assert.Equal(1.0, engine.LocalPosition);
        engine.Play();
        await WaitFor(() => engine.CurrentIndex == 1 && engine.LocalPosition > 1.3, "playing past 1.3 s in file 2");

        // Back into the first file while playing.
        engine.Seek(0, 2.0);
        await WaitFor(() => engine.CurrentIndex == 0 && engine.LocalPosition >= 2.0 && engine.LocalPosition < 2.9, "file 1 at 2 s");

        // It moves on to the second file by itself, then finishes.
        await WaitFor(() => engine.CurrentIndex == 1, "advance to file 2");
        await WaitFor(() => ended == 1, "end of book");
        Assert.Equal(-1, engine.CurrentIndex);

        // Play again from the start after the end.
        engine.Seek(0, 0);
        engine.Play();
        await WaitFor(() => engine.CurrentIndex == 0 && engine.LocalPosition > 0.3, "replay from start");

        // Pause holds position.
        engine.Pause();
        await Task.Delay(300);
        var held = engine.LocalPosition;
        await Task.Delay(500);
        Assert.Equal(held, engine.LocalPosition, 2);

        engine.Close();
        Assert.Equal(-1, engine.CurrentIndex);
    }

    [Fact]
    public async Task Speed_changes_how_fast_position_moves()
    {
        var (engine, _) = SmtcNowPlaying.Create();
        using var _e = engine;
        engine.Volume = 0;
        engine.Rate = 2;
        engine.Open(_files, 0, 0);
        engine.Play();
        await WaitFor(() => engine.LocalPosition > 0.2, "started");
        var start = engine.LocalPosition;
        var sw = Stopwatch.StartNew();
        await Task.Delay(1000);
        var moved = engine.LocalPosition - start;
        var rate = moved / sw.Elapsed.TotalSeconds;
        Assert.InRange(rate, 1.6, 2.4);
        engine.Close();
    }

    [Fact]
    public async Task Speed_carries_over_into_the_next_file()
    {
        var (engine, _) = SmtcNowPlaying.Create();
        using var _e = engine;
        engine.Volume = 0;
        engine.Rate = 2;
        engine.Open(_files, 0, 2.0); // One second of file 1 left at 1x; half that at 2x.
        engine.Play();
        await WaitFor(() => engine.CurrentIndex == 1 && engine.LocalPosition > 0.2, "into file 2");
        var start = engine.LocalPosition;
        var sw = Stopwatch.StartNew();
        await Task.Delay(800);
        var rate = (engine.LocalPosition - start) / sw.Elapsed.TotalSeconds;
        Assert.InRange(rate, 1.6, 2.4);
        engine.Close();
    }

    [Fact]
    public async Task Missing_file_reports_failure()
    {
        var (engine, _) = SmtcNowPlaying.Create();
        using var _e = engine;
        string? failure = null;
        engine.Failed += (_, m) => failure = m;
        engine.Open([new Uri(Path.Combine(_dir, "missing.mp3"))], 0, 0);
        engine.Play();
        await WaitFor(() => failure is not null, "failure");
    }

    [Fact]
    public void Overlay_accepts_updates()
    {
        var (engine, np) = SmtcNowPlaying.Create();
        using var _e = engine;
        np.SetMetadata("Chapter 1", "The Book", "An Author");
        np.SetState(playing: false, position: 10, duration: 100, rate: 1.25);
        np.SetArtwork(_files[0]); // Not an image; must not throw.
        np.Clear();
    }
}
