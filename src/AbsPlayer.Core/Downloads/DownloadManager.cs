using System.Net;
using System.Text.Json;
using AbsPlayer.Core.Api;
using AbsPlayer.Core.Models;
using AbsPlayer.Core.Playback;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core.Downloads;

public enum DownloadKind { None, Queued, WaitingForUnmetered, Downloading, Downloaded, Failed }

/// <summary>Where a book stands. Progress is 0–1; SecondsLeft once the speed is known.</summary>
public sealed record DownloadStatus(DownloadKind Kind, double Progress = 0, string? Message = null, double? SecondsLeft = null);

/// <summary>A book saved on this PC: its audio files, chapters and cover. Stored as manifest.json in its folder.</summary>
public sealed record DownloadManifest(
    string ItemId, string Title, string? Author, double Duration, List<Chapter> Chapters,
    List<DownloadManifest.Track> Tracks, DateTimeOffset DownloadedAt, long SizeBytes)
{
    public sealed record Track(string FileName, double Offset, double Duration);

    /// <summary>Stand-in library item so downloaded books show in the normal grid, even offline.</summary>
    public LibraryItem ToLibraryItem() => new()
    {
        Id = ItemId,
        MediaType = "book",
        Media = new Media
        {
            Metadata = new Metadata { Title = Title, AuthorName = Author },
            Duration = Duration,
            Chapters = Chapters,
        },
    };
}

/// <summary>
/// Downloads books for offline listening. One file at a time per book, up to two books at once.
/// Unfinished files are kept as .part and resumed with a Range request, after a dropped
/// connection or the next time the app starts. Events arrive through <c>dispatch</c>.
/// </summary>
public sealed class DownloadManager : IOfflineLibrary
{
    private const int MaxBooksAtOnce = 2;
    private const int Retries = 3;

    private readonly AudiobookRepository _repo;
    private readonly HttpClient _http;
    private readonly Prefs _prefs;
    private readonly string _root;
    private readonly Func<bool> _isMetered;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _dispatch;
    private readonly SemaphoreSlim _slots = new(MaxBooksAtOnce);
    private readonly Lock _lock = new();

    private readonly Dictionary<string, DownloadManifest> _books = [];
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly Dictionary<string, string> _failures = [];

    /// <summary>Raised (with the item id) whenever a book's download state changes.</summary>
    public event EventHandler<string>? Changed;

    /// <param name="isMetered">True on a metered connection (phone hotspot, capped plan).</param>
    public DownloadManager(AudiobookRepository repo, HttpClient http, Prefs prefs, string root,
        Func<bool>? isMetered = null, TimeProvider? clock = null, Action<Action>? dispatch = null)
    {
        _repo = repo;
        _http = http;
        _prefs = prefs;
        _root = root;
        _isMetered = isMetered ?? (() => false);
        _clock = clock ?? TimeProvider.System;
        _dispatch = dispatch ?? (a => a());
        Directory.CreateDirectory(root);
        LoadManifests();
    }

    public string Root => _root;

    // Queries

    public DownloadStatus Status(string itemId)
    {
        lock (_lock)
        {
            if (_books.ContainsKey(itemId)) return new(DownloadKind.Downloaded, 1);
            if (_jobs.TryGetValue(itemId, out var job)) return job.Status();
            if (_failures.TryGetValue(itemId, out var f)) return new(DownloadKind.Failed, Message: f);
            return new(DownloadKind.None);
        }
    }

    public IReadOnlyList<DownloadManifest> Books
    {
        get { lock (_lock) return _books.Values.OrderByDescending(b => b.DownloadedAt).ToList(); }
    }

    public long TotalBytes
    {
        get { lock (_lock) return _books.Values.Sum(b => b.SizeBytes); }
    }

