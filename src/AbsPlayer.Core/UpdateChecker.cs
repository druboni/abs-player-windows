using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AbsPlayer.Core.Storage;

namespace AbsPlayer.Core;

/// <summary>A newer release on GitHub, with its installer.</summary>
public sealed record AvailableUpdate(Version Version, string Tag, Uri ReleasePage, Uri InstallerUrl, string InstallerName, string? Sha256);

/// <summary>
/// Asks GitHub's public API for the newest release of this app. No account or personal data is sent.
/// Checks at most once a day on its own, and only when the setting is on.
/// </summary>
public sealed class UpdateChecker(HttpClient http, SettingsStore settings, string currentVersion, TimeProvider? clock = null)
{
    public const string Repository = "druboni/abs-player-windows";
    private const string EnabledKey = "check_for_updates";
    private const string LastCheckKey = "last_update_check";
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public bool Enabled
    {
        get => settings.Get<bool?>(EnabledKey) ?? true;
        set => settings.Set(EnabledKey, value);
    }

    /// <summary>The automatic check: does nothing when switched off or already checked in the last day.</summary>
    public async Task<AvailableUpdate?> CheckIfDue()
    {
        if (!Enabled) return null;
        if (settings.Get<DateTimeOffset?>(LastCheckKey) is { } last && _clock.GetUtcNow() - last < Interval) return null;
        try { return await Check(); }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException) { return null; }
    }

    /// <summary>Checks now. Throws when GitHub can't be reached.</summary>
    public async Task<AvailableUpdate?> Check()
    {
        var releases = await http.GetFromJsonAsync<List<Release>>($"https://api.github.com/repos/{Repository}/releases?per_page=10") ?? [];
        settings.Set(LastCheckKey, _clock.GetUtcNow());
        var current = ParseVersion(currentVersion);
        var newest = releases
            .Where(r => !r.Draft)
            .Select(r => (Release: r, Version: ParseVersion(r.TagName)))
            .Where(x => x.Version is not null)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault();
        if (newest.Release is null || current is null || newest.Version! <= current) return null;
        var installer = newest.Release.Assets.FirstOrDefault(a => a.Name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase));
        if (installer is null) return null;
        var sha = installer.Digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
        return new AvailableUpdate(newest.Version!, newest.Release.TagName, new Uri(newest.Release.HtmlUrl),
            new Uri(installer.BrowserDownloadUrl), installer.Name, sha);
    }

    /// <summary>"v0.2.1", "0.2.1" or "0.2.1+abc123" → 0.2.1. Anything else → null.</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var core = s.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        return Version.TryParse(core, out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>
    /// Downloads the installer to a temp folder and checks it against GitHub's SHA-256 when one is published.
    /// Returns the file path.
    /// </summary>
    public async Task<string> Download(AvailableUpdate update, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ABSPlayerUpdate");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Path.GetFileName(update.InstallerName));
        using (var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }
        if (update.Sha256 is { } expected)
        {
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                stream.Close();
                File.Delete(path);
                throw new InvalidDataException("The downloaded installer didn't match the checksum published on GitHub.");
            }
        }
        return path;
    }

    private sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] List<Asset> Assets);

    private sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("digest")] string? Digest);
}
