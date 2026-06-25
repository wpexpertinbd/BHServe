namespace BHServe.Core;

/// <summary>
/// Resolves managed binaries. Primary location is BHServe's own portable installs
/// under <c>%LOCALAPPDATA%\BHServe\bin\&lt;tool&gt;\…</c> (filled by <see cref="Downloader"/>).
/// As a convenience on dev machines we fall back to an existing Laragon install so
/// the stack is testable before anything is downloaded.
/// </summary>
public static class Tools
{
    /// <summary>Laragon roots used as a dev fallback (harmless if absent).</summary>
    private static readonly string LaragonBin = @"C:\laragon\bin";

    /// <summary>Find a file matching <paramref name="glob"/> under <paramref name="root"/> (first hit, shallow-first).</summary>
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

    private static string? FirstDirMatch(string root, string pattern)
    {
        if (!Directory.Exists(root)) return null;
        try { return Directory.EnumerateDirectories(root, pattern).OrderDescending().FirstOrDefault(); }
        catch { return null; }
    }

    /// <summary>php.exe for a version ("8.4"): our bin first, then a matching Laragon build.</summary>
    public static string? PhpExe(string version) => PhpDirExe(version, "php.exe");

    /// <summary>php-cgi.exe for a version — the FastCGI runner nginx talks to.</summary>
    public static string? PhpCgiExe(string version) => PhpDirExe(version, "php-cgi.exe");

    private static string? PhpDirExe(string version, string exe)
    {
        // 1) our managed install: bin\php\8.4\php-cgi.exe
        var managed = Path.Combine(Paths.Bin, "php", version, exe);
        if (File.Exists(managed)) return managed;
        // 2) any nested layout under bin\php\8.4
        var nested = FindUnder(Path.Combine(Paths.Bin, "php", version), exe);
        if (nested is not null) return nested;
        // 3) Laragon fallback: php-8.4.*  (prefer NTS for php-cgi stability)
        var lphp = Path.Combine(LaragonBin, "php");
        if (Directory.Exists(lphp))
        {
            var dir = Directory.EnumerateDirectories(lphp, $"php-{version}.*")
                               .OrderByDescending(d => d.Contains("-nts-")).ThenByDescending(d => d)
                               .FirstOrDefault();
            if (dir is not null)
            {
                var p = Path.Combine(dir, exe);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary>nginx.exe: our bin\nginx\** first, then Laragon.</summary>
    public static string? NginxExe()
    {
        var managed = FindUnder(Path.Combine(Paths.Bin, "nginx"), "nginx.exe");
        if (managed is not null) return managed;
        return FindUnder(Path.Combine(LaragonBin, "nginx"), "nginx.exe");
    }

    /// <summary>The directory nginx treats as its prefix (the dir containing nginx.exe).</summary>
    public static string? NginxPrefix() => NginxExe() is { } exe ? Path.GetDirectoryName(exe) : null;

    public static string? MkcertExe()
    {
        var managed = Path.Combine(Paths.Bin, "mkcert", "mkcert.exe");
        if (File.Exists(managed)) return managed;
        return FindUnder(Path.Combine(Paths.Bin, "mkcert"), "mkcert.exe");
    }

    /// <summary>mysqld.exe (MariaDB/MySQL): our bin first, then Laragon mysql.</summary>
    public static string? MysqldExe()
    {
        var managed = FindUnder(Path.Combine(Paths.Bin, "mariadb"), "mysqld.exe")
                   ?? FindUnder(Path.Combine(Paths.Bin, "mysql"), "mysqld.exe");
        if (managed is not null) return managed;
        return FindUnder(Path.Combine(LaragonBin, "mysql"), "mysqld.exe");
    }

    public static string? MysqlClientExe()
    {
        var managed = FindUnder(Path.Combine(Paths.Bin, "mariadb"), "mysql.exe")
                   ?? FindUnder(Path.Combine(Paths.Bin, "mysql"), "mysql.exe");
        if (managed is not null) return managed;
        return FindUnder(Path.Combine(LaragonBin, "mysql"), "mysql.exe");
    }

    public static string? MailpitExe() => FindUnder(Path.Combine(Paths.Bin, "mailpit"), "mailpit.exe");

    /// <summary>httpd.exe (Apache) — our bin\apache\** first, then Laragon.</summary>
    public static string? HttpdExe()
    {
        var managed = FindUnder(Path.Combine(Paths.Bin, "apache"), "httpd.exe");
        if (managed is not null) return managed;
        return FindUnder(Path.Combine(LaragonBin, "apache"), "httpd.exe");
    }

    /// <summary>Apache ServerRoot = the dir that contains bin\httpd.exe (its parent).</summary>
    public static string? ApacheRoot() =>
        HttpdExe() is { } exe ? Path.GetDirectoryName(Path.GetDirectoryName(exe)!) : null;

    public static string? CloudflaredExe()
    {
        var managed = Path.Combine(Paths.Bin, "cloudflared", "cloudflared.exe");
        if (File.Exists(managed)) return managed;
        return FindUnder(Path.Combine(Paths.Bin, "cloudflared"), "cloudflared.exe");
    }

    public static string? FnmExe()
    {
        var managed = FindUnder(Path.Combine(Paths.Bin, "fnm"), "fnm.exe");
        if (managed is not null) return managed;
        // fall back to a system fnm if the user already has one on PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try { var p = Path.Combine(dir.Trim(), "fnm.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }
}
