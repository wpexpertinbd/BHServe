namespace BHServe.Core;

/// <summary>
/// Resolves managed binaries — ALL under BHServe's own portable installs at
/// <c>%LOCALAPPDATA%\BHServe\bin\&lt;tool&gt;\…</c> (filled by <see cref="Downloader"/>).
/// BHServe is self-contained: it does NOT borrow binaries from Laragon/XAMPP/etc.
/// A null result means "not installed — run <c>bhserve install &lt;tool&gt;</c>".
/// </summary>
public static class Tools
{
    /// <summary>Find a file under <paramref name="root"/> (first hit, shallowest path).</summary>
    private static string? FindUnder(string root, string fileName)
    {
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                            .OrderBy(p => p.Count(c => c == Path.DirectorySeparatorChar))
                            .FirstOrDefault();
        }
        catch { return null; }
    }

    public static string? PhpExe(string version)    => PhpDirExe(version, "php.exe");
    public static string? PhpCgiExe(string version) => PhpDirExe(version, "php-cgi.exe");

    private static string? PhpDirExe(string version, string exe)
    {
        var managed = Path.Combine(Paths.Bin, "php", version, exe);   // bin\php\8.4\php-cgi.exe
        if (File.Exists(managed)) return managed;
        return FindUnder(Path.Combine(Paths.Bin, "php", version), exe);
    }

    public static string? NginxExe() => FindUnder(Path.Combine(Paths.Bin, "nginx"), "nginx.exe");

    /// <summary>The directory nginx treats as its prefix (the dir containing nginx.exe).</summary>
    public static string? NginxPrefix() => NginxExe() is { } exe ? Path.GetDirectoryName(exe) : null;

    public static string? MkcertExe() => FindUnder(Path.Combine(Paths.Bin, "mkcert"), "mkcert.exe");

    public static string? MysqldExe() =>
        FindUnder(Path.Combine(Paths.Bin, "mariadb"), "mysqld.exe")
        ?? FindUnder(Path.Combine(Paths.Bin, "mysql"), "mysqld.exe");

    public static string? MysqlClientExe() =>
        FindUnder(Path.Combine(Paths.Bin, "mariadb"), "mysql.exe")
        ?? FindUnder(Path.Combine(Paths.Bin, "mysql"), "mysql.exe");

    public static string? MailpitExe() => FindUnder(Path.Combine(Paths.Bin, "mailpit"), "mailpit.exe");

    public static string? HttpdExe() => FindUnder(Path.Combine(Paths.Bin, "apache"), "httpd.exe");

    /// <summary>Apache ServerRoot = the dir that contains bin\httpd.exe (its parent).</summary>
    public static string? ApacheRoot() =>
        HttpdExe() is { } exe ? Path.GetDirectoryName(Path.GetDirectoryName(exe)!) : null;

    public static string? RedisServerExe() => FindUnder(Path.Combine(Paths.Bin, "redis"), "redis-server.exe");

    public static string? MemcachedExe()
    {
        var dir = Path.Combine(Paths.Bin, "memcached");
        if (!Directory.Exists(dir)) return null;
        try
        {
            return Directory.EnumerateFiles(dir, "memcached.exe", SearchOption.AllDirectories)
                            .OrderByDescending(p => p.Contains("win64") || p.Contains("x64"))
                            .FirstOrDefault();
        }
        catch { return null; }
    }

    public static string? CloudflaredExe() => FindUnder(Path.Combine(Paths.Bin, "cloudflared"), "cloudflared.exe");

    public static string? FnmExe() => FindUnder(Path.Combine(Paths.Bin, "fnm"), "fnm.exe");
}
