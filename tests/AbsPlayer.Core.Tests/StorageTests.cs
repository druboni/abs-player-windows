using System.Net;
using System.Text.Json;
using AbsPlayer.Core.Storage;
using Microsoft.Extensions.Time.Testing;

namespace AbsPlayer.Core.Tests;

public class StorageTests
{
    [Fact]
    public void Settings_survive_a_restart_and_tolerate_a_corrupt_file()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Dir, "settings.json");
        _ = new Prefs(env.Prefs.Settings) { SkipBack = 15, LibrarySort = LibrarySort.Author, HideFinished = true };
        var deviceId = env.Prefs.DeviceId;

        var reloaded = new Prefs(new SettingsStore(path));
        Assert.Equal(15, reloaded.SkipBack);
        Assert.Equal(Prefs.DefaultSkip, reloaded.SkipForward);
        Assert.Equal(LibrarySort.Author, reloaded.LibrarySort);
        Assert.True(reloaded.HideFinished);
        Assert.Equal(deviceId, new UserPreferences(new SettingsStore(path), env.Secrets).DeviceId);

        File.WriteAllText(path, "{not json");
        Assert.Equal(Prefs.DefaultSkip, new Prefs(new SettingsStore(path)).SkipBack);
    }

    [Fact]
    public void Tokens_never_land_in_the_settings_file()
    {
        using var env = new TestEnv();
        env.Prefs.SaveCredentials("http://abs.test", "secret-token", "secret-refresh", "u1", "brian");
        var text = File.ReadAllText(Path.Combine(env.Dir, "settings.json"));
        Assert.DoesNotContain("secret", text);
        Assert.Contains("brian", text);
    }

    [Fact]
    public void Credential_locker_round_trip()
    {
        var store = new CredentialLockerStore("ABS Player tests " + Guid.NewGuid().ToString("N"));
        Assert.Null(store.Get("auth_token"));
        store.Set("auth_token", "one");
        store.Set("auth_token", "two");
        Assert.Equal("two", store.Get("auth_token"));
        store.Set("auth_token", null);
        Assert.Null(store.Get("auth_token"));
    }

    [Fact]
    public void Local_progress_keeps_pending_flag_until_synced()
    {
        using var env = new TestEnv();
        var path = Path.Combine(env.Dir, "progress.json");
        var store = new LocalProgressStore(path);
        store.Save("a", 10, 100, needsSync: true);
        store.Save("a", 20, 100, needsSync: false); // A later successful-looking save mustn't clear it.

        var reloaded = new LocalProgressStore(path);
        Assert.Equal(20, reloaded.Get("a")!.CurrentTime);
        Assert.True(reloaded.Get("a")!.NeedsSync);

        reloaded.MarkSynced("a");
        Assert.False(new LocalProgressStore(path).Get("a")!.NeedsSync);
    }

    [Fact]
    public async Task Flush_sends_offline_progress_unless_server_is_newer()
    {
        using var env = new TestEnv();
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
        var store = new LocalProgressStore(Path.Combine(env.Dir, "progress.json"), clock);
        store.Save("older-on-server", 50, 100, needsSync: true);
        store.Save("newer-on-server", 60, 100, needsSync: true);
        store.Save("fails", 70, 100, needsSync: true);
        store.Save("done", 80, 100, needsSync: false);

        env.Handler.Respond = req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get)
            {
                var updated = path.EndsWith("newer-on-server") ? 2_000_000 : 500_000;
                return Task.FromResult(StubHandler.Json($$"""{"libraryItemId":"x","currentTime":1,"duration":100,"progress":0.01,"isFinished":false,"lastUpdate":{{updated}}}"""));
            }
            return Task.FromResult(path.EndsWith("fails")
                ? StubHandler.Status(HttpStatusCode.BadGateway)
                : StubHandler.Json("{}"));
        };

        await store.Flush(env.Repo);

        var patched = env.Handler.Requests.Where(r => r.Request.Method == HttpMethod.Patch)
            .Select(r => r.Request.RequestUri!.AbsolutePath.Split('/').Last()).Order().ToList();
        Assert.Equal(["fails", "older-on-server"], patched);
        Assert.False(store.Get("older-on-server")!.NeedsSync);
        Assert.False(store.Get("newer-on-server")!.NeedsSync); // Skipped, server wins.
        Assert.True(store.Get("fails")!.NeedsSync);            // Retried next time.
        var sent = env.Handler.Requests.First(r => r.Request.RequestUri!.AbsolutePath.EndsWith("older-on-server") && r.Request.Method == HttpMethod.Patch);
        Assert.Equal(50, JsonDocument.Parse(sent.Body!).RootElement.GetProperty("currentTime").GetDouble());
    }
}
