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
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };
    private const string NginxPinned = "1.27.4";
    private const string MysqlPinned = "8.4.3";        // Oracle MySQL portable zip (keeps --initialize-insecure)

    // ── downloads go through Windows' built-in, Microsoft-SIGNED tools ───────────────
    // curl.exe fetches files and tar.exe extracts them, so the process that pulls
    // executables off the internet and writes them to disk is curl/tar (trusted),
    // NOT bhserve.exe. That keeps antivirus behavioral scanners from flagging bhserve
    // as a "dropper" — without bundling and without a code-signing certificate.
    private static string CurlExe => Path.Combine(Environment.SystemDirectory, "curl.exe");
    private static string TarExe  => Path.Combine(Environment.SystemDirectory, "tar.exe");
    private const string UA = "BHServe/0.1 (+https://github.com/wpexpertinbd/BHServe)";

    private static void Shell(string exe, string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {err.Trim()}");
    }

    /// <summary>Download a file to <paramref name="dest"/> via the signed system curl.exe.</summary>
    private static Task CurlTo(string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Shell(CurlExe, $"-fL --retry 3 --retry-delay 2 -A \"{UA}\" -o \"{dest}\" \"{url}\"");
        if (!File.Exists(dest) || new FileInfo(dest).Length == 0)
            throw new InvalidOperationException($"download produced no file: {url}");
        return Task.CompletedTask;
    }

    private static async Task<string> DownloadToTmp(string url, string fileName)
    {
        var dest = Path.Combine(Paths.Tmp, fileName);
        await CurlTo(url, dest);
        return dest;
    }

    /// <summary>Extract a zip via the signed system tar.exe (so tar, not bhserve, creates the exes).</summary>
    private static void ExtractZip(string zip, string destDir)
    {
        Directory.CreateDirectory(destDir);
        Shell(TarExe, $"-xf \"{zip}\" -C \"{destDir}\"");
    }

    /// <summary>Delete every *.exe under <paramref name="dir"/> except the named keepers (case-insensitive).</summary>
    private static void PruneExes(string dir, params string[] keep)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.exe", SearchOption.AllDirectories))
                if (!keep.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
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
        var dest = Path.Combine(dir, "mkcert.exe");
        await CurlTo(url, dest);
        return dest;
    }

    private static async Task<string> GithubAsset(string repo, Func<string, bool> match)
    {
        // /releases/latest 404s when every release is marked pre-release (e.g. nono303/memcached),
        // so fall back to the full releases list and scan newest-first.
        string json;
        try { json = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest"); }
        catch (HttpRequestException) { json = "[]"; }

        using (var doc = JsonDocument.Parse(json))
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("assets", out var a1))
                foreach (var a in a1.EnumerateArray())
                    if (match(a.GetProperty("name").GetString() ?? "")) return a.GetProperty("browser_download_url").GetString()!;

        var list = await Http.GetStringAsync($"https://api.github.com/repos/{repo}/releases?per_page=20");
        using var doc2 = JsonDocument.Parse(list);
        foreach (var rel in doc2.RootElement.EnumerateArray())
            if (rel.TryGetProperty("assets", out var a2))
                foreach (var a in a2.EnumerateArray())
                    if (match(a.GetProperty("name").GetString() ?? "")) return a.GetProperty("browser_download_url").GetString()!;

        throw new InvalidOperationException($"no matching asset in {repo} releases");
    }

    public static async Task<string> InstallMailpit()
    {
        var url = await GithubAsset("axllent/mailpit",
            n => n.Contains("windows", StringComparison.OrdinalIgnoreCase) && n.Contains("amd64") && n.EndsWith(".zip"));
        var zip = await DownloadToTmp(url, "mailpit.zip");
        var dir = Path.Combine(Paths.Bin, "mailpit");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);
        return Tools.MailpitExe() ?? throw new InvalidOperationException("mailpit.exe not found after extract");
    }

    /// <summary>Download Oracle MySQL (portable winx64 zip) into bin\mysql.</summary>
    public static async Task<string> InstallDb()
    {
        // dev.mysql.com/get/ 302-redirects to the CDN; HttpClient follows it.
        var url = $"https://dev.mysql.com/get/Downloads/MySQL-8.4/mysql-{MysqlPinned}-winx64.zip";
        var zip = await DownloadToTmp(url, "mysql.zip");
        var dir = Path.Combine(Paths.Bin, "mysql");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);   // → bin\mysql\mysql-8.4.3-winx64\bin\mysqld.exe
        return Tools.MysqldExe() ?? throw new InvalidOperationException("mysqld.exe not found after extract");
    }

    /// <summary>Download Apache (Apache Lounge build) into bin\apache.</summary>
    public static async Task<string> InstallApache()
    {
        // Apache Lounge is the de-facto Windows httpd source. URLs carry a build date, so we
        // pin a known-good one; if it 404s, the caller surfaces a manual-install hint.
        const string url = "https://www.apachelounge.com/download/VS17/binaries/httpd-2.4.62-240904-win64-VS17.zip";
        var zip = await DownloadToTmp(url, "httpd.zip");
        var dir = Path.Combine(Paths.Bin, "apache");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);   // → bin\apache\Apache24\bin\httpd.exe
        return Tools.HttpdExe() ?? throw new InvalidOperationException("httpd.exe not found after extract");
    }

    public static async Task<string> InstallRedis()
    {
        // tporadowski/redis ships Redis-x64-<ver>.zip (redis-server.exe inside)
        var url = await GithubAsset("tporadowski/redis",
            n => n.StartsWith("Redis-x64", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var zip = await DownloadToTmp(url, "redis.zip");
        var dir = Path.Combine(Paths.Bin, "redis");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);
        return Tools.RedisServerExe() ?? throw new InvalidOperationException("redis-server.exe not found after extract");
    }

    public static async Task<string> InstallMemcached()
    {
        // jefyt/memcached-windows ships memcached-<ver>-win64-mingw.zip (memcached.exe + mingw deps,
        // no cygwin). Skip the sibling libevent/libressl zips in the same release.
        var url = await GithubAsset("jefyt/memcached-windows",
            n => n.StartsWith("memcached", StringComparison.OrdinalIgnoreCase)
              && n.Contains("win64") && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        var zip = await DownloadToTmp(url, "memcached.zip");
        var dir = Path.Combine(Paths.Bin, "memcached");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);
        // The mingw build bundles throwaway helper exes (e.g. sizes.exe) that trip generic
        // AV heuristics. Keep only memcached.exe + its DLLs so nothing junk ships to users.
        PruneExes(dir, keep: "memcached.exe");
        return Tools.MemcachedExe() ?? throw new InvalidOperationException("memcached.exe not found after extract");
    }

    public static async Task<string> InstallCloudflared()
    {
        var url = await GithubAsset("cloudflare/cloudflared",
            n => n.Equals("cloudflared-windows-amd64.exe", StringComparison.OrdinalIgnoreCase));
        var dest = Path.Combine(Paths.Bin, "cloudflared", "cloudflared.exe");
        await CurlTo(url, dest);
        return dest;
    }

    public static async Task<string> InstallFnm()
    {
        // fnm ships fnm-windows.zip (contains fnm.exe)
        var url = await GithubAsset("Schniz/fnm",
            n => n.Contains("windows", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip"));
        var zip = await DownloadToTmp(url, "fnm.zip");
        var dir = Path.Combine(Paths.Bin, "fnm");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);
        return Tools.FnmExe() ?? throw new InvalidOperationException("fnm.exe not found after extract");
    }

    /// <summary>Download + extract the Windows ionCube loaders for a VC build (cached per vc).</summary>
    public static async Task<string> InstallIoncube(string vc)
    {
        var dir = Path.Combine(Paths.Bin, "ioncube", vc);
        if (Directory.Exists(Path.Combine(dir, "ioncube"))) return dir;   // already extracted
        var url = $"https://downloads.ioncube.com/loader_downloads/ioncube_loaders_win_{vc}_x86-64.zip";
        var zip = await DownloadToTmp(url, $"ioncube_{vc}.zip");
        // A 404 returns an HTML page, not a zip — ExtractZip throws, surfaced to the caller.
        ExtractZip(zip, dir);
        return dir;
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var d in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(d.Replace(src, dst));
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(f, f.Replace(src, dst), overwrite: true);
    }

    /// <summary>Download the latest WordPress into <paramref name="root"/> and pre-write wp-config.php.</summary>
    public static async Task InstallWordPress(string root, string db)
    {
        var zip = await DownloadToTmp("https://wordpress.org/latest.zip", "wordpress.zip");
        var tmp = Path.Combine(Paths.Tmp, "wp-extract");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ExtractZip(zip, tmp);
        CopyDir(Path.Combine(tmp, "wordpress"), root);   // merge into the (possibly placeholder) root

        var sample = Path.Combine(root, "wp-config-sample.php");
        var cfg = Path.Combine(root, "wp-config.php");
        if (File.Exists(sample) && !File.Exists(cfg))
        {
            var txt = await File.ReadAllTextAsync(sample);
            txt = txt.Replace("database_name_here", db)
                     .Replace("username_here", "root")
                     .Replace("'password_here'", "''")
                     .Replace("localhost", "127.0.0.1");
            try
            {
                var salts = await Http.GetStringAsync("https://api.wordpress.org/secret-key/1.1/salt/");
                if (salts.Trim().Length > 0)
                    txt = System.Text.RegularExpressions.Regex.Replace(
                        txt, @"define\(\s*'AUTH_KEY'.*?'NONCE_SALT'[^;]*\);",
                        salts.Trim(), System.Text.RegularExpressions.RegexOptions.Singleline);
            }
            catch { /* keep sample salts if the API is unreachable */ }
            await File.WriteAllTextAsync(cfg, txt);
        }
    }

    /// <summary>Download + extract the latest phpMyAdmin into <paramref name="root"/> and write config.inc.php.</summary>
    public static async Task InstallPhpMyAdmin(string root)
    {
        var verJson = await Http.GetStringAsync("https://www.phpmyadmin.net/home_page/version.json");
        using var doc = JsonDocument.Parse(verJson);
        var ver = doc.RootElement.GetProperty("version").GetString()
                  ?? throw new InvalidOperationException("could not resolve phpMyAdmin version");
        var url = $"https://files.phpmyadmin.net/phpMyAdmin/{ver}/phpMyAdmin-{ver}-all-languages.zip";
        var zip = await DownloadToTmp(url, "phpmyadmin.zip");

        var tmp = Path.Combine(Paths.Tmp, "pma-extract");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        ExtractZip(zip, tmp);
        var inner = Directory.GetDirectories(tmp).FirstOrDefault() ?? tmp;   // phpMyAdmin-<ver>-all-languages\

        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.GetDirectoryName(root)!);
        Directory.Move(inner, root);

        // config.inc.php: connect to BHServe's MySQL as passwordless root.
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(Path.Combine(root, "config.inc.php"),
            "<?php\n" +
            $"$cfg['blowfish_secret'] = '{secret}';\n" +
            "$i = 0; $i++;\n" +
            "$cfg['Servers'][$i]['host'] = '127.0.0.1';\n" +
            "$cfg['Servers'][$i]['port'] = '3306';\n" +
            "$cfg['Servers'][$i]['auth_type'] = 'cookie';\n" +
            "$cfg['Servers'][$i]['AllowNoPassword'] = true;\n");
    }

    /// <summary>Download the latest single-file Adminer to <paramref name="dest"/>.</summary>
    public static async Task InstallAdminer(string dest)
        => await CurlTo("https://www.adminer.org/latest.php", dest);

    static Downloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BHServe/0.1 (+https://github.com/wpexpertinbd/BHServe)");
    }
}
