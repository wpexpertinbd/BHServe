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
    private const string MysqlPinned = "9.7.1";        // latest Oracle MySQL innovation (keeps --initialize-insecure)

    // ── downloads go through Windows' built-in, Microsoft-SIGNED tools ───────────────
    // curl.exe fetches files and tar.exe extracts them, so the process that pulls
    // executables off the internet and writes them to disk is curl/tar (trusted),
    // NOT bhserve.exe. That keeps antivirus behavioral scanners from flagging bhserve
    // as a "dropper" — without bundling and without a code-signing certificate.
    private static string CurlExe => Path.Combine(Environment.SystemDirectory, "curl.exe");
    private static string TarExe  => Path.Combine(Environment.SystemDirectory, "tar.exe");
    private const string UA = "BHServe/0.1 (+https://github.com/wpexpertinbd/BHServe)";

    // ArgumentList (not a single string) so every arg is escaped by the runtime — a URL or path
    // can never break out and inject extra curl/tar flags.
    private static void Shell(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = System.Diagnostics.Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} failed ({p.ExitCode}): {err.Trim()}");
    }

    /// <summary>Live download progress (0–100), set by the host during a tracked install.</summary>
    public static Action<double>? OnProgress;

    /// <summary>Download a file to <paramref name="dest"/> via the signed system curl.exe, reporting
    /// live progress (curl's --progress-bar on stderr is parsed for the percentage).</summary>
    private static Task CurlTo(string url, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = CurlExe, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        // --progress-bar (instead of -s) gives a parseable "####  45.2%" on stderr.
        foreach (var a in new[] { "-fL", "--progress-bar", "--show-error", "--retry", "5", "--retry-delay", "2",
                                  "--speed-limit", "2048", "--speed-time", "20", "--connect-timeout", "30",
                                  "-A", UA, "-o", dest, url })
            psi.ArgumentList.Add(a);

        var p = System.Diagnostics.Process.Start(psi)!;
        var tail = new System.Text.StringBuilder();
        var buf = new char[256];
        int n;
        while ((n = p.StandardError.Read(buf, 0, buf.Length)) > 0)
        {
            tail.Append(buf, 0, n);
            if (tail.Length > 4000) tail.Remove(0, tail.Length - 1000);   // keep the tail only
            var m = System.Text.RegularExpressions.Regex.Matches(tail.ToString(), @"(\d+(?:\.\d+)?)%");
            if (m.Count > 0 && double.TryParse(m[^1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var pct))
                OnProgress?.Invoke(pct);
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"curl failed ({p.ExitCode}): {tail.ToString().Trim()}");
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

    /// <summary>Extract a zip via the signed system tar.exe (so tar, not bhserve, creates the exes).
    /// Junk helper exes that trip generic AV heuristics (e.g. mingw's sizes.exe) are excluded so
    /// they never touch disk and can't be flagged.</summary>
    private static void ExtractZip(string zip, string destDir)
    {
        Directory.CreateDirectory(destDir);
        // bsdtar: --exclude must precede -f, and its * does NOT cross '/', so match the basename.
        Shell(TarExe, "--exclude", "sizes.exe", "-xf", zip, "-C", destDir);
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

    /// <summary>Install nginx — resolves the latest version from nginx.org, pin as fallback. Only the
    /// binaries in bin\nginx are replaced; the running config lives in Home\nginx and is regenerated
    /// on every Start, so an update never loses site config.</summary>
    public static async Task<string> InstallNginx()
    {
        var ver = await LatestNginx();
        try { return DoInstallNginx(ver); }
        catch when (ver != NginxPinned) { return DoInstallNginx(NginxPinned); }
    }

    private static async Task<string> LatestNginx()
    {
        try
        {
            var html = await ApiGet("https://nginx.org/en/download.html");
            var best = System.Text.RegularExpressions.Regex.Matches(html, @"nginx-(\d+\.\d+\.\d+)")
                .Select(m => m.Groups[1].Value).OrderByDescending(Vparse).FirstOrDefault();
            return best ?? NginxPinned;
        }
        catch { return NginxPinned; }
    }

    private static string DoInstallNginx(string ver)
    {
        var url = $"https://nginx.org/download/nginx-{ver}.zip";
        var zip = DownloadToTmp(url, $"nginx-{ver}.zip").GetAwaiter().GetResult();
        var dir = Path.Combine(Paths.Bin, "nginx");
        // The zip contains nginx-<ver>\, so extract ALONGSIDE rather than deleting bin\nginx first —
        // a still-running old nginx.exe would lock its dir and block the delete. The newest version
        // wins in Tools.NginxExe; old version dirs are best-effort pruned (a locked one is skipped).
        ExtractZip(zip, dir);
        PruneOldVersionDirs(dir, $"nginx-{ver}");
        return Tools.NginxExe() ?? throw new InvalidOperationException("nginx.exe not found after extract");
    }

    /// <summary>Best-effort removal of sibling version dirs under <paramref name="parent"/>, keeping
    /// <paramref name="keep"/>. A dir locked by a running process is silently skipped.</summary>
    private static void PruneOldVersionDirs(string parent, string keep)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(parent))
                if (!string.Equals(Path.GetFileName(d), keep, StringComparison.OrdinalIgnoreCase))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
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

    // Pins are the OFFLINE FALLBACK only. Install/Update resolve the real latest from the
    // vendors' own release APIs, so when MariaDB 13 / MySQL 9.8 ship, "Reinstall (update)"
    // picks them up automatically — no code change needed. The pin is used only if the API
    // is unreachable.
    private const string MariadbPinned = "12.3.2";

    /// <summary>A short-timeout client for the tiny version-resolution API calls (the download
    /// client has a 15-min timeout that's wrong for a quick JSON GET).</summary>
    private static readonly HttpClient ApiHttp = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static async Task<string> ApiGet(string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UA);
        using var resp = await ApiHttp.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    private static Version Vparse(string s) => Version.TryParse(s, out var v) ? v : new Version(0, 0);

    /// <summary>Latest MariaDB STABLE point release from the official release API; pin on failure.</summary>
    private static async Task<string> LatestMariadb()
    {
        try
        {
            using var idx = JsonDocument.Parse(await ApiGet("https://downloads.mariadb.org/rest-api/mariadb/"));
            string? major = null;
            foreach (var mr in idx.RootElement.GetProperty("major_releases").EnumerateArray())
                if (mr.GetProperty("release_status").GetString() == "Stable") { major = mr.GetProperty("release_id").GetString(); break; }
            if (major is null) return MariadbPinned;
            using var rel = JsonDocument.Parse(await ApiGet($"https://downloads.mariadb.org/rest-api/mariadb/{major}/"));
            var latest = rel.RootElement.GetProperty("releases").EnumerateObject()
                            .Select(p => p.Name).OrderByDescending(Vparse).FirstOrDefault();
            return latest ?? MariadbPinned;
        }
        catch { return MariadbPinned; }
    }

    /// <summary>Latest MySQL GA point release (endoflife.date catalog); pin on failure.</summary>
    private static async Task<string> LatestMysql()
    {
        try
        {
            using var doc = JsonDocument.Parse(await ApiGet("https://endoflife.date/api/mysql.json"));
            var latest = doc.RootElement.EnumerateArray()
                .Select(c => c.TryGetProperty("latest", out var l) ? l.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderByDescending(s => Vparse(s!)).FirstOrDefault();
            return latest ?? MysqlPinned;
        }
        catch { return MysqlPinned; }
    }

    /// <summary>Download MariaDB (portable winx64 zip) into bin\mariadb — latest stable, pin fallback.
    /// Only the bin\mariadb binaries are replaced; the data dir (data-mariadb) is left untouched, so
    /// existing databases survive an update.</summary>
    public static async Task<string> InstallMariadb()
    {
        var ver = await LatestMariadb();
        try { return DoInstallMariadb(ver); }
        catch when (ver != MariadbPinned) { return DoInstallMariadb(MariadbPinned); }   // bad/missing winx64 zip → known-good pin
    }

    private static string DoInstallMariadb(string ver)
    {
        var url = $"https://archive.mariadb.org/mariadb-{ver}/winx64-packages/mariadb-{ver}-winx64.zip";
        var zip = DownloadToTmp(url, "mariadb.zip").GetAwaiter().GetResult();
        var dir = Path.Combine(Paths.Bin, "mariadb");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);   // binaries only — data-mariadb is a separate dir
        ExtractZip(zip, dir);
        return Tools.MysqldExe() ?? throw new InvalidOperationException("mysqld.exe not found after extract");
    }

    /// <summary>Download Oracle MySQL (portable winx64 zip) into bin\mysql — latest GA, pin fallback.
    /// Replaces only the bin\mysql binaries; the data dir is untouched so databases survive.</summary>
    public static async Task<string> InstallDb()
    {
        var ver = await LatestMysql();
        try { return DoInstallDb(ver); }
        catch when (ver != MysqlPinned) { return DoInstallDb(MysqlPinned); }
    }

    private static string DoInstallDb(string ver)
    {
        // dev.mysql.com/get/ 302-redirects to the CDN; curl follows it. Series subdir (MySQL-9.7)
        // is derived from the version.
        var series = string.Join('.', ver.Split('.').Take(2));
        var url = $"https://dev.mysql.com/get/Downloads/MySQL-{series}/mysql-{ver}-winx64.zip";
        var zip = DownloadToTmp(url, "mysql.zip").GetAwaiter().GetResult();
        var dir = Path.Combine(Paths.Bin, "mysql");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);
        return Tools.MysqldExe() ?? throw new InvalidOperationException("mysqld.exe not found after extract");
    }

    private const string PgPinned = "16.4-1";

    /// <summary>Install PostgreSQL (EDB portable binaries) — latest version via endoflife.date, pin
    /// fallback. NOTE: a major-version bump (e.g. 16 → 18) can't read an existing pgdata in place
    /// (PostgreSQL needs pg_upgrade), so an update is best on a fresh data dir.</summary>
    public static async Task<string> InstallPostgres()
    {
        var ver = await LatestPostgres();
        try { return DoInstallPostgres(ver); }
        catch when (ver != PgPinned) { return DoInstallPostgres(PgPinned); }
    }

    private static async Task<string> LatestPostgres()
    {
        try
        {
            using var doc = JsonDocument.Parse(await ApiGet("https://endoflife.date/api/postgresql.json"));
            var latest = doc.RootElement.EnumerateArray()
                .Select(c => c.TryGetProperty("latest", out var l) ? l.GetString() : null)
                .Where(s => !string.IsNullOrEmpty(s)).OrderByDescending(s => Vparse(s!)).FirstOrDefault();
            return latest is null ? PgPinned : $"{latest}-1";   // EDB appends a build number; -1 is the first/standard build
        }
        catch { return PgPinned; }
    }

    private static string DoInstallPostgres(string ver)
    {
        var url = $"https://get.enterprisedb.com/postgresql/postgresql-{ver}-windows-x64-binaries.zip";
        var zip = DownloadToTmp(url, "postgresql.zip").GetAwaiter().GetResult();
        var dir = Path.Combine(Paths.Bin, "postgresql");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);   // → bin\postgresql\pgsql\bin\postgres.exe
        return Tools.PostgresExe() ?? throw new InvalidOperationException("postgres.exe not found after extract");
    }

    // Fallback only — Apache Lounge URLs carry a build date + VS toolset that change over time
    // (it has moved VS17 → VS18), so we scrape the current latest and keep this as last resort.
    private const string ApachePinned = "https://www.apachelounge.com/download/VS18/binaries/httpd-2.4.68-260617-Win64-VS18.zip";

    /// <summary>Install Apache (Apache Lounge build) — scrapes the current latest Win64 zip from the
    /// download page (its URLs carry a build date + VS toolset), pin as last resort.</summary>
    public static async Task<string> InstallApache()
    {
        var url = await LatestApacheUrl() ?? ApachePinned;
        var zip = await DownloadToTmp(url, "httpd.zip");
        var dir = Path.Combine(Paths.Bin, "apache");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        ExtractZip(zip, dir);   // → bin\apache\Apache24\bin\httpd.exe
        return Tools.HttpdExe() ?? throw new InvalidOperationException("httpd.exe not found after extract");
    }

    private static async Task<string?> LatestApacheUrl()
    {
        try
        {
            var html = await ApiGet("https://www.apachelounge.com/download/");
            // hrefs look like: VS18/binaries/httpd-2.4.68-260617-Win64-VS18.zip
            var best = System.Text.RegularExpressions.Regex.Matches(
                    html, @"VS\d+/binaries/httpd-2\.4\.\d+-\d+-Win64-VS\d+\.zip",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Select(m => m.Value).Distinct()
                .OrderByDescending(s =>
                {
                    var n = System.Text.RegularExpressions.Regex.Match(s, @"httpd-2\.4\.(\d+)-(\d+)-Win64-VS(\d+)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    // newest by (httpd patch, build date, VS toolset)
                    return (int.Parse(n.Groups[1].Value), long.Parse(n.Groups[2].Value), int.Parse(n.Groups[3].Value));
                })
                .FirstOrDefault();
            return best is null ? null : "https://www.apachelounge.com/download/" + best;
        }
        catch { return null; }
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
            var rootPw = Config.Load().RootPassword;
            txt = txt.Replace("database_name_here", db)
                     .Replace("username_here", "root")
                     .Replace("'password_here'", $"'{rootPw}'")
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
        var allowNoPw = Config.Load().RootPassword.Length == 0 ? "true" : "false";
        File.WriteAllText(Path.Combine(root, "config.inc.php"),
            "<?php\n" +
            $"$cfg['blowfish_secret'] = '{secret}';\n" +
            "$i = 0; $i++;\n" +
            "$cfg['Servers'][$i]['host'] = '127.0.0.1';\n" +
            "$cfg['Servers'][$i]['port'] = '3306';\n" +
            "$cfg['Servers'][$i]['auth_type'] = 'cookie';\n" +
            $"$cfg['Servers'][$i]['AllowNoPassword'] = {allowNoPw};\n");
    }

    /// <summary>Download the latest single-file Adminer to <paramref name="dest"/>.</summary>
    public static async Task InstallAdminer(string dest)
        => await CurlTo("https://www.adminer.org/latest.php", dest);

    static Downloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("BHServe/0.1 (+https://github.com/wpexpertinbd/BHServe)");
    }
}
