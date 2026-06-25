namespace BHServe.Core;

/// <summary>
/// Windows PHP runner. There is NO php-fpm on Windows, so each PHP version runs
/// as one or more <c>php-cgi.exe</c> processes bound to a stable TCP port, and
/// nginx points at it with <c>fastcgi_pass 127.0.0.1:&lt;port&gt;;</c>.
/// This is the structural analog of the mac engine's per-version FPM unix socket.
/// </summary>
public static class PhpCgi
{
    /// <summary>Stable port per PHP minor: 8.1→9181, 8.2→9182, 8.3→9183, 8.4→9184, default→9100.</summary>
    public static int PortFor(string version) // "8.4" or "default"
    {
        if (version is "default" or "")
            return 9100;
        var parts = version.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out var maj) && int.TryParse(parts[1], out var min))
            return 9100 + maj * 10 + min;   // 8.4 -> 9100+80+4 = 9184
        return 9100;
    }

    /// <summary>Path to a version's php-cgi.exe under the bin dir (filled by the downloader).</summary>
    public static string PhpCgiExe(string version) =>
        System.IO.Path.Combine(Paths.Bin, "php", version, "php-cgi.exe");

    /// <summary>Path to a version's php.exe (used to resolve php.ini, run ionCube, etc.).</summary>
    public static string PhpExe(string version) =>
        System.IO.Path.Combine(Paths.Bin, "php", version, "php.exe");

    // TODO(windows): Start(version) → spawn php-cgi.exe -b 127.0.0.1:<port>, write
    // run\php-<ver>.json {pid, port}. Stop(version) → kill the pid. Running(version)
    // → pid alive. On "Start All", start every version any ENABLED site references
    // (the 502 fix), not just the starred default.
}
