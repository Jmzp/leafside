using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using PdfReader.Core.Updates;

namespace PdfReader.App.Services;

/// <summary>
/// Checks GitHub for a newer release and downloads it. This is the app's only network access: one request to
/// GitHub's public API (no identifiers, no cookies) a day at most, and the download when the user asks for it.
/// </summary>
public static class UpdateService
{
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    // After CurrentVersion: static initializers run in order, and the client's User-Agent uses it.
    private static readonly HttpClient Http = CreateClient();

    public static string RuntimeId => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";

    /// <summary>
    /// True when this copy was installed with the MSI (which records its folder), so updates use the installer;
    /// a portable copy (the zip) gets the zip.
    /// </summary>
    public static bool IsInstalled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\PdfReader");
                if (key?.GetValue("InstallFolder") is not string folder) return false;
                return string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), AppContext.BaseDirectory.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>The latest release if it is newer than this version, otherwise null. Throws on network errors.</summary>
    public static async Task<UpdateRelease?> CheckAsync(CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateCheck.LatestReleaseApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, cancellation);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellation);
        var release = UpdateCheck.Parse(json);
        return release is not null && UpdateCheck.IsNewer(release, CurrentVersion) ? release : null;
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="folder"/> and checks its size and, when GitHub
    /// publishes one, its SHA-256 (<see cref="InvalidDataException"/> when they do not match). A partial or
    /// mismatching file is deleted. Returns the file's path.
    /// </summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, string folder, IProgress<double> progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, Path.GetFileName(asset.Name));
        string partial = target + ".partial";
        try
        {
            using (var response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancellation))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? asset.Size;
                await using var input = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                    sha.AppendData(buffer, 0, read);
                    done += read;
                    if (total > 0) progress.Report(Math.Min(1, (double)done / total));
                }
                if (asset.Size > 0 && done != asset.Size)
                    throw new InvalidDataException("The download is incomplete.");
                if (asset.Sha256 is { } expected && Convert.ToHexStringLower(sha.GetHashAndReset()) != expected)
                    throw new InvalidDataException("The downloaded file's checksum does not match.");
            }
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>Where downloads go: the installer to a temporary folder, the zip to the user's Downloads.</summary>
    public static string DownloadFolder(bool installer) => installer
        ? Path.Combine(Path.GetTempPath(), "LeafSide-update")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Starts the installer; it upgrades this installation in place (the app must exit right after).</summary>
    public static void RunInstaller(string msiPath) =>
        Process.Start(new ProcessStartInfo("msiexec.exe", $"/i \"{msiPath}\"") { UseShellExecute = false });

    /// <summary>Opens File Explorer with the file selected.</summary>
    public static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API requires a User-Agent; it carries nothing but the app's name and version.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LeafSide/{CurrentVersion.ToString(3)}");
        return client;
    }
}
