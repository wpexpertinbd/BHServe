using System.Diagnostics;

namespace BHServe.Core;

/// <summary>
/// Stops EVERYTHING BHServe runs when Windows is ending the session.
///
/// Why this exists: Windows asks running programs to close at shutdown/restart. Nothing in BHServe
/// ever did that, so the servers were still alive when the session ended and Windows put up its
/// "This app is preventing you from shutting down" screen naming php-cgi.exe (and nginx.exe, and
/// anything else we had spawned). Clicking OK did nothing — nobody had asked those processes to
/// exit — so the user had to force the machine off. Linux/macOS never showed this because
/// systemd/launchd signal their services at session end.
///
/// Two rules shape this:
///  • It must be FAST. Windows only waits a few seconds before showing that screen, so a leisurely
///    graceful stop of eight services would itself become the thing blocking shutdown. Only the
///    DATABASES get a clean stop (they have files to close); everything else is stateless and is
///    killed outright.
///  • It must be COMPLETE. Engine.Stop("all") covers the core services but NOT node apps, python
///    apps or Cloudflare tunnels — those are long-lived processes too, and they blocked shutdown
///    just the same. After the targeted stops we sweep anything still alive that runs from
///    BHServe's own folders, so a process we forgot (or one orphaned by an earlier crash) can't
///    hold the shutdown either.
/// </summary>
public static class ShutdownGuard
{
    /// <summary>Set as soon as Windows starts ending the session. PhpCgi checks it so the heal
    /// loop cannot respawn a worker into a machine that is shutting down (which is what produced
    /// the "php-cgi.exe was unable to start correctly" dialog). One-way: nothing clears it.</summary>
    public static volatile bool IsShuttingDown;

    /// <summary>Stop every BHServe-managed process, FAST. Safe to call twice; never throws.</summary>
    public static void StopAllForShutdown()
    {
        IsShuttingDown = true;   // stop the heal loop respawning behind us
        // Measured: a normal graceful `stop all` takes ~16s on a busy stack (13 nginx + 91 php-cgi
        // workers, each asked to quit politely). Windows shows its "preventing shutdown" screen after
        // about 5, so doing that here would just move the blocking process from php-cgi to us.
        //
        // So: the DATABASES get a real, graceful stop — they have files to close and are the only
        // thing with data at risk — bounded so a wedged server can't eat the whole budget. Everything
        // else is stateless (web servers, PHP workers, caches, tunnels) and is killed outright.
        var dbs = Task.Run(() =>
        {
            Try(() => { if (DbServer.Running()) DbServer.Stop(); });
            Try(() => { if (PgServer.Running()) PgServer.Stop(); });
        });
        dbs.Wait(TimeSpan.FromSeconds(3));

        // Node/Python apps can run from the USER's project folder (a system python, say), so the
        // path sweep below would miss them — stop those by their tracked pid first.
        Try(() => { foreach (var n in NodeSite.List()) Try(() => NodeSite.Stop(n)); });
        Try(() => { foreach (var n in PySite.List())   Try(() => PySite.Stop(n)); });
        Try(() => { foreach (var (name, _) in Tunnel.List()) Try(() => Tunnel.Stop(name)); });

        // Everything that runs out of BHServe's own folders — nginx, php-cgi, httpd, redis,
        // memcached, mailpit, cloudflared, and any leftover orphan from an earlier crash.
        KillOwnedProcesses();
    }

    /// <summary>Kill every live process whose executable lives under BHServe's home/bin. Path
    /// matching (not process names) so we only ever touch OUR copies — a system nginx/python/node
    /// the user runs for their own work is never a candidate.</summary>
    private static void KillOwnedProcesses()
    {
        string[] roots;
        try { roots = new[] { Paths.Bin, Paths.Home }.Where(Directory.Exists).ToArray(); }
        catch { return; }
        if (roots.Length == 0) return;

        Process[] all;
        try { all = Process.GetProcesses(); } catch { return; }
        var self = Environment.ProcessId;

        foreach (var p in all)
        {
            try
            {
                if (p.Id == self || p.HasExited) continue;
                // MainModule throws for processes we can't open (system/other-user) — those are
                // never ours, so the catch below simply skips them.
                var path = p.MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) continue;
                if (!roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase))) continue;
                p.Kill(entireProcessTree: true);
            }
            catch { /* not ours, already gone, or access denied — skip */ }
            finally { try { p.Dispose(); } catch { } }
        }
    }

    private static void Try(Action a) { try { a(); } catch { } }
}
