using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace BHServe.App.Services;

/// <summary>
/// In-app updater — checks the GitHub releases for a newer BHServe-Setup.exe and
/// (on request) downloads + launches it. Mirrors the mac app's .pkg update flow;
/// the release-asset matcher is simply "ends with .exe".
/// </summary>
public static class Updater
{
    private const string Repo = "wpexpertinbd/BHServe";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public sealed record Result(bool UpdateAvailable, string Latest, string? AssetUrl, string? Notes, string? Error);

    public static async Task<Result> Check()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BHServe-Updater");
            // IMPORTANT: read ALL releases and filter the Windows channel (win-v* tags).
            // NOT /releases/latest — that is the macOS .pkg channel and would mis-report
            // the Mac version as a Windows update.
            using var doc = JsonDocument.Parse(
                await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases?per_page=50"));

            string? bestVer = null, bestAsset = null, bestNotes = null;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var tag = rel.GetProperty("tag_name").GetString() ?? "";
                if (!tag.StartsWith("win-v", StringComparison.OrdinalIgnoreCase)) continue;
                var ver = tag["win-v".Length..];
                if (bestVer is not null && Compare(ver, bestVer) <= 0) continue;

                var exe = rel.TryGetProperty("assets", out var assets)
                    ? assets.EnumerateArray()
                            .Select(a => a.GetProperty("browser_download_url").GetString())
                            .FirstOrDefault(u => u is not null && u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    : null;
                bestVer = ver; bestAsset = exe;
                bestNotes = rel.TryGetProperty("body", out var b) ? b.GetString() : null;
            }

            if (bestVer is null) return new Result(false, CurrentVersion, null, null, "No Windows releases published yet.");
            return new Result(Compare(bestVer, CurrentVersion) > 0, bestVer, bestAsset, bestNotes, null);
        }
        catch (Exception ex) { return new Result(false, CurrentVersion, null, null, ex.Message); }
    }

    private static int Compare(string a, string b) =>
        (Version.TryParse(a, out var va) ? va : new Version(0, 0)).CompareTo(
         Version.TryParse(b, out var vb) ? vb : new Version(0, 0));

    /// <summary>Download the installer and launch it (the running app should exit so files can be replaced).</summary>
    public static async Task DownloadAndRun(string url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BHServe-Updater");
        var dest = Path.Combine(Path.GetTempPath(), "BHServe-Setup-update.exe");
        await using (var s = await http.GetStreamAsync(url))
        await using (var f = File.Create(dest))
            await s.CopyToAsync(f);
        // Launch the installer, then fully exit BHServe (incl. the tray) so the running
        // BHServe.App.exe / Core.dll unlock and the installer can replace them without "couldn't
        // close the application". /FORCECLOSEAPPLICATIONS is a fallback for any other instance
        // (a second tray, the CLI) still holding files. Process.Start throws if the UAC prompt is
        // declined — in which case we never reach ForceQuit and the app stays running.
        Process.Start(new ProcessStartInfo
        {
            FileName = dest,
            Arguments = "/FORCECLOSEAPPLICATIONS",
            UseShellExecute = true,
        });
        App.ForceQuit();
    }
}
