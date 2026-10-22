using System.Text.Json;

namespace AbsPlayer.Core;

/// <summary>
/// Position saved on this device, so offline listening resumes correctly and
/// reaches the server once we're back online.
/// </summary>
public sealed record LocalProgress(double CurrentTime, double Duration, DateTimeOffset UpdatedAt, bool NeedsSync);

public sealed class LocalProgressStore(string path, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private Dictionary<string, LocalProgress>? _all;

    private Dictionary<string, LocalProgress> All
    {
        get
        {
            if (_all is not null) return _all;
            try
            {
                if (File.Exists(path))
                    _all = JsonSerializer.Deserialize<Dictionary<string, LocalProgress>>(File.ReadAllText(path));
            }
            catch (Exception e) when (e is JsonException or IOException) { }
            return _all ??= [];
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(All));
        File.Move(tmp, path, overwrite: true);
    }

    public LocalProgress? Get(string id)
    {
        lock (_lock) return All.GetValueOrDefault(id);
    }

    public void Save(string id, double currentTime, double duration, bool needsSync)
    {
        lock (_lock)
        {
            var pending = needsSync || (All.GetValueOrDefault(id)?.NeedsSync ?? false);
            All[id] = new LocalProgress(currentTime, duration, _clock.GetUtcNow(), pending);
            Save();
        }
    }

    public void MarkSynced(string id)
    {
        lock (_lock)
        {
            if (All.GetValueOrDefault(id) is not { } p) return;
            All[id] = p with { NeedsSync = false };
            Save();
        }
    }

    /// <summary>Sends positions saved while offline, unless the server already has something newer.</summary>
    public async Task Flush(AudiobookRepository repo)
    {
        if (!repo.Preferences.IsLoggedIn) return;
        List<KeyValuePair<string, LocalProgress>> pending;
        lock (_lock) pending = All.Where(kv => kv.Value.NeedsSync).ToList();

        foreach (var (id, local) in pending)
        {
            Models.MediaProgressResponse? server = null;
            try { server = await repo.GetProgress(id); }
            catch (Api.ApiException) { }
            if (server?.LastUpdate is { } ms && DateTimeOffset.FromUnixTimeMilliseconds((long)ms) > local.UpdatedAt)
            {
                MarkSynced(id);
                continue;
            }
            try
            {
                await repo.UpdateProgress(id, local.CurrentTime, local.Duration);
                MarkSynced(id);
            }
            catch (Api.ApiException) { }
        }
    }
}
