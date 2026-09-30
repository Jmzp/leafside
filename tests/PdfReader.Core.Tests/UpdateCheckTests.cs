using PdfReader.Core.State;
using PdfReader.Core.Updates;
using Xunit;

namespace PdfReader.Core.Tests;

public class UpdateCheckTests
{
    private const string Sha = "3b1f0e7a55c1e0f5d4c3b2a1908f7e6d5c4b3a291807f6e5d4c3b2a1908f7e6d";

    private static string Release(string tag = "v1.2.0", bool draft = false, bool prerelease = false, string host = "github.com") => $$"""
        {
          "tag_name": "{{tag}}", "draft": {{(draft ? "true" : "false")}}, "prerelease": {{(prerelease ? "true" : "false")}},
          "html_url": "https://github.com/Jmzp/leafside/releases/tag/{{tag}}",
          "body": "Notes",
          "assets": [
            { "name": "PdfReader-1.2.0-win-x64.msi", "size": 1000,
              "browser_download_url": "https://{{host}}/Jmzp/leafside/releases/download/{{tag}}/PdfReader-1.2.0-win-x64.msi",
              "digest": "sha256:{{Sha.ToUpperInvariant()}}" },
            { "name": "PdfReader-1.2.0-win-x64.zip", "size": 2000,
              "browser_download_url": "https://{{host}}/Jmzp/leafside/releases/download/{{tag}}/PdfReader-1.2.0-win-x64.zip" },
            { "name": "PdfReader-1.2.0-win-arm64.msi", "size": 3000,
              "browser_download_url": "https://{{host}}/Jmzp/leafside/releases/download/{{tag}}/PdfReader-1.2.0-win-arm64.msi" },
            { "name": "PdfReader-1.2.0-win-arm64.zip", "size": 4000,
              "browser_download_url": "https://{{host}}/Jmzp/leafside/releases/download/{{tag}}/PdfReader-1.2.0-win-arm64.zip" }
          ]
        }
        """;

    [Fact]
    public void Parses_the_latest_release()
    {
        var release = UpdateCheck.Parse(Release())!;
        Assert.Equal(new Version(1, 2, 0, 0), release.Version);
        Assert.Equal("v1.2.0", release.Tag);
        Assert.Equal("https://github.com/Jmzp/leafside/releases/tag/v1.2.0", release.PageUrl);
        Assert.Equal(4, release.Assets.Count);
        Assert.Equal(Sha, release.Assets[0].Sha256);
        Assert.Null(release.Assets[1].Sha256);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Ignores_drafts_and_prereleases(bool draft, bool prerelease) =>
        Assert.Null(UpdateCheck.Parse(Release(draft: draft, prerelease: prerelease)));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "message": "API rate limit exceeded" }""")]
    public void Unexpected_responses_are_not_updates(string json) => Assert.Null(UpdateCheck.Parse(json));

    [Fact]
    public void Files_from_other_hosts_are_ignored()
    {
        var release = UpdateCheck.Parse(Release(host: "example.com"))!;
        Assert.Empty(release.Assets);
    }

    [Theory]
    [InlineData("v1.2.0", "1.1.0", true)]
    [InlineData("1.1.1", "1.1.0", true)]
    [InlineData("v1.1", "1.1.0", false)]
    [InlineData("v1.1.0", "1.1.0.0", false)]
    [InlineData("v1.0.9", "1.1.0", false)]
    [InlineData("v2.0.0", "1.9.9", true)]
    public void Compares_versions(string tag, string current, bool newer)
    {
        var release = UpdateCheck.Parse(Release(tag))!;
        Assert.Equal(newer, UpdateCheck.IsNewer(release, Version.Parse(current)));
    }

    [Theory]
    [InlineData("win-x64", true, "PdfReader-1.2.0-win-x64.msi")]
    [InlineData("win-x64", false, "PdfReader-1.2.0-win-x64.zip")]
    [InlineData("win-arm64", true, "PdfReader-1.2.0-win-arm64.msi")]
    [InlineData("win-arm64", false, "PdfReader-1.2.0-win-arm64.zip")]
    public void Picks_the_file_for_this_machine(string rid, bool installed, string expected) =>
        Assert.Equal(expected, UpdateCheck.PickAsset(UpdateCheck.Parse(Release())!, rid, installed)?.Name);

    [Fact]
    public void The_check_runs_once_a_day()
    {
        var now = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);
        Assert.True(UpdateCheck.IsDue(null, now));
        Assert.False(UpdateCheck.IsDue(now.AddHours(-23), now));
        Assert.True(UpdateCheck.IsDue(now.AddHours(-24), now));
        Assert.True(UpdateCheck.IsDue(now.AddDays(3), now)); // clock moved back
    }

    [Fact]
    public void Update_settings_persist()
    {
        string path = Path.Combine(Path.GetTempPath(), $"state-{Guid.NewGuid():N}.json");
        try
        {
            var when = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.FromHours(-5));
            var store = new AppStateStore(path);
            Assert.True(store.Settings.CheckForUpdates);
            store.Settings.CheckForUpdates = false;
            store.Settings.LastUpdateCheck = when;
            store.Settings.SkippedVersion = "1.2.0";
            store.Save();

            var reloaded = new AppStateStore(path);
            Assert.False(reloaded.Settings.CheckForUpdates);
            Assert.Equal(when, reloaded.Settings.LastUpdateCheck);
            Assert.Equal("1.2.0", reloaded.Settings.SkippedVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
