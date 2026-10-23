using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace AbsPlayer.Core.Tests;

public class UpdateCheckerTests
{
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("pretend installer");
    private static readonly string InstallerSha = Convert.ToHexString(SHA256.HashData(InstallerBytes)).ToLowerInvariant();

    private static string Release(string tag, bool draft = false, string? digest = null, string asset = "Homeshelf-x-Setup.exe") =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/r/releases/tag/" + tag + "\",\"draft\":" + (draft ? "true" : "false")
        + ",\"assets\":[{\"name\":\"" + asset + "\",\"browser_download_url\":\"https://github.com/dl/" + tag + "/" + asset + "\""
        + (digest is null ? "" : ",\"digest\":\"" + digest + "\"") + "}]}";

    private static (TestEnv Env, UpdateChecker Checker, FakeTimeProvider Clock) Setup(string current, params string[] releases)
    {
        var env = new TestEnv();
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        env.Handler.Respond = req => Task.FromResult(req.RequestUri!.Host == "api.github.com"
            ? StubHandler.Json("[" + string.Join(",", releases) + "]")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(InstallerBytes) });
        return (env, new UpdateChecker(env.Http, env.Prefs.Settings, current, clock), clock);
    }

    [Theory]
    [InlineData("v0.2.1", "0.2.1")]
    [InlineData("0.2", "0.2.0")]
    [InlineData("0.1.1+918d458", "0.1.1")]
    [InlineData("v1.0.0-beta", "1.0.0")]
    public void Versions_parse(string input, string expected) => Assert.Equal(Version.Parse(expected), UpdateChecker.ParseVersion(input));

    [Fact]
    public void Junk_versions_are_null() => Assert.Null(UpdateChecker.ParseVersion("latest"));

    [Fact]
    public async Task Finds_the_newest_release_ignoring_drafts()
    {
        var (env, checker, _) = Setup("0.1.1+abc", Release("v0.1.0"), Release("v0.3.0", draft: true), Release("v0.2.0", digest: "sha256:ABC"));
        using var _e = env;
        var u = await checker.Check();
        Assert.Equal(new Version(0, 2, 0), u!.Version);
        Assert.Equal("v0.2.0", u.Tag);
        Assert.Equal("ABC", u.Sha256);
        Assert.Equal("https://github.com/dl/v0.2.0/Homeshelf-x-Setup.exe", u.InstallerUrl.AbsoluteUri);
        Assert.Contains("/repos/druboni/abs-player-windows/releases", env.Handler.Requests[0].Request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Same_or_older_is_no_update()
    {
        var (env, checker, _) = Setup("0.2.0", Release("v0.2.0"), Release("v0.1.9"));
        using var _e = env;
        Assert.Null(await checker.Check());
    }

    [Fact]
    public async Task Release_without_an_installer_is_ignored()
    {
        var (env, checker, _) = Setup("0.1.0", Release("v0.2.0", asset: "notes.txt"));
        using var _e = env;
        Assert.Null(await checker.Check());
    }

    [Fact]
    public async Task Automatic_check_runs_once_a_day_and_respects_the_switch()
    {
        var (env, checker, clock) = Setup("0.1.0", Release("v0.2.0"));
        using var _e = env;
        Assert.NotNull(await checker.CheckIfDue());
        Assert.Null(await checker.CheckIfDue()); // Checked moments ago.
        clock.Advance(TimeSpan.FromHours(25));
        Assert.NotNull(await checker.CheckIfDue());
        Assert.Equal(2, env.Handler.Requests.Count);

        clock.Advance(TimeSpan.FromDays(2));
        checker.Enabled = false;
        Assert.Null(await checker.CheckIfDue());
        Assert.Equal(2, env.Handler.Requests.Count);
    }

    [Fact]
    public async Task Automatic_check_swallows_network_errors()
    {
        var (env, checker, _) = Setup("0.1.0");
        using var _e = env;
        env.Handler.Respond = _ => throw new HttpRequestException("offline");
        Assert.Null(await checker.CheckIfDue());
        await Assert.ThrowsAsync<HttpRequestException>(() => checker.Check());
    }

    [Fact]
    public async Task Download_checks_the_published_checksum()
    {
        var (env, checker, _) = Setup("0.1.0", Release("v0.2.0", digest: "sha256:" + InstallerSha));
        using var _e = env;
        var update = (await checker.Check())!;
        var reports = new List<double>();
        var path = await checker.Download(update, new Progress<double>(reports.Add));
        Assert.Equal(InstallerBytes, await File.ReadAllBytesAsync(path));
        File.Delete(path);

        var tampered = update with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<InvalidDataException>(() => checker.Download(tampered));
        Assert.False(File.Exists(path));
    }
}
