using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>
/// Public sharing via Cloudflare quick tunnels (no account needed) — the analog of
/// the mac engine's <c>tunnel</c>. cloudflared writes to a log file (via cmd redirect
/// so it survives the CLI exiting); we poll it for the https://*.trycloudflare.com URL.
/// </summary>
public static class Tunnel
{
    private static string PidFile(string n) => Path.Combine(Paths.Run, $"tunnel-{n}.pid");
    private static string LogFile(string n) => Path.Combine(Paths.Run, $"tunnel-{n}.log");
    private static string UrlFile(string n) => Path.Combine(Paths.Run, $"tunnel-{n}.url");

    public static bool Running(string name)
    {
        try
        {
            if (!File.Exists(PidFile(name))) return false;
            if (int.TryParse(File.ReadAllText(PidFile(name)).Trim(), out var pid))
            { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        }
        catch { }
        return false;
    }

    public static string? Url(string name)
    {
        try { return File.Exists(UrlFile(name)) ? File.ReadAllText(UrlFile(name)).Trim() : null; }
        catch { return null; }
    }

    /// <summary>Read a file that another process holds open for writing (cloudflared's log).
    /// The default File.ReadAllText opens with FileShare.Read, which on Windows throws because
    /// cloudflared (via the `cmd > log` redirect) holds the log open for writing — so the URL is
    /// never seen and the tunnel reports "no URL yet". Opening with FileShare.ReadWrite fixes it.
    /// (POSIX allows the concurrent read, which is why the mac engine never hit this.)</summary>
    private static string ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return ""; }
    }

    public static (bool ok, string msg) Start(string name, string domain, string origin)
    {
        var cf = Tools.CloudflaredExe();
        if (cf is null) return (false, "cloudflared not installed — bhserve tunnel install");
        if (Running(name)) return (true, Url(name) is { } u ? $"tunnel already running: {u}" : "tunnel already running");

        Directory.CreateDirectory(Paths.Run);
        try { File.Delete(UrlFile(name)); } catch { }
        File.WriteAllText(LogFile(name), "");

        var tls = origin.StartsWith("https") ? " --no-tls-verify" : "";
        // Run via cmd so cloudflared's output goes to a FILE (it must outlive this process,
        // and we must not block on an unread stderr pipe).
        var inner = $"\"{cf}\" tunnel --no-autoupdate --url {origin} --http-host-header {domain}{tls} > \"{LogFile(name)}\" 2>&1";
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{inner}\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var proc = Process.Start(psi);
        if (proc is null) return (false, "failed to launch cloudflared");
        File.WriteAllText(PidFile(name), proc.Id.ToString());

        // Poll the log for the public URL (cloudflared prints it within a few seconds).
        for (var i = 0; i < 40; i++)
        {
            var log = ReadShared(LogFile(name));
            var m = Regex.Match(log, @"https://[a-z0-9-]+\.trycloudflare\.com");
            if (m.Success)
            {
                File.WriteAllText(UrlFile(name), m.Value);
                return (true, m.Value);
            }
            if (!Running(name)) return (false, "tunnel exited early — see " + LogFile(name));
            System.Threading.Thread.Sleep(1000);
        }
        return (true, "tunnel up but no URL yet — check " + LogFile(name));
    }

    public static void Stop(string name)
    {
        try
        {
            if (File.Exists(PidFile(name)) && int.TryParse(File.ReadAllText(PidFile(name)).Trim(), out var pid))
                Process.GetProcessById(pid).Kill(true);
        }
        catch { }
        try { File.Delete(PidFile(name)); File.Delete(UrlFile(name)); } catch { }
    }

    public static IEnumerable<(string name, string? url)> List()
    {
        if (!Directory.Exists(Paths.Run)) yield break;
        foreach (var f in Directory.EnumerateFiles(Paths.Run, "tunnel-*.pid"))
        {
            var name = Path.GetFileNameWithoutExtension(f)["tunnel-".Length..];
            if (Running(name)) yield return (name, Url(name));
        }
    }
}
