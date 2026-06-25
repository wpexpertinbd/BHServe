namespace BHServe.Core;

/// <summary>
/// Resolves managed binaries from BHServe's own portable installs — in two roots:
/// (1) the user's <c>%LOCALAPPDATA%\BHServe\bin\</c> (on-demand downloads/updates), and
/// (2) the bundled <c>&lt;app&gt;\bin\</c> shipped inside the installer. The bundled root
/// means a fresh install runs with ZERO runtime executable downloads — which keeps
/// antivirus behavioral scanners from flagging bhserve.exe as a "dropper".
/// BHServe never borrows binaries from Laragon/XAMPP/etc.
/// </summary>
public static class Tools
{
    /// <summary>Search roots, in priority order: user downloads first, then the bundled install.</summary>
    private static IEnumerable<string> BinRoots()
    {
        yield return Paths.Bin;
        var appBin = Path.Combine(AppContext.BaseDirectory, "bin");
        if (!string.Equals(appBin, Paths.Bin, StringComparison.OrdinalIgnoreCase)) yield return appBin;
    }

    /// <summary>First match for <paramref name="fileName"/> under <c>&lt;root&gt;\&lt;tool&gt;\…</c> across both roots.</summary>
    private static string? Find(string tool, string fileName)
    {
        foreach (var root in BinRoots())
        {
            var dir = Path.Combine(root, tool);
            if (!Directory.Exists(dir)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories)
                                   .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                                   .FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }

    public static string? PhpExe(string version)    => Find(Path.Combine("php", version), "php.exe");
    public static string? PhpCgiExe(string version) => Find(Path.Combine("php", version), "php-cgi.exe");

    public static string? NginxExe() => Find("nginx", "nginx.exe");
    public static string? NginxPrefix() => NginxExe() is { } exe ? Path.GetDirectoryName(exe) : null;

    public static string? MkcertExe() => Find("mkcert", "mkcert.exe");

    public static string? MysqldExe()      => Find("mariadb", "mysqld.exe") ?? Find("mysql", "mysqld.exe");
    public static string? MysqlClientExe() => Find("mariadb", "mysql.exe")  ?? Find("mysql", "mysql.exe");

    public static string? MailpitExe() => Find("mailpit", "mailpit.exe");

    public static string? HttpdExe() => Find("apache", "httpd.exe");
    public static string? ApacheRoot() =>
        HttpdExe() is { } exe ? Path.GetDirectoryName(Path.GetDirectoryName(exe)!) : null;

    public static string? RedisServerExe() => Find("redis", "redis-server.exe");

    public static string? MemcachedExe()
    {
        // prefer a 64-bit build if the extract has several
        foreach (var root in BinRoots())
        {
            var dir = Path.Combine(root, "memcached");
            if (!Directory.Exists(dir)) continue;
            try
            {
                var hit = Directory.EnumerateFiles(dir, "memcached.exe", SearchOption.AllDirectories)
                                   .OrderByDescending(p => p.Contains("win64") || p.Contains("x64"))
                                   .FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { }
        }
        return null;
    }

    public static string? CloudflaredExe() => Find("cloudflared", "cloudflared.exe");

    public static string? FnmExe() => Find("fnm", "fnm.exe");

    /// <summary>Directory holding the fnm-managed default node.exe + npm (for Node-app sites).</summary>
    public static string? NodeBinDir()
    {
        var root = Path.Combine(Paths.Home, "node");   // FNM_DIR
        if (!Directory.Exists(root)) return null;
        try
        {
            // prefer the 'default' alias, else the newest installed version
            var node = Directory.EnumerateFiles(root, "node.exe", SearchOption.AllDirectories)
                                 .OrderByDescending(p => p.Contains("default"))
                                 .ThenByDescending(p => p)
                                 .FirstOrDefault();
            return node is null ? null : Path.GetDirectoryName(node);
        }
        catch { return null; }
    }
}
