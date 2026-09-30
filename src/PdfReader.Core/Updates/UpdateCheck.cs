using System.Text.Json;

namespace PdfReader.Core.Updates;

/// <summary>A downloadable file of a release.</summary>
/// <param name="Sha256">Lowercase hex digest published by GitHub, when it provides one.</param>
public sealed record UpdateAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>A published release of the app on GitHub.</summary>
public sealed record UpdateRelease(Version Version, string Tag, string PageUrl, IReadOnlyList<UpdateAsset> Assets);

/// <summary>
/// The logic of the update check, kept free of I/O: reading GitHub's "latest release" response, comparing
/// versions, picking the file for this machine and deciding when the daily check is due.
/// </summary>
public static class UpdateCheck
{
    public const string Repository = "Jmzp/winui-pdf-reader";
    public const string LatestReleaseApi = $"https://api.github.com/repos/{Repository}/releases/latest";
    public const string ReleasesPage = $"https://github.com/{Repository}/releases/latest";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>Only files from GitHub's own download hosts are accepted, whatever the response says.</summary>
    private static readonly string[] TrustedHosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Parses a "latest release" response; null for drafts, pre-releases or anything unexpected.</summary>
    public static UpdateRelease? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft") || Bool(root, "prerelease")) return null;
            string tag = Str(root, "tag_name") ?? "";
            if (!TryParseVersion(tag, out var version)) return null;

            var assets = new List<UpdateAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    string? name = Str(a, "name"), url = Str(a, "browser_download_url");
                    if (name is null || url is null || !IsTrusted(url)) continue;
                    long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                    string? digest = Str(a, "digest");
                    string? sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? digest[7..].ToLowerInvariant() : null;
                    assets.Add(new UpdateAsset(name, url, size, sha));
                }
            }
            string page = Str(root, "html_url") is { } html && IsTrusted(html) ? html : ReleasesPage;
            return new UpdateRelease(version, tag, page, assets);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"v1.2.0", "1.2" or "1.2.0.4" (tags may carry a leading v).</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        string text = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(text, out var parsed) && parsed.Major >= 0)
        {
            // Normalize missing parts to 0 so 1.2 == 1.2.0.
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }
        version = new Version(0, 0);
        return false;
    }

    public static bool IsNewer(UpdateRelease release, Version current)
    {
        TryParseVersion(current.ToString(), out var normalized);
        return release.Version > normalized;
    }

    /// <summary>
    /// The file for this machine: the installer when the app was installed with it, otherwise the portable zip
    /// (<paramref name="runtimeId"/> is "win-x64" or "win-arm64").
    /// </summary>
    public static UpdateAsset? PickAsset(UpdateRelease release, string runtimeId, bool installed)
    {
        string extension = installed ? ".msi" : ".zip";
        return release.Assets.FirstOrDefault(a =>
            a.Name.EndsWith($"-{runtimeId}{extension}", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether the automatic check should run now (a clock set back also counts as due).</summary>
    public static bool IsDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || now - last >= Interval || last > now;

    private static bool IsTrusted(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
