using System.IO.Compression;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>
/// Fetches portable Windows builds into <c>bin\&lt;tool&gt;\…</c> — the Windows analog of
/// the mac engine's <c>brew install</c>. No package manager: we pull the official
/// portable zips/exes and extract them ourselves.
/// </summary>
public static class Downloader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private const string NginxPinned = "1.27.4";

    private static async Task<string> DownloadToTmp(string url, string fileName)
    {
        Directory.CreateDirectory(Paths.Tmp);
        var dest = Path.Combine(Paths.Tmp, fileName);
        await using var s = await Http.GetStreamAsync(url);
        await using var f = File.Create(dest);
        await s.CopyToAsync(f);
        return dest;
    }

    private static void ExtractZip(string zip, string destDir)
    {
        Directory.CreateDirectory(destDir);
        ZipFile.ExtractToDirectory(zip, destDir, overwriteFiles: true);
    }

    public static async Task<string> InstallNginx()
    {
        var url = $"https://nginx.org/download/nginx-{NginxPinned}.zip";
        var zip = await DownloadToTmp(url, $"nginx-{NginxPinned}.zip");
        ExtractZip(zip, Path.Combine(Paths.Bin, "nginx"));   // → bin\nginx\nginx-1.27.4\nginx.exe
        return Tools.NginxExe() ?? throw new InvalidOperationException("nginx.exe not found after extract");
    }

    public static async Task<string> InstallPhp(string version)
    {
        // Resolve the current patch + the NTS x64 zip path from the official manifest.
        var json = await Http.GetStringAsync("https://windows.php.net/downloads/releases/releases.json");
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(version, out var branch))
            throw new InvalidOperationException($"PHP {version} not in releases.json (try an active branch)");

        string? relPath = null;
        foreach (var prop in branch.EnumerateObject())
        {
            // keys look like "nts-vs16-x64", "nts-vs17-x64", "ts-vs16-x64", …
            if (prop.Name.StartsWith("nts-") && prop.Name.EndsWith("-x64") &&
                prop.Value.TryGetProperty("zip", out var zipEl) &&
                zipEl.TryGetProperty("path", out var pathEl))
            {
                relPath = pathEl.GetString();
                break;
            }
        }
        if (relPath is null)
            throw new InvalidOperationException($"no NTS x64 build listed for PHP {version}");

        var zip = await DownloadToTmp($"https://windows.php.net/downloads/releases/{relPath}", relPath);
        var dest = Path.Combine(Paths.Bin, "php", version);
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        ExtractZip(zip, dest);
        SeedPhpIni(dest);
        return Tools.PhpCgiExe(version) ?? throw new InvalidOperationException("php-cgi.exe not found after extract");
    }

    /// <summary>If the build ships only php.ini-development, copy it to php.ini (php-cgi needs one).</summary>
    private static void SeedPhpIni(string phpDir)
    {
        var ini = Path.Combine(phpDir, "php.ini");
        if (File.Exists(ini)) return;
        var seed = Path.Combine(phpDir, "php.ini-development");
        if (File.Exists(seed))
        {
            var text = File.ReadAllText(seed);
            // enable the extensions BHServe sites commonly need
            text = text.Replace(";extension_dir = \"ext\"", "extension_dir = \"ext\"");
            foreach (var ext in new[] { "curl", "mbstring", "openssl", "mysqli", "pdo_mysql", "gd", "fileinfo", "zip", "intl", "exif" })
                text = text.Replace($";extension={ext}", $"extension={ext}");
            File.WriteAllText(ini, text);
        }
    }

    public static async Task<string> InstallMkcert()
    {
        // Latest mkcert release asset for windows amd64.
        var rel = await Http.GetStringAsync("https://api.github.com/repos/FiloSottile/mkcert/releases/latest");
        using var doc = JsonDocument.Parse(rel);
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").Contains("windows-amd64"));
        var url = asset.GetProperty("browser_download_url").GetString()
                  ?? throw new InvalidOperationException("mkcert windows asset not found");
        var dir = Path.Combine(Paths.Bin, "mkcert");
        Directory.CreateDirectory(dir);
        await using var s = await Http.GetStreamAsync(url);
        await using var f = File.Create(Path.Combine(dir, "mkcert.exe"));
        await s.CopyToAsync(f);
        return Path.Combine(dir, "mkcert.exe");
    }

    static Downloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BHServe/0.1 (+https://github.com/wpexpertinbd/BHServe)");
    }
}
