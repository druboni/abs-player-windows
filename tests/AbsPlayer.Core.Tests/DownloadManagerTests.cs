using System.Diagnostics;
using System.Net;
using AbsPlayer.Core.Downloads;
using AbsPlayer.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace AbsPlayer.Core.Tests;

/// <summary>A stream that fails part-way, like a dropped connection.</summary>
internal sealed class BreakingStream(byte[] data, int breakAfter) : Stream
{
    private int _pos;
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_pos >= breakAfter) throw new IOException("connection reset");
        var n = Math.Min(count, Math.Min(breakAfter, data.Length) - _pos);
        Array.Copy(data, _pos, buffer, offset, n);
        _pos += n;
        return n;
    }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => data.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public class DownloadManagerTests : IDisposable
{
    private static readonly byte[] File1 = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
    private static readonly byte[] File2 = Enumerable.Range(0, 120_000).Select(i => (byte)(i % 13)).ToArray();
    private static readonly byte[] Cover = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly TestEnv _env = new(server: "http://abs.test");
    private readonly Prefs _prefs;
    private readonly string _root;

    /// <summary>Set per test: how to answer a file request (null = normal, Range-aware).</summary>
    private Func<HttpRequestMessage, string, HttpResponseMessage?>? _override;
    private int _fileRequests;

    public DownloadManagerTests()
    {
        _prefs = new Prefs(_env.Prefs.Settings);
        _root = Path.Combine(_env.Dir, "Downloads");
        _env.Handler.Respond = req => Task.FromResult(Route(req));
    }

    public void Dispose() => _env.Dispose();

    private HttpResponseMessage Route(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == "/api/items/b1")
            return StubHandler.Json("""
                {"id":"b1","mediaType":"book","media":{"metadata":{"title":"The Book","authorName":"Writer"},"duration":300,
                 "chapters":[{"id":0,"start":0,"end":150,"title":"One"},{"id":1,"start":150,"end":300,"title":"Two"}],
                 "tracks":[
                   {"index":2,"startOffset":180,"duration":120,"contentUrl":"/api/items/b1/file/2","mimeType":"audio/mpeg","metadata":{"filename":"part 2.mp3","ext":".mp3","size":120000}},
                   {"index":1,"startOffset":0,"duration":180,"contentUrl":"/api/items/b1/file/1","mimeType":"audio/mp4","metadata":{"filename":"part 1.m4b","ext":".m4b","size":200000}}]}}
                """);
        if (path == "/api/items/b1/cover") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Cover) };
        if (path.StartsWith("/api/items/b1/file/"))
        {
            Interlocked.Increment(ref _fileRequests);
            var file = path.EndsWith("/1") ? "1" : "2";
            if (_override?.Invoke(req, file) is { } r) return r;
            return Serve(req, file == "1" ? File1 : File2);
        }
        return StubHandler.Status(HttpStatusCode.NotFound);
    }

    /// <summary>Answers like a real server: whole file, or from the requested byte with 206.</summary>
    private static HttpResponseMessage Serve(HttpRequestMessage req, byte[] data)
    {
        var from = req.Headers.Range?.Ranges.First().From ?? 0;
        if (from >= data.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
        return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data[(int)from..]),
        };
    }

    private DownloadManager Manager(Func<bool>? metered = null, TimeProvider? clock = null) =>
        new(_env.Repo, _env.Http, _prefs, _root, metered, clock);

    private static async Task WaitFor(DownloadManager m, DownloadKind kind, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (m.Status("b1").Kind != kind)
        {
            if (sw.ElapsedMilliseconds > timeoutMs) Assert.Fail($"Expected {kind}, still {m.Status("b1")}");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Downloads_every_file_with_its_real_extension_plus_cover_and_manifest()
    {
        var m = Manager();
        m.Download("b1");
        await WaitFor(m, DownloadKind.Downloaded);

        var dir = m.Folder("b1");
        Assert.Equal(File1, await File.ReadAllBytesAsync(Path.Combine(dir, "track-001.m4b")));
        Assert.Equal(File2, await File.ReadAllBytesAsync(Path.Combine(dir, "track-002.mp3")));
        Assert.Equal(Cover, await File.ReadAllBytesAsync(Path.Combine(dir, "cover.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "plan.json")));
        Assert.Empty(Directory.GetFiles(dir, "*.part"));

        var tracks = m.LocalTracks("b1")!;
        Assert.Equal([0.0, 180.0], tracks.Select(t => t.Offset));
        Assert.True(tracks[0].Url.IsFile);
        Assert.Equal("The Book", m.Book("b1")!.Title);
        Assert.Equal(2, m.Book("b1")!.Chapters.Count);
        Assert.Equal(320_000, m.TotalBytes);
        Assert.NotNull(m.LocalCover("b1"));
        Assert.Equal("b1", m.Books.Single().ItemId);

        // Downloads carry the token, which a new sign-in would change, so it's added at download time.
        Assert.All(_env.Handler.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.Contains("/file/")),
            r => Assert.Contains("token=tok", r.Request.RequestUri!.Query));

        // A fresh start (app relaunch) finds it again.
        Assert.Equal(DownloadKind.Downloaded, Manager().Status("b1").Kind);
    }

    [Fact]
    public async Task A_dropped_connection_resumes_from_where_it_stopped()
    {
        var dropped = false;
        _override = (req, file) =>
        {
            if (file != "1" || dropped) return null;
            dropped = true;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(File1, 70_000)) };
        };
        var m = Manager();
        m.Download("b1");
        await WaitFor(m, DownloadKind.Downloaded);

        var file1Requests = _env.Handler.Requests.Where(r => r.Request.RequestUri!.AbsolutePath.EndsWith("/file/1")).ToList();
        Assert.Equal(2, file1Requests.Count);
        Assert.Equal(70_000, file1Requests[1].Request.Headers.Range!.Ranges.First().From);
        Assert.Equal(File1, await File.ReadAllBytesAsync(Path.Combine(m.Folder("b1"), "track-001.m4b")));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_gets_the_file_from_the_start()
    {
        var dropped = false;
        _override = (req, file) =>
        {
            if (file != "1") return null;
            if (!dropped) { dropped = true; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(File1, 50_000)) }; }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File1) }; // Range ignored.
        };
        var m = Manager();
        m.Download("b1");
        await WaitFor(m, DownloadKind.Downloaded);
        Assert.Equal(File1, await File.ReadAllBytesAsync(Path.Combine(m.Folder("b1"), "track-001.m4b")));
    }

    [Fact]
    public async Task Unfinished_download_continues_after_the_app_restarts()
    {
        // File 2 fails for good the first time, leaving file 1 done and the plan saved.
        _override = (_, file) => file == "2" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null;
        var first = Manager();
        first.Download("b1");
        await WaitFor(first, DownloadKind.Failed);
        Assert.Equal("The server answered 403", first.Status("b1").Message);
        Assert.True(File.Exists(Path.Combine(first.Folder("b1"), "track-001.m4b")));

        _override = null;
        var before = _fileRequests;
        var second = Manager(); // Relaunch.
        second.ResumeUnfinished();
        await WaitFor(second, DownloadKind.Downloaded);
        Assert.Equal(1, _fileRequests - before); // Only file 2 was fetched again.
    }

    [Fact]
    public async Task Try_again_resumes_only_that_book()
    {
        _override = (_, file) => file == "2" ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;
        var m = Manager();
        m.Download("b1");
        await WaitFor(m, DownloadKind.Failed);
        _override = null;
        m.Retry("b1");
        await WaitFor(m, DownloadKind.Downloaded);
    }

    [Fact]
    public async Task Cancel_deletes_everything_and_it_doesnt_come_back_after_a_restart()
    {
        var release = new TaskCompletionSource();
        _override = (req, file) =>
        {
            if (file != "1") return null;
            release.Task.Wait(); // Hold the download open.
            return Serve(req, File1);
        };
        var m = Manager();
        m.Download("b1");
        await Task.Delay(200);
        m.Cancel("b1");
        release.SetResult();
        Assert.Equal(DownloadKind.None, m.Status("b1").Kind);

        var sw = Stopwatch.StartNew();
        while (Directory.Exists(m.Folder("b1")) && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        Assert.False(Directory.Exists(m.Folder("b1")));

        var after = Manager();
        after.ResumeUnfinished();
        Assert.Equal(DownloadKind.None, after.Status("b1").Kind);
    }

    [Fact]
    public async Task Waits_while_the_connection_is_metered_when_asked_to()
    {
        _prefs.DownloadOnWiFiOnly = true;
        var metered = true;
        var clock = new FakeTimeProvider();
        var m = Manager(() => metered, clock);
        m.Download("b1");
        await WaitFor(m, DownloadKind.WaitingForUnmetered);
        Assert.Equal(0, _fileRequests);

        metered = false;
        clock.Advance(TimeSpan.FromSeconds(31));
        await WaitFor(m, DownloadKind.Downloaded);
    }

    [Fact]
    public async Task Metered_is_ignored_when_the_setting_is_off()
    {
        var m = Manager(() => true);
        m.Download("b1");
        await WaitFor(m, DownloadKind.Downloaded);
    }

    [Fact]
    public async Task Remove_deletes_the_files()
    {
        var m = Manager();
        m.Download("b1");
        await WaitFor(m, DownloadKind.Downloaded);
        var changes = 0;
        m.Changed += (_, id) => { if (id == "b1") changes++; };
        m.Remove("b1");
        Assert.False(Directory.Exists(m.Folder("b1")));
        Assert.Null(m.LocalTracks("b1"));
        Assert.Equal(DownloadKind.None, m.Status("b1").Kind);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task A_book_with_missing_files_isnt_treated_as_downloaded()
    {
        var m = Manager();
        m.Download("b1");
        await WaitFor(m, DownloadKind.Downloaded);
        File.Delete(Path.Combine(m.Folder("b1"), "track-002.mp3"));
        Assert.Equal(DownloadKind.None, Manager().Status("b1").Kind);
    }

    [Fact]
    public async Task Progress_is_reported_by_bytes()
    {
        var gate = new TaskCompletionSource();
        _override = (req, file) =>
        {
            if (file != "2") return null;
            gate.Task.Wait();
            return Serve(req, File2);
        };
        var m = Manager();
        m.Download("b1");
        var sw = Stopwatch.StartNew();
        while (m.Status("b1").Progress < 0.6 && sw.ElapsedMilliseconds < 5000) await Task.Delay(20);
        var status = m.Status("b1");
        Assert.Equal(DownloadKind.Downloading, status.Kind);
        Assert.Equal(200_000.0 / 320_000, status.Progress, 3); // File 1 done, file 2 waiting.
        gate.SetResult();
        await WaitFor(m, DownloadKind.Downloaded);
    }
}