    /// <summary>Free space on the drive downloads go to, in bytes.</summary>
    public long? FreeSpace
    {
        get
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_root))!).AvailableFreeSpace; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return null; }
        }
    }

    public DownloadManifest? Manifest(string itemId)
    {
        lock (_lock) return _books.GetValueOrDefault(itemId);
    }

    // IOfflineLibrary (used by the player)

    public DownloadedBook? Book(string itemId) =>
        Manifest(itemId) is { } m ? new DownloadedBook(m.Title, m.Author, m.Chapters, m.Duration) : null;

    public IReadOnlyList<TrackInfo>? LocalTracks(string itemId) =>
        Manifest(itemId) is { } m
            ? m.Tracks.Select(t => new TrackInfo(new Uri(Path.Combine(Folder(itemId), t.FileName)), t.Offset, t.Duration)).ToList()
            : null;

    public Uri? LocalCover(string itemId)
    {
        if (Manifest(itemId) is null) return null;
        var path = Path.Combine(Folder(itemId), "cover.jpg");
        return File.Exists(path) ? new Uri(path) : null;
    }

    // Download, cancel, remove

    /// <summary>Starts downloading a book (does nothing if it's already downloaded or downloading).</summary>
    public void Download(string itemId)
    {
        Job job;
        lock (_lock)
        {
            if (_books.ContainsKey(itemId) || _jobs.ContainsKey(itemId)) return;
            _failures.Remove(itemId);
            job = new Job(itemId);
            _jobs[itemId] = job;
        }
        Raise(itemId);
        _ = Task.Run(() => Run(job, plan: null));
    }

    public void Cancel(string itemId)
    {
        Job? job;
        lock (_lock)
        {
            if (!_jobs.Remove(itemId, out job)) return;
        }
        // The download loop deletes the folder once its files are closed.
        job.Cts.Cancel();
        Raise(itemId);
    }

    public void Remove(string itemId)
    {
        bool downloading;
        lock (_lock) downloading = _jobs.ContainsKey(itemId);
        Cancel(itemId);
        lock (_lock)
        {
            _books.Remove(itemId);
            _failures.Remove(itemId);
        }
        if (!downloading) TryDelete(Folder(itemId));
        Raise(itemId);
    }

    public void RemoveAll()
    {
        List<string> ids;
        lock (_lock) ids = [.. _books.Keys, .. _jobs.Keys, .. _failures.Keys];
        foreach (var id in ids.Distinct()) Remove(id);
    }

    /// <summary>Picks up downloads that were in progress (or had failed) when the app last closed.</summary>
    public void ResumeUnfinished()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root)) ResumeFrom(dir);
    }

    /// <summary>Restarts one book from its saved plan. False if there's no usable plan.</summary>
    private bool ResumeFrom(string dir)
    {
        var planPath = Path.Combine(dir, "plan.json");
        if (!File.Exists(planPath)) return false;
        Plan? plan;
        try { plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(planPath), AbsJson.Options); }
        catch (Exception e) when (e is JsonException or IOException) { return false; }
        if (plan is null) return false;
        Job job;
        lock (_lock)
        {
            if (_books.ContainsKey(plan.ItemId) || _jobs.ContainsKey(plan.ItemId)) return true;
            _failures.Remove(plan.ItemId);
            job = new Job(plan.ItemId);
            _jobs[plan.ItemId] = job;
        }
        Raise(plan.ItemId);
        _ = Task.Run(() => Run(job, plan));
        return true;
    }

    public string Folder(string itemId) => Path.Combine(_root, Sanitize(itemId));

    // The work

    private async Task Run(Job job, Plan? plan)
    {
        var ct = job.Cts.Token;
        try
        {
            plan ??= await MakePlan(job.ItemId).ConfigureAwait(false);
            job.Plan = plan;
            Raise(job.ItemId);

            await _slots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                foreach (var part in plan.Parts.Where(p => !p.Done))
                {
                    await WaitForUnmetered(job, ct).ConfigureAwait(false);
                    await DownloadPart(job, part, ct).ConfigureAwait(false);
                    part.Done = true;
                    SavePlan(plan);
                }
            }
            finally { _slots.Release(); }

            await Finish(job, plan, ct).ConfigureAwait(false);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Cancelled or removed. Files are closed now, so the folder can go.
            bool kept;
            lock (_lock) kept = _books.ContainsKey(job.ItemId) || _jobs.ContainsKey(job.ItemId);
            if (!kept) TryDelete(Folder(job.ItemId));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Fail(job, e.Message);
        }
    }

    private async Task<Plan> MakePlan(string itemId)
    {
        var item = await _repo.GetItem(itemId).ConfigureAwait(false);
        var parts = (item.Media.Tracks ?? []).OrderBy(t => t.Index)
            .Where(t => t.ContentUrl is not null)
            .Select(t => new Part
            {
                FileName = $"track-{t.Index:000}{Extension(t)}",
                ContentUrl = t.ContentUrl!,
                Offset = t.StartOffset,
                Duration = t.Duration,
                Size = t.Metadata?.Size,
            })
            .ToList();
        if (parts.Count == 0) throw new InvalidDataException("This book has no downloadable audio");
        var plan = new Plan
        {
            ItemId = itemId,
            Title = item.Media.Metadata.Title,
            Author = item.Media.Metadata.AuthorName,
            Duration = item.Media.Duration ?? parts.Sum(p => p.Duration),
            Chapters = item.Media.Chapters ?? [],
            Parts = parts,
        };
        Directory.CreateDirectory(Folder(itemId));
        SavePlan(plan);
        return plan;
    }

    /// <summary>The file's real extension, so Windows knows how to play it ("/file/123" has none).</summary>
    private static string Extension(AudioTrack t)
    {
        var ext = t.Metadata?.Ext;
        if (string.IsNullOrEmpty(ext)) ext = Path.GetExtension(t.Metadata?.Filename ?? "");
        if (string.IsNullOrEmpty(ext))
        {
            ext = t.MimeType switch
            {
                "audio/mpeg" or "audio/mp3" => ".mp3",
                "audio/flac" => ".flac",
                "audio/ogg" => ".ogg",
                "audio/opus" => ".opus",
                "audio/wav" or "audio/x-wav" => ".wav",
                "audio/webm" => ".webm",
                _ => ".m4b",
            };
        }
        ext = ext.StartsWith('.') ? ext : "." + ext;
        return ext.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? ext.ToLowerInvariant() : ".m4b";
    }

    private async Task WaitForUnmetered(Job job, CancellationToken ct)
    {
        while (_prefs.DownloadOnWiFiOnly && _isMetered())
        {
            job.Waiting = true;
            Raise(job.ItemId);
            await Task.Delay(TimeSpan.FromSeconds(30), _clock, ct).ConfigureAwait(false);
        }
        if (job.Waiting) { job.Waiting = false; Raise(job.ItemId); }
    }

    /// <summary>Downloads one file into .part, resuming what's there, then renames it. Retries dropped connections.</summary>
    private async Task DownloadPart(Job job, Part part, CancellationToken ct)
    {
        var final = Path.Combine(Folder(job.ItemId), part.FileName);
        var partial = final + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Transfer(job, part, partial, ct).ConfigureAwait(false);
                File.Move(partial, final, overwrite: true);
                job.Received[part.FileName] = new FileInfo(final).Length;
                return;
            }
            catch (Exception e) when (attempt < Retries && !ct.IsCancellationRequested && e is HttpRequestException or IOException
                                      && e is not HttpStatusException { Permanent: true })
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), _clock, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task Transfer(Job job, Part part, string partial, CancellationToken ct)
    {
        var url = _repo.MediaUrl(part.ContentUrl) ?? throw new InvalidDataException("Not signed in");
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (res.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && part.Size == have) return; // Already complete.
        if (!res.IsSuccessStatusCode)
            throw new HttpStatusException((int)res.StatusCode);
        var append = have > 0 && res.StatusCode == HttpStatusCode.PartialContent;
        if (!append) have = 0; // Server ignored the range: start the file again.
        part.Size ??= res.Content.Headers.ContentLength is { } len ? len + have : null;

        await using var source = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var file = new FileStream(partial, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 16];
        var lastReport = _clock.GetTimestamp();
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            have += read;
            job.Received[part.FileName] = have;
            if (_clock.GetElapsedTime(lastReport) > TimeSpan.FromMilliseconds(250))
            {
                lastReport = _clock.GetTimestamp();
                job.MeasureSpeed(_clock);
                Raise(job.ItemId);
            }
        }
        if (part.Size is { } expected && have < expected) throw new IOException("The connection dropped before the file finished");
    }

    private async Task Finish(Job job, Plan plan, CancellationToken ct)
    {
        var dir = Folder(plan.ItemId);
        if (_repo.CoverUrl(plan.ItemId) is { Scheme: "http" or "https" } cover)
        {
            try { await File.WriteAllBytesAsync(Path.Combine(dir, "cover.jpg"), await _http.GetByteArrayAsync(cover, ct).ConfigureAwait(false), ct).ConfigureAwait(false); }
            catch (HttpRequestException) { } // A missing cover doesn't stop the book playing.
        }
        var manifest = new DownloadManifest(plan.ItemId, plan.Title, plan.Author, plan.Duration, plan.Chapters,
            plan.Parts.Select(p => new DownloadManifest.Track(p.FileName, p.Offset, p.Duration)).ToList(),
            _clock.GetUtcNow(),
            plan.Parts.Sum(p => new FileInfo(Path.Combine(dir, p.FileName)).Length));
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, AbsJson.Options), ct).ConfigureAwait(false);
        TryDeleteFile(Path.Combine(dir, "plan.json"));
        lock (_lock)
        {
            if (!_jobs.Remove(plan.ItemId)) return; // Cancelled at the last moment.
            _books[plan.ItemId] = manifest;
        }
        Raise(plan.ItemId);
    }

    private void Fail(Job job, string message)
    {
        lock (_lock)
        {
            if (!_jobs.Remove(job.ItemId)) return;
            _failures[job.ItemId] = message;
        }
        // Keep the folder (and its .part files) so Try Again resumes rather than starting over.
        Raise(job.ItemId);
    }

    /// <summary>Try Again after a failure: resumes from the saved plan when there is one.</summary>
    public void Retry(string itemId)
    {
        if (!ResumeFrom(Folder(itemId)))
        {
            lock (_lock) _failures.Remove(itemId);
            Download(itemId);
        }
    }

    // Storage

    private void LoadManifests()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var path = Path.Combine(dir, "manifest.json");
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonSerializer.Deserialize<DownloadManifest>(File.ReadAllText(path), AbsJson.Options) is { } m
                    && m.Tracks.All(t => File.Exists(Path.Combine(dir, t.FileName))))
                    _books[m.ItemId] = m;
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
    }

    private void SavePlan(Plan plan) =>
        File.WriteAllText(Path.Combine(Folder(plan.ItemId), "plan.json"), JsonSerializer.Serialize(plan, AbsJson.Options));

    private void Raise(string itemId) => _dispatch(() => Changed?.Invoke(this, itemId));

    private static string Sanitize(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>What still has to be fetched. Saved as plan.json so a download survives a restart.</summary>
    private sealed class Plan
    {
        public required string ItemId { get; init; }
        public required string Title { get; init; }
        public string? Author { get; init; }
        public double Duration { get; init; }
        public List<Chapter> Chapters { get; init; } = [];
        public List<Part> Parts { get; init; } = [];
    }

    private sealed class Part
    {
        public required string FileName { get; init; }
        /// <summary>The server path; the token is added when downloading, so a new sign-in still works.</summary>
        public required string ContentUrl { get; init; }
        public double Offset { get; init; }
        public double Duration { get; init; }
        public long? Size { get; set; }
        public bool Done { get; set; }
    }

    private sealed class HttpStatusException(int status) : HttpRequestException($"The server answered {status}")
    {
        /// <summary>4xx (other than timeouts and rate limits) won't fix themselves by retrying.</summary>
        public bool Permanent => status is >= 400 and < 500 and not 408 and not 429;
    }

    /// <summary>One book being downloaded.</summary>
    private sealed class Job(string itemId)
    {
        public string ItemId { get; } = itemId;
        public CancellationTokenSource Cts { get; } = new();
        public Plan? Plan { get; set; }
        public bool Waiting { get; set; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, long> Received { get; } = new();
        private long _sampleBytes;
        private long _sampleAt;
        private double? _speed;

        public void MeasureSpeed(TimeProvider clock)
        {
            var bytes = Received.Values.Sum();
            if (_sampleAt == 0) { _sampleAt = clock.GetTimestamp(); _sampleBytes = bytes; return; }
            var dt = clock.GetElapsedTime(_sampleAt).TotalSeconds;
            if (dt < 1) return;
            var rate = (bytes - _sampleBytes) / dt;
            _speed = _speed is { } s ? 0.7 * s + 0.3 * rate : rate;
            _sampleAt = clock.GetTimestamp();
            _sampleBytes = bytes;
        }

        public DownloadStatus Status()
        {
            if (Plan is not { } plan) return new(DownloadKind.Queued);
            if (Waiting) return new(DownloadKind.WaitingForUnmetered, Fraction(plan));
            var received = Received.Values.Sum();
            if (received == 0 && plan.Parts.All(p => !p.Done)) return new(DownloadKind.Queued);
            var fraction = Fraction(plan);
            var total = plan.Parts.Sum(p => p.Size ?? 0);
            double? left = _speed is > 0 && total > 0 ? Math.Max(total - received, 0) / _speed : null;
            return new(DownloadKind.Downloading, fraction, SecondsLeft: left);
        }

        /// <summary>By bytes when every file's size is known, otherwise by listening time.</summary>
        private double Fraction(Plan plan)
        {
            if (plan.Parts.All(p => p.Size is > 0))
            {
                var total = plan.Parts.Sum(p => p.Size!.Value);
                var done = plan.Parts.Sum(p => p.Done ? p.Size!.Value : Math.Min(Received.GetValueOrDefault(p.FileName), p.Size!.Value));
                return Math.Clamp((double)done / total, 0, 1);
            }
            var seconds = Math.Max(plan.Parts.Sum(p => p.Duration), 1);
            var heard = plan.Parts.Sum(p => p.Done ? p.Duration
                : p.Size is > 0 ? p.Duration * Math.Min(1, (double)Received.GetValueOrDefault(p.FileName) / p.Size.Value) : 0);
            return Math.Clamp(heard / seconds, 0, 1);
        }
    }
}
