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
        // Always spawn php-cgi DIRECTLY (in this process). History: win-v1.0.38 thought "the WinUI GUI
        // can't spawn ionCube-capable workers" and delegated to a child bhserve.exe (__spawn-php). The
        // real cause was a stripped Path/SystemRoot, now rebuilt in SpawnOnce — and PROVEN: the app's own
        // in-process spawn produces php that loads ionCube. Meanwhile the child-bhserve.exe path proved
        // UNRELIABLE when launched by the WinUI app at boot (it hung / silently did nothing). So no child
        // helper — spawn here, whether we're the GUI app or the console CLI.
        return SpawnWorker(version);
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
        // Just spawn — FAST, no nested verify/retry (that used to block this call for up to ~2 min per
        // version and made Start("all") crawl, delaying nginx). ionCube verification + respawn-until-warm
        // is owned entirely by the out-of-band heal loop (Engine.PhpHealUntilHealthy). Breadcrumb every
        // spawn so a boot with "nothing logged" is impossible.
        var ok = SpawnOnce(version);
        Heal($"spawn php {version}: {(ok ? "started" : "FAILED — php missing?")}");
        return ok;
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
                if (r is null)
                {
                    // Listener not answering. If it's been unreachable from the start, don't burn
                    // 16×2s connect-timeouts — bail as "not healthy" so the loop respawns and re-checks.
                    if (reached == 0 && i >= 1) return false;
                    System.Threading.Thread.Sleep(120); continue;
                }
                reached++;
                if (!r.Contains("ICOK")) return false;             // a child without ionCube ⇒ not healthy
            }
            return reached > 0;                                     // every reached child had ionCube
        }
        catch { return false; }                                    // treat as not-verified ⇒ the loop retries
    }

    /// <summary>Respawn one version DIRECTLY (Stop + SpawnOnce) — no GUI-delegation, no nested
    /// verify/retry, so a heal pass stays fast. The caller (the heal loop, run in a console helper)
    /// re-verifies on the next pass. php-cgi is a child of whatever runs the loop.</summary>
    public static void HealOnce(string version)
    {
        if (!IonCubeConfigured(version)) return;
        Stop(version);
        System.Threading.Thread.Sleep(300);   // let the port free before rebinding
        SpawnOnce(version);
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
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {msg}{Environment.NewLine}";
        var path = Path.Combine(Paths.Logs, "php-heal.log");
        // Open with FileShare.ReadWrite so CONCURRENT writers (the app breadcrumb + the helper's START
        // line fire microseconds apart at boot) don't lose lines to a sharing violation — File.AppendAllText
        // uses FileShare.Read, which is exactly why boot-time lines vanished. Retry longer to ride out a
        // transient AV lock during the login storm. A lost audit line once cost a whole reboot cycle.
        for (var t = 0; t < 8; t++)
        {
            try
            {
                Directory.CreateDirectory(Paths.Logs);
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) { System.Threading.Thread.Sleep(80); }
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
            // ⚠️ THE BOOT-FREEZE FIX (win-v1.0.50): reading p.MainModule.FileName opens each process
            // and walks its module list — at COLD BOOT, with ~78 php-cgi processes churning under heavy
            // I/O contention, that call is very slow and can HANG. It wedged the heal loop before it made
            // any progress (CPU burned then frozen, holding the mutex, ionCube never healed) — the real
            // cause of the "still no ionCube after reboot" reports. So: TIME-BOUND every module read AND
            // cap the whole orphan scan. A read that doesn't answer fast is skipped (leftovers get caught
            // on the next heal pass, when the box is warmer and the read is instant). The tracked-pid
            // Kill above already frees the port in the common case; this scan only mops up stale orphans.
            var deadline = DateTime.UtcNow.AddSeconds(3);
            foreach (var p in Process.GetProcessesByName("php-cgi"))
            {
                try
                {
                    if (DateTime.UtcNow > deadline) { p.Dispose(); continue; }   // stop scanning — never wedge
                    var path = SafeMainModulePath(p, 400);
                    if (path is not null && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                        p.Kill(true);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        try { File.Delete(RunFile(version)); } catch { }
    }

    /// <summary>Read a process's main-module path with a hard timeout. MainModule can be very slow or
    /// hang for a process that is starting/exiting or under boot-time contention; a bounded read means a
    /// single bad process can never wedge Stop() (and thus the heal loop). Returns null on timeout/error.</summary>
    private static string? SafeMainModulePath(Process p, int timeoutMs)
    {
        try
        {
            var t = System.Threading.Tasks.Task.Run(() =>
            {
                try { return p.MainModule?.FileName; } catch { return null; }
            });
            return t.Wait(timeoutMs) ? t.Result : null;
        }
        catch { return null; }
    }
}
