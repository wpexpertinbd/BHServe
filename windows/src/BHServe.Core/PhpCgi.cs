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
        // ⚠️ THE ionCube-in-php-cgi ROOT CAUSE (win-v1.0.38): when the WinUI3 GUI process
        // (BHServe.App) spawns php-cgi, its worker children SILENTLY FAIL to load zend_extensions
        // like ionCube — a WinUI3/WindowsAppSDK process-context quirk. Proven exhaustively: a plain
        // console process (the bhserve.exe CLI, a bare .NET console, even Python) spawning the SAME
        // php-cgi with the SAME env/args loads ionCube fine; only GUI-spawned workers fail. So when
        // we're the GUI, DELEGATE the real spawn to the plain bhserve.exe CLI; when we're already a
        // console (the CLI itself, incl. the hidden __spawn-php verb), spawn directly.
        if (IsGuiProcess()) return SpawnViaCli(version);
        return SpawnWorker(version);
    }

    /// <summary>True when we're running inside the WinUI GUI (BHServe.App) rather than the console CLI.</summary>
    private static bool IsGuiProcess()
    {
        try { return Process.GetCurrentProcess().ProcessName.Equals("BHServe.App", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>GUI path: run `bhserve.exe __spawn-php &lt;version&gt;` so php-cgi is a child of the plain
    /// console (where ionCube loads), then confirm via the runfile. No handle inheritance / no window
    /// so nothing of the GUI's process context leaks into the CLI or its php-cgi grandchildren.</summary>
    private static bool SpawnViaCli(string version)
    {
        try
        {
            var cli = Path.Combine(AppContext.BaseDirectory, "bhserve.exe");
            if (!File.Exists(cli))
            { Heal($"gui: bhserve.exe helper NOT FOUND — spawning php {version} directly from the GUI"); return SpawnWorker(version); }
            var psi = new ProcessStartInfo
            {
                FileName = cli,
                Arguments = $"__spawn-php {version}",
                UseShellExecute = false,
                CreateNoWindow = true,      // no redirect => bInheritHandles=false => clean context
            };
            var p = Process.Start(psi);
            if (p is null) { Heal($"gui: helper Process.Start returned null for php {version}"); return false; }
            p.WaitForExit(120000);   // generous: the helper may respawn-heal several times at boot
            if (!p.HasExited) Heal($"gui: helper for php {version} still running after 120s (continuing)");
            return Running(version);
        }
        catch (Exception e) { Heal($"gui: helper launch FAILED for php {version}: {e.GetType().Name}: {e.Message}"); return false; }
    }

    /// <summary>The REAL php-cgi spawn + VERIFY-AND-HEAL. MUST run in a plain console process
    /// (bhserve.exe) — see Start(). After binding, when ionCube is configured for this version we
    /// PROBE the actual FastCGI workers for the loaded extension; if missing (an intermittent
    /// load race — seen reliably after a cold reboot when all versions burst-start, err 126 on the
    /// loader DLL), we kill and respawn with backoff until the workers really have ionCube. This
    /// makes ionCube survive reboots by construction, independent of WHY a given load failed.</summary>
    public static bool SpawnWorker(string version)
    {
        if (Running(version)) { Heal($"spawn php {version}: already running — skipped"); return true; }
        var wantIonCube = IonCubeConfigured(version);
        // Breadcrumb EVERY spawn (a few lines per boot) — a boot where "nothing was logged" must be
        // impossible: if this line is absent from a boot, the spawn simply didn't go through here.
        Heal($"spawn php {version} (ioncube={(wantIonCube ? "verify" : "not-configured")})");
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            if (!SpawnOnce(version)) { Heal($"spawn php {version}: SpawnOnce failed (php missing?)"); return false; }
            if (!wantIonCube) return true;                     // nothing to verify
            if (ProbeIonCube(version)) { if (attempt > 1) Heal($"php {version}: ionCube OK after respawn #{attempt}"); return true; }
            Heal($"php {version}: workers came up WITHOUT ionCube (attempt {attempt}) — respawning");
            Stop(version);
            System.Threading.Thread.Sleep(500 * attempt);      // backoff: let the load contention pass
        }
        Heal($"php {version}: ionCube still not loading after 4 attempts — leaving php running without it");
        return SpawnOnce(version);                             // php still serves, just without ionCube
    }

    /// <summary>Is an ionCube zend_extension configured for this version (php.ini or our conf.d)?</summary>
    private static bool IonCubeConfigured(string version)
    {
        try
        {
            var exe = Tools.PhpCgiExe(version);
            if (exe is null) return false;
            var ini = Path.Combine(Path.GetDirectoryName(exe)!, "php.ini");
            if (File.Exists(ini) &&
                Regex.IsMatch(File.ReadAllText(ini), @"(?im)^\s*zend_extension\s*=.*ioncube")) return true;
            var confd = ConfDir(version);
            if (Directory.Exists(confd))
                foreach (var f in Directory.EnumerateFiles(confd, "*.ini"))
                    if (Regex.IsMatch(File.ReadAllText(f), @"(?im)^\s*zend_extension\s*=.*ioncube")) return true;
        }
        catch (Exception e)
        {
            // A transient read failure here must NOT silently disable verification for this spawn.
            Heal($"php {version}: ioncube-config check FAILED ({e.GetType().Name}) — assuming configured");
            return true;
        }
        return false;
    }

    /// <summary>Ask the RUNNING FastCGI workers whether ionCube is loaded — a real end-to-end check
    /// against the serving processes (a fresh `php-cgi -m` proves nothing about them). Writes a tiny
    /// probe script and FastCGI-requests it twice (two different pool children).</summary>
    private static bool ProbeIonCube(string version)
    {
        try
        {
            Directory.CreateDirectory(Paths.Tmp);
            var probe = Path.Combine(Paths.Tmp, "_bh_icprobe.php");
            File.WriteAllText(probe, "<?php echo extension_loaded('ionCube Loader') ? 'ICOK' : 'ICNO';");
            var port = PortFor(version);
            // Wait for the listener to actually answer (children may still be starting). At COLD BOOT
            // this can take a long time (login storm), so the window is generous — a silent give-up
            // here is exactly how ionCube used to slip through to a served site.
            for (var i = 0; i < 60; i++)
            {
                if (FcgiRequest(port, probe) is not null) break;    // listener is up
                System.Threading.Thread.Sleep(500);
                if (i == 59)
                {
                    Heal($"php {version}: probe could not reach workers within 30s — verification SKIPPED (delayed heal pass will re-check)");
                    return true;   // never respawn-loop on an unreachable listener
                }
            }
            // Sample MANY pool children (with PHP_FCGI_CHILDREN=12 the load races PER CHILD; a fresh
            // master can have some children with ionCube and some without). Give the pool a moment to
            // finish starting, then require EVERY sampled child to report ionCube — one ICNO ⇒ respawn.
            // A false "OK" here is exactly what left a site serving without ionCube (2-sample probe).
            System.Threading.Thread.Sleep(1500);
            var reached = 0;
            for (var i = 0; i < 24; i++)                            // 24 samples across 12 children
            {
                var r = FcgiRequest(port, probe);
                if (r is null) { System.Threading.Thread.Sleep(200); continue; }
                reached++;
                if (!r.Contains("ICOK")) return false;             // a child without ionCube ⇒ heal
            }
            if (reached == 0)
            { Heal($"php {version}: probe reached NO worker in the sampling window — verification SKIPPED"); return true; }
            return true;                                            // every sampled child had ionCube
        }
        catch (Exception e) { Heal($"php {version}: probe failed ({e.GetType().Name}) — verification SKIPPED"); }
        return true;   // probe machinery failed → don't respawn-loop on a broken probe (logged above)
    }

    /// <summary>Verify-and-heal pass for an already-running version (called by the CLI `__heal-php`
    /// verb some minutes after launch, when the boot storm has settled). MUST run in a console
    /// process. No-op when ionCube isn't configured or the workers already have it.</summary>
    public static void EnsureIonCube(string version)
    {
        if (!IonCubeConfigured(version)) return;
        if (!Running(version)) { SpawnWorker(version); return; }
        if (ProbeIonCube(version)) return;
        Heal($"php {version}: heal pass found workers WITHOUT ionCube — respawning");
        Stop(version);
        SpawnWorker(version);
    }

    /// <summary>FAST health check for the heal-until-healthy loop: are this version's RUNNING workers
    /// serving ionCube right now? Unlike ProbeIonCube it never waits for a listener (returns false
    /// immediately if nothing's up) so it's cheap to call repeatedly. Samples several pool children;
    /// ALL must report ionCube (one cold child ⇒ not healthy). No ionCube configured ⇒ trivially OK.</summary>
    public static bool VerifyIonCube(string version)
    {
        if (!IonCubeConfigured(version)) return true;
        if (!Running(version)) return false;
        try
        {
            Directory.CreateDirectory(Paths.Tmp);
            var probe = Path.Combine(Paths.Tmp, "_bh_icprobe.php");
            File.WriteAllText(probe, "<?php echo extension_loaded('ionCube Loader') ? 'ICOK' : 'ICNO';");
            var port = PortFor(version);
            var reached = 0;
            for (var i = 0; i < 16; i++)                            // 16 samples across the pool
            {
                var r = FcgiRequest(port, probe);
                if (r is null) { System.Threading.Thread.Sleep(150); continue; }
                reached++;
                if (!r.Contains("ICOK")) return false;             // a child without ionCube ⇒ not healthy
            }
            return reached > 0;                                     // every reached child had ionCube
        }
        catch { return false; }                                    // treat as not-verified ⇒ the loop retries
    }

    /// <summary>Respawn one version through the normal (GUI-delegating) Start path, so the fresh
    /// workers are spawned exactly like a manual start. Used by the heal-until-healthy loop.</summary>
    public static void HealOnce(string version)
    {
        if (!IonCubeConfigured(version)) return;
        Stop(version);
        Start(version);   // GUI → SpawnViaCli (windowless CLI helper); console → SpawnWorker
    }

    /// <summary>Append to the php-heal audit log from outside this class (e.g. the pass banner).</summary>
    public static void HealLog(string msg) => Heal(msg);

    /// <summary>Minimal FastCGI responder request to 127.0.0.1:port for a script. Returns the raw
    /// response body, or null when the connection failed (listener not up yet).</summary>
    private static string? FcgiRequest(int port, string scriptPath)
    {
        try
        {
            using var sock = new System.Net.Sockets.TcpClient();
            if (!sock.ConnectAsync("127.0.0.1", port).Wait(2000)) return null;
            using var s = sock.GetStream();
            s.ReadTimeout = 5000; s.WriteTimeout = 5000;

            static byte[] Rec(byte type, byte[] data)
            {
                var r = new byte[8 + data.Length];
                r[0] = 1; r[1] = type; r[2] = 0; r[3] = 1;                       // version, type, requestId=1
                r[4] = (byte)(data.Length >> 8); r[5] = (byte)(data.Length & 0xFF);
                data.CopyTo(r, 8);
                return r;
            }
            static void Kv(System.IO.MemoryStream m, string k, string v)
            {
                var kb = System.Text.Encoding.UTF8.GetBytes(k);
                var vb = System.Text.Encoding.UTF8.GetBytes(v);
                m.WriteByte((byte)kb.Length); m.WriteByte((byte)vb.Length);      // our names/values are < 128
                m.Write(kb); m.Write(vb);
            }
            var ps = new System.IO.MemoryStream();
            Kv(ps, "SCRIPT_FILENAME", scriptPath.Replace('\\', '/'));
            Kv(ps, "REQUEST_METHOD", "GET");
            Kv(ps, "SERVER_PROTOCOL", "HTTP/1.1");
            Kv(ps, "GATEWAY_INTERFACE", "CGI/1.1");
            Kv(ps, "SCRIPT_NAME", "/_bh_icprobe.php");
            Kv(ps, "QUERY_STRING", "");

            s.Write(Rec(1, new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }));              // BEGIN_REQUEST responder
            s.Write(Rec(4, ps.ToArray())); s.Write(Rec(4, Array.Empty<byte>())); // PARAMS + end
            s.Write(Rec(5, Array.Empty<byte>()));                                // STDIN end

            var outBuf = new System.IO.MemoryStream();
            var hdr = new byte[8];
            while (true)
            {
                var read = 0;
                while (read < 8) { var n = s.Read(hdr, read, 8 - read); if (n <= 0) goto done; read += n; }
                int clen = (hdr[4] << 8) | hdr[5]; int plen = hdr[6];
                var body = new byte[clen + plen]; var got = 0;
                while (got < body.Length) { var n = s.Read(body, got, body.Length - got); if (n <= 0) goto done; got += n; }
                if (hdr[1] == 6) outBuf.Write(body, 0, clen);                    // STDOUT
                if (hdr[1] == 3) break;                                          // END_REQUEST
            }
        done:
            return System.Text.Encoding.UTF8.GetString(outBuf.ToArray());
        }
        catch { return null; }
    }

    /// <summary>Append a line to the php-heal log so post-reboot behavior is auditable. Concurrent
    /// writers (app + several helpers at boot) can collide on the file — RETRY on IOException so a
    /// collision can't silently eat evidence (a silent skip here cost us a whole reboot cycle).</summary>
    private static void Heal(string msg)
    {
        for (var t = 0; t < 4; t++)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                File.AppendAllText(Path.Combine(Paths.Logs, "php-heal.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {msg}{Environment.NewLine}");
                return;
            }
            catch (IOException) { System.Threading.Thread.Sleep(50); }
            catch { return; }
        }
    }

    /// <summary>One raw spawn of php-cgi.exe -b (no verification) + runfile write.</summary>
    private static bool SpawnOnce(string version)
    {
        if (Running(version)) return true;
        var exe = Tools.PhpCgiExe(version);
        if (exe is null) return false;

        // Tune the build's php.ini before launching (uploads + OPcache/JIT + realpath cache) so
        // both nginx- and Apache-served PHP are fast. Idempotent + survives reinstalls (runs on
        // every start, only writes when something differs). OPcache is the big WordPress win —
        // Windows PHP ships it off, so every request recompiles all PHP without this.
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
        // PHP_FCGI_CHILDREN: spawn a pool of worker processes so one slow/cold request (a WordPress
        // first-load phoning home, a heavy app compile) doesn't block every other site on this PHP
        // version. Without it, php-cgi -b on Windows serializes to a SINGLE request at a time → 502s
        // under multi-site load.
        psi.Environment["PHP_FCGI_CHILDREN"] = "12";
        // Load BHServe's per-version conf.d (ionCube etc.) on top of the build's php.ini.
        // Leading ';' keeps the compiled-in scan dir (Windows path-list separator).
        var confd = ConfDir(version);
        Directory.CreateDirectory(confd);
        psi.Environment["PHP_INI_SCAN_DIR"] = ";" + confd;

        // ── Guarantee a usable Path + SystemRoot for the workers ────────────────────────────────
        // The tray App can be launched with a STRIPPED environment (empty Path/SystemRoot — observed
        // when it starts via its login-item/elevation path). php-cgi inherits that, and the FastCGI
        // CHILD workers the master then spawns can't resolve the ionCube loader's dependency DLLs (the
        // VC++ runtime in System32) → ionCube SILENTLY fails to load, breaking every ionCube-encoded
        // app (e.g. WHMCS). A directly-launched php-cgi loads ionCube fine even with an empty env; only
        // the master-spawned children are hit, and only when Path/SystemRoot are missing. Rebuild a
        // sane Path (php dir + Windows system dirs) + SystemRoot so the workers load ionCube regardless
        // of how the App itself was launched. Prepend our dirs; keep any inherited Path after them.
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);   // C:\Windows\System32
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);  // C:\Windows
        var phpDir = Path.GetDirectoryName(exe)!;
        var pathParts = new List<string> { phpDir, sysDir, Path.Combine(sysDir, "Wbem"), winDir };
        if (psi.Environment.TryGetValue("Path", out var inheritedPath) && !string.IsNullOrWhiteSpace(inheritedPath))
            pathParts.Add(inheritedPath);
        psi.Environment["Path"] = string.Join(";",
            pathParts.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (!psi.Environment.TryGetValue("SystemRoot", out var sr) || string.IsNullOrWhiteSpace(sr))
            psi.Environment["SystemRoot"] = winDir;
        if (!psi.Environment.TryGetValue("windir", out var wd) || string.IsNullOrWhiteSpace(wd))
            psi.Environment["windir"] = winDir;

        var proc = Process.Start(psi);
        if (proc is null) return false;
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile(version),
            JsonSerializer.Serialize(new PhpRun(version, port, proc.Id)));
        return true;
    }

    /// <summary>BHServe's php.ini defaults — generous uploads + performance (OPcache/JIT/realpath).</summary>
    private static readonly (string key, string val)[] Limits =
    {
        // generous local-dev limits
        ("upload_max_filesize", "2048M"),
        ("post_max_size",       "2048M"),
        ("memory_limit",        "1024M"),
        ("max_execution_time",  "600"),
        ("max_input_time",      "600"),
        ("max_file_uploads",    "50"),
        // realpath cache — WordPress includes hundreds of files; Windows file stat is slow
        ("realpath_cache_size", "4096k"),
        ("realpath_cache_ttl",  "600"),
        // OPcache — caches compiled PHP so each request doesn't recompile all of WP (huge on Windows)
        ("opcache.enable",                 "1"),
        ("opcache.enable_cli",             "0"),
        ("opcache.memory_consumption",     "256"),
        ("opcache.interned_strings_buffer","16"),
        ("opcache.max_accelerated_files",  "20000"),
        ("opcache.validate_timestamps",    "1"),   // still picks up file edits...
        ("opcache.revalidate_freq",        "2"),   // ...but only re-stats every 2s
        ("opcache.jit",                    "tracing"),
        ("opcache.jit_buffer_size",        "128M"),
    };

    /// <summary>Apply BHServe's limits + performance directives to a build's php.ini (active or
    /// commented), enabling the OPcache extension. Only writes if something changed; never throws.</summary>
    private static void EnsureLimits(string phpDir)
    {
        try
        {
            var ini = Path.Combine(phpDir, "php.ini");
            if (!File.Exists(ini)) return;
            var text = File.ReadAllText(ini);
            var orig = text;

            // OPcache is a Zend extension — php.ini-development ships it commented. Turn it on.
            if (Regex.IsMatch(text, @"(?m)^[ \t]*;[ \t]*zend_extension[ \t]*=[ \t]*opcache"))
                text = Regex.Replace(text, @"(?m)^[ \t]*;[ \t]*zend_extension[ \t]*=[ \t]*opcache.*$", "zend_extension=opcache", RegexOptions.None);
            else if (!Regex.IsMatch(text, @"(?m)^[ \t]*zend_extension[ \t]*=[ \t]*opcache"))
                text = text.TrimEnd() + "\nzend_extension=opcache\n";

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
        // The tracked pid can be stale while ORPHANED php-cgi masters from earlier starts keep
        // serving the port (a restart that didn't clean up cleanly, an app relaunch, etc. — the
        // same pile-up nginx had). Then a "restart" kills only the tracked pid, the orphan keeps
        // answering, and workers spawned with a stale/stripped env stay live — which is why ionCube
        // stopped loading until a full stop-all + start-all. Kill EVERY php-cgi for THIS version's
        // exe path so a restart truly respawns fresh workers (with the ionCube-capable env).
        var exe = Tools.PhpCgiExe(version);
        if (exe is not null)
        {
            foreach (var p in Process.GetProcessesByName("php-cgi"))
            {
                try
                {
                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (path is not null && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                        p.Kill(true);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        try { File.Delete(RunFile(version)); } catch { }
    }
}
