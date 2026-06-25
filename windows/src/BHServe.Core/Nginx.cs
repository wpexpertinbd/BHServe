using System.Diagnostics;

namespace BHServe.Core;

/// <summary>nginx process lifecycle on Windows (start/stop/reload/test) — analog of the
/// mac engine's <c>nginx_start</c>/<c>nginx_stop</c>/<c>nginx_reload</c>.</summary>
public static class Nginx
{
    private static string NginxDir => Path.Combine(Paths.Home, "nginx");
    private static string ConfPath => Path.Combine(NginxDir, "nginx.conf");
    private static string PidFile  => Path.Combine(Paths.Run, "nginx.pid");

    public static bool Running()
    {
        // pid file is authoritative when valid, but it goes stale across restarts — so
        // fall back to "is any nginx.exe process alive?" (avoids a stale pid skipping reloads).
        try
        {
            if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) return true;
            }
        }
        catch { }
        try { return Process.GetProcessesByName("nginx").Length > 0; } catch { return false; }
    }

    private static (int code, string output) Run(string exe, string args, bool wait = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        var proc = Process.Start(psi)!;
        if (!wait)
            // Detached daemon: DON'T read the streams — ReadToEnd() would block until the
            // child exits (i.e. forever for nginx). Redirecting (above) is enough to keep
            // the daemon from inheriting the caller's console handle.
            return (0, "");
        var outp = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return (proc.ExitCode, outp);
    }

    /// <summary>nginx -t: returns (ok, message). Treats "syntax is ok" as success even if the bind probe fails.</summary>
    public static (bool ok, string msg) Test(string exe)
    {
        var (_, outp) = Run(exe, $"-t -p \"{NginxConfig.Fwd(NginxDir)}\" -c \"{NginxConfig.Fwd(ConfPath)}\"");
        return (outp.Contains("syntax is ok"), outp);
    }

    public static (bool ok, string msg) Start(Config cfg)
    {
        NginxConfig.RenderMain(cfg);
        var exe = Tools.NginxExe();
        if (exe is null) return (false, "nginx not installed — run: bhserve install nginx");
        if (Running()) return (true, "nginx already running");

        var (ok, msg) = Test(exe);
        if (!ok) return (false, "nginx config test failed:\n" + msg);

        // Launch detached. nginx daemonizes on Windows and writes its own pid file.
        Run(exe, $"-p \"{NginxConfig.Fwd(NginxDir)}\" -c \"{NginxConfig.Fwd(ConfPath)}\"", wait: false);
        System.Threading.Thread.Sleep(400);
        return Running() ? (true, "nginx started") : (false, "nginx failed to start (see logs/nginx-error.log)");
    }

    public static void Stop()
    {
        var exe = Tools.NginxExe();
        if (exe is not null && Running())
            Run(exe, $"-s stop -p \"{NginxConfig.Fwd(NginxDir)}\" -c \"{NginxConfig.Fwd(ConfPath)}\"");
        // Fallback: kill by pid if -s stop didn't clear it.
        try
        {
            if (File.Exists(PidFile) && int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
                Process.GetProcessById(pid).Kill(true);
        }
        catch { }
    }

    public static void Reload(Config cfg)
    {
        NginxConfig.RenderMain(cfg);
        var exe = Tools.NginxExe();
        if (exe is null || !Running()) return;
        Run(exe, $"-s reload -p \"{NginxConfig.Fwd(NginxDir)}\" -c \"{NginxConfig.Fwd(ConfPath)}\"");
    }
}
