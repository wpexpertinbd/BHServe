using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>Tracked php-cgi process: which version, the TCP port it listens on, its pid.</summary>
public sealed record PhpRun(string Version, int Port, int Pid);

/// <summary>
/// Windows PHP runner. There is NO php-fpm on Windows, so each PHP version runs
/// as a <c>php-cgi.exe</c> process bound to a stable TCP port, and nginx points at
/// it with <c>fastcgi_pass 127.0.0.1:&lt;port&gt;;</c>. This is the structural analog
/// of the mac engine's per-version FPM unix socket (<c>render_fpm_pool</c>/<c>fpm_start</c>).
/// </summary>
public static class PhpCgi
{
    /// <summary>Stable port per PHP minor: 8.1→9181, 8.2→9182, 8.3→9183, 8.4→9184, default→9100.</summary>
    public static int PortFor(string version)
    {
        if (version is "default" or "") return 9100;
        var parts = version.Split('.');
        if (parts.Length == 2 && int.TryParse(parts[0], out var maj) && int.TryParse(parts[1], out var min))
            return 9100 + maj * 10 + min;   // 8.4 -> 9100+80+4 = 9184
        return 9100;
    }

    private static string RunFile(string version) => Path.Combine(Paths.Run, $"php-{version}.json");

    /// <summary>BHServe-managed extra-ini dir for a version (ionCube etc.), loaded via PHP_INI_SCAN_DIR.</summary>
    public static string ConfDir(string version) => Path.Combine(Paths.Home, "php", "conf.d", version);

    public static PhpRun? Info(string version)
    {
        try
        {
            var f = RunFile(version);
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<PhpRun>(File.ReadAllText(f));
        }
        catch { return null; }
    }

    public static bool Running(string version)
    {
        var info = Info(version);
        if (info is null) return false;
        try { using var p = Process.GetProcessById(info.Pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>Spawn php-cgi.exe -b 127.0.0.1:&lt;port&gt; for a version (idempotent). Returns false if php missing.</summary>
    public static bool Start(string version)
    {
        if (Running(version)) return true;
        var exe = Tools.PhpCgiExe(version);
        if (exe is null) return false;

        // Raise the stock 2M upload cap to 2 GB on the build's php.ini before launching, so both
        // nginx-served and Apache-served PHP honor it. Idempotent + survives PHP reinstalls (this
        // runs on every start, but only writes when a value actually differs).
        EnsureLimits(Path.GetDirectoryName(exe)!);

        var port = PortFor(version);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = $"-b 127.0.0.1:{port}",
            UseShellExecute = false,
            CreateNoWindow = true,
            // Redirect (and never read) so the daemon does NOT inherit the caller's
            // console/stdout handle — otherwise a foreground shell stays "open" waiting
            // on this long-running child. php-cgi -b logs to nginx, not stdout.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        // PHP_FCGI_MAX_REQUESTS=0 → never recycle the listener.
        psi.Environment["PHP_FCGI_MAX_REQUESTS"] = "0";
        // Load BHServe's per-version conf.d (ionCube etc.) on top of the build's php.ini.
        // Leading ';' keeps the compiled-in scan dir (Windows path-list separator).
        var confd = ConfDir(version);
        Directory.CreateDirectory(confd);
        psi.Environment["PHP_INI_SCAN_DIR"] = ";" + confd;

        var proc = Process.Start(psi);
        if (proc is null) return false;
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile(version),
            JsonSerializer.Serialize(new PhpRun(version, port, proc.Id)));
        return true;
    }

    /// <summary>BHServe's default PHP limits — generous for local dev (2 GB uploads).</summary>
    private static readonly (string key, string val)[] Limits =
    {
        ("upload_max_filesize", "2048M"),
        ("post_max_size",       "2048M"),
        ("memory_limit",        "1024M"),
        ("max_execution_time",  "600"),
        ("max_input_time",      "600"),
        ("max_file_uploads",    "50"),
    };

    /// <summary>Set the upload/size/time directives in a build's php.ini (active or commented),
    /// only writing if something changed. Never throws (best-effort).</summary>
    private static void EnsureLimits(string phpDir)
    {
        try
        {
            var ini = Path.Combine(phpDir, "php.ini");
            if (!File.Exists(ini)) return;
            var text = File.ReadAllText(ini);
            var orig = text;
            foreach (var (key, val) in Limits)
            {
                var rx = new Regex($@"(?m)^[ \t]*;?[ \t]*{Regex.Escape(key)}[ \t]*=.*$");
                text = rx.IsMatch(text) ? rx.Replace(text, $"{key} = {val}", 1) : text.TrimEnd() + $"\n{key} = {val}\n";
            }
            if (text != orig) File.WriteAllText(ini, text);
        }
        catch { }
    }

    public static void Stop(string version)
    {
        var info = Info(version);
        if (info is not null)
        {
            try { Process.GetProcessById(info.Pid).Kill(true); } catch { /* already gone */ }
        }
        try { File.Delete(RunFile(version)); } catch { }
    }
}
