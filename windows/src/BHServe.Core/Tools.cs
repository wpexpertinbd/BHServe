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

    /// <summary>Highest version embedded in a path (e.g. …\nginx-1.31.2\… → 1.31.2), else 0.0 — so the
    /// newest of several coexisting version dirs is preferred when a locked old one can't be removed.</summary>
    private static Version PathVersion(string path)
    {
        Version best = new(0, 0);
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(path.Replace('\\', '/'), @"(\d+\.\d+(?:\.\d+)?)"))
            if (Version.TryParse(m.Groups[1].Value, out var v) && v > best) best = v;
        return best;
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
                                   .ThenByDescending(PathVersion)   // when a stale + new version dir coexist, the newest wins
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
    /// <summary>Installed nginx version parsed from its dir (…\nginx-1.31.2\…), or null.</summary>
    public static string? NginxVersion()
    {
        if (NginxExe() is not { } e) return null;
        var m = System.Text.RegularExpressions.Regex.Match(e.Replace('\\', '/'), @"/nginx-(\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }
    public static string? NginxPrefix() => NginxExe() is { } exe ? Path.GetDirectoryName(exe) : null;

    public static string? MkcertExe() => Find("mkcert", "mkcert.exe");

    // MySQL → bin\mysql, MariaDB → bin\mariadb. MysqldExe prefers MariaDB if both are present
    // (only one DB runs on :3306 at a time; each engine keeps its own data dir).
    public static string? MysqldExe()      => Find("mariadb", "mysqld.exe") ?? Find("mysql", "mysqld.exe");
    public static string? MysqldExe(string engine) => engine == "mariadb" ? Find("mariadb", "mysqld.exe") : Find("mysql", "mysqld.exe");

    /// <summary>The ACTUAL installed version of a DB engine, parsed from its versioned extract dir
    /// (…\mariadb\mariadb-12.3.2-winx64\…), or null if not installed. Lets the UI show the real
    /// version instead of a hardcoded label.</summary>
    public static string? DbVersionFor(string engine)
    {
        if (MysqldExe(engine) is not { } exe) return null;
        var m = System.Text.RegularExpressions.Regex.Match(
            exe.Replace('\\', '/'), @"/(?:mariadb|mysql)-(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }
    public static string? MysqlClientExe() => Find("mariadb", "mysql.exe")  ?? Find("mysql", "mysql.exe");
    /// <summary>The command-line client for a SPECIFIC engine (falls back to the other). Critical when
    /// both are installed: a MariaDB client against a MySQL server can't load MySQL's caching_sha2_password
    /// auth plugin (ERROR 1156/2059), so the running engine must be talked to by its own client.</summary>
    public static string? MysqlClientFor(string engine) => engine == "mariadb"
        ? (Find("mariadb", "mariadb.exe") ?? Find("mariadb", "mysql.exe") ?? Find("mysql", "mysql.exe"))
        : (Find("mysql", "mysql.exe") ?? Find("mariadb", "mariadb.exe") ?? Find("mariadb", "mysql.exe"));
    public static bool MysqlInstalled   => Find("mysql", "mysqld.exe") is not null;
    public static bool MariadbInstalled => Find("mariadb", "mysqld.exe") is not null;
    public static string? MariadbInstallDbExe() => Find("mariadb", "mariadb-install-db.exe") ?? Find("mariadb", "mysql_install_db.exe");
    public static string? MariadbUpgradeExe()   => Find("mariadb", "mariadb-upgrade.exe")    ?? Find("mariadb", "mysql_upgrade.exe");

    public static string? PostgresExe() => Find("postgresql", "postgres.exe");
    public static string? PgCtlExe()    => Find("postgresql", "pg_ctl.exe");
    public static string? PsqlExe()     => Find("postgresql", "psql.exe");
    public static string? InitdbExe()   => Find("postgresql", "initdb.exe");
    public static string? CreatedbExe() => Find("postgresql", "createdb.exe");

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

    // ── Python (portable CPython for Python-app sites) ────────────────────────────
    public static string? PythonExe() => Find("python", "python.exe");
    public static bool PythonInstalled => PythonExe() is not null;
    /// <summary>Directory holding the managed python.exe (prepended to a Python app's PATH).</summary>
    public static string? PythonBinDir() => PythonExe() is { } e ? Path.GetDirectoryName(e) : null;
    /// <summary>Parsed CPython version from the extracted dir (…\cpython-3.13.1+…\…), or null.</summary>
    public static string? PythonVersion()
    {
        if (PythonExe() is not { } exe) return null;
        var m = System.Text.RegularExpressions.Regex.Match(exe.Replace('\\', '/'), @"cpython-(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }

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
