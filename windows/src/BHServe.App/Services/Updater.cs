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
            var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new Result(false, CurrentVersion, null, null, "No published releases yet.");
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var tag = (doc.RootElement.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
            string? asset = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
                asset = assets.EnumerateArray()
                    .Select(a => a.GetProperty("browser_download_url").GetString())
                    .FirstOrDefault(u => u is not null && u.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var notes = doc.RootElement.TryGetProperty("body", out var b) ? b.GetString() : null;

            var newer = Compare(tag, CurrentVersion) > 0;
            return new Result(newer, tag, asset, notes, null);
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
        Process.Start(new ProcessStartInfo { FileName = dest, UseShellExecute = true });
    }
}
