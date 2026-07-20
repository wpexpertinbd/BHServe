using System.Diagnostics;
using System.Runtime.InteropServices;
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
        var f = RunFile(version);
        // ⚠️ Share-tolerant read. The dashboard's 2s status refresh reads EVERY runfile via this method;
        // if it opens php-<v>.json without allowing concurrent write/DELETE, a simultaneous Stop()/spawn
        // (e.g. the "Enable ionCube" button respawning) fails with "being used by another process" — which
        // made the respawn fail and ionCube report ✗ while the app was running (fine with the app stopped).
        for (var t = 0; t < 3; t++)
        {
            try
            {
                if (!File.Exists(f)) return null;
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                return JsonSerializer.Deserialize<PhpRun>(sr.ReadToEnd());
            }
            catch (IOException) { System.Threading.Thread.Sleep(25); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Write a version's runfile, tolerating concurrent readers (the 2s status refresh) and
    /// retrying on a transient lock so a spawn's bookkeeping can't be lost to a race.</summary>
    private static void WriteRunFile(string version, PhpRun run)
    {
        var f = RunFile(version);
        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(run));
        for (var t = 0; t < 8; t++)
        {
            try
            {
                Directory.CreateDirectory(Paths.Run);
                using var fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes, 0, bytes.Length);
                return;
            }
            catch (IOException) { System.Threading.Thread.Sleep(40); }
            catch { return; }
        }
    }

    /// <summary>Delete a version's runfile, retrying past a transient reader lock.</summary>
    private static void DeleteRunFile(string version)
    {
        var f = RunFile(version);
        for (var t = 0; t < 8; t++)
        {
            try { if (File.Exists(f)) File.Delete(f); return; }
            catch (IOException) { System.Threading.Thread.Sleep(40); }
            catch { return; }
        }
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
        try
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
        catch (Exception e) { Heal($"spawn php {version}: EXCEPTION {e.GetType().Name}: {e.Message}"); return false; }
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

    /// <summary>Public check: is an ionCube zend_extension configured for this version?</summary>
    public static bool HasIonCube(string version) => IonCubeConfigured(version);

    /// <summary>⭐ If ionCube is CONFIGURED but the loader DLL file the ini points at does NOT exist,
    /// return that missing path (else null). THE final root cause of the "ionCube after reboot" saga:
    /// php.ini referenced a loader DLL that wasn't on the real filesystem (here: it lived only inside
    /// an MSIX package's virtualized AppData store), so every spawn printed "Failed loading …" and no
    /// amount of respawning could ever fix it. A missing FILE needs a re-install (download), not a
    /// respawn — Engine.EnableIonCube uses this to re-run the loader install instead of spinning.</summary>
    public static string? MissingIonCubeDll(string version)
    {
        try
        {
            var exe = Tools.PhpCgiExe(version);
            if (exe is null) return null;
            var files = new List<string>();
            var ini = Path.Combine(Path.GetDirectoryName(exe)!, "php.ini");
            if (File.Exists(ini)) files.Add(ini);
            var confd = ConfDir(version);
            if (Directory.Exists(confd)) files.AddRange(Directory.EnumerateFiles(confd, "*.ini"));
            foreach (var f in files)
                foreach (Match m in Regex.Matches(File.ReadAllText(f),
                             @"(?im)^\s*zend_extension\s*=\s*""?([^""\r\n;]*ioncube[^""\r\n;]*?)""?\s*$"))
                {
                    var dll = m.Groups[1].Value.Trim().Replace('/', '\\');
                    if (dll.Length > 0 && Path.IsPathRooted(dll) && !File.Exists(dll)) return dll;
                }
        }
        catch { }
        return null;
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
        EnsureCaBundle();
        EnsureLimits(Path.GetDirectoryName(exe)!);

        var port = PortFor(version);
        var phpDir = Path.GetDirectoryName(exe)!;
        var confd = ConfDir(version);
        Directory.CreateDirectory(confd);
        var sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);   // C:\Windows\System32
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);  // C:\Windows

        // Clean, WHITELISTED environment — do NOT inherit the app's full process env. Hygiene, not the
        // ionCube fix (that turned out to be a missing loader DLL — see MissingIonCubeDll): the app's env
        // carries injected vars (AV hooks, WindowsAppSDK MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY, …)
        // that long-lived php workers have no business seeing, and a stripped/stale Path or SystemRoot
        // from a bad parent has bitten us before. Copy ONLY the standard Windows + PHP vars a worker
        // actually needs; rebuild the essentials explicitly below.
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Keep(string k) { var v = Environment.GetEnvironmentVariable(k); if (!string.IsNullOrEmpty(v)) env[k] = v; }
        foreach (var k in new[]
        {
            "SystemDrive", "ComSpec", "PATHEXT",
            "TEMP", "TMP", "USERPROFILE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH", "LOGONSERVER",
            "APPDATA", "LOCALAPPDATA", "ALLUSERSPROFILE", "ProgramData",
            "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
            "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
            "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
            "COMPUTERNAME", "OS",
        }) Keep(k);
        // Explicit, clean essentials (never taken from the possibly-polluted inherited values).
        env["SystemRoot"]            = winDir;
        env["windir"]                = winDir;
        env["Path"]                  = string.Join(";", new[] { phpDir, sysDir, Path.Combine(sysDir, "Wbem"), winDir }
                                                     .Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase));
        env["PHP_FCGI_MAX_REQUESTS"] = "0";                 // never recycle the listener
        env["PHP_FCGI_CHILDREN"]     = "12";                // worker pool → concurrency (no 502 under multi-site load)
        env["PHP_INI_SCAN_DIR"]      = ";" + confd;         // load our per-version conf.d on top of php.ini

        // Spawn windowless via raw CreateProcess: no console, no window, NO inherited handles and no
        // redirected pipes (a parent-held pipe once hung Stop/spawn paths). Not the ionCube fix — that
        // was a missing loader DLL (see MissingIonCubeDll) — but the clean way to run php-cgi from
        // either the GUI app or the CLI with zero popups.
        var pid = SpawnHiddenConsole(exe, $"-b 127.0.0.1:{port}", env, phpDir);
        if (pid <= 0) return false;
        WriteRunFile(version, new PhpRun(version, port, pid));   // share-tolerant + retried
        return true;
    }

    // ── Win32 CreateProcess, no console window, no pipes, no inherited handles ───────────────────────
    private const uint CREATE_NO_WINDOW = 0x08000000;   // run console app WITHOUT a console (no window, no new console alloc)
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int  STARTF_USESHOWWINDOW = 0x00000001;
    private const short SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(
        string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Spawn a background program with its OWN real console, HIDDEN (like RunHiddenConsole).
    /// Returns the PID or -1. No inherited handles; the process has its own console for stdio.</summary>
    private static int SpawnHiddenConsole(string exe, string args, IDictionary<string, string> env, string workingDir)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), dwFlags = STARTF_USESHOWWINDOW, wShowWindow = SW_HIDE };
        var sb = new System.Text.StringBuilder();
        foreach (var kv in env.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        sb.Append('\0');
        var envPtr = Marshal.StringToHGlobalUni(sb.ToString());
        try
        {
            var ok = CreateProcess(null, $"\"{exe}\" {args}", IntPtr.Zero, IntPtr.Zero, false,
                CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, envPtr, workingDir, ref si, out var pi);
            if (!ok) { Heal($"CreateProcess(php-cgi) failed: Win32 err {Marshal.GetLastWin32Error()}"); return -1; }
            var pid = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return pid;
        }
        catch (Exception e) { Heal($"SpawnHiddenConsole exception: {e.GetType().Name}: {e.Message}"); return -1; }
        finally { Marshal.FreeHGlobal(envPtr); }
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
        // JIT stays OFF: tracing JIT on Windows php-cgi crashes workers (0xc0000005 mid-request → 502)
        // under real apps (PHP 8.4 + Filament/Livewire, 2026-07-20). It only ever ran on versions
        // without ionCube anyway (the loader force-disables JIT), and its gain for web apps is minor —
        // opcache itself is the real win. Stability > JIT.
        ("opcache.jit",                    "disable"),
        ("opcache.jit_buffer_size",        "0"),
    };

    /// <summary>Path of the shared Mozilla CA bundle used by every PHP build's curl/openssl.</summary>
    private static string CaBundlePath => Path.Combine(Paths.Bin, "cacert.pem");

    /// <summary>Windows PHP ships with NO CA bundle, so every PHP curl/openssl HTTPS call that
    /// verifies certificates fails with "unable to get local issuer certificate" (curl error 60) —
    /// e.g. the WHMCS/Blesta license phone-home, payment gateways, any API SDK. Laragon and XAMPP
    /// both ship a cacert.pem and point php.ini at it; so do we: download the standard Mozilla
    /// bundle (curl.se) once into bin\cacert.pem, and EnsureLimits wires curl.cainfo +
    /// openssl.cafile to it. Offline at first start → silently retried on every later spawn.</summary>
    private static void EnsureCaBundle()
    {
        try
        {
            if (File.Exists(CaBundlePath)) return;
            Directory.CreateDirectory(Paths.Bin);
            using var http = new HttpClient() { Timeout = TimeSpan.FromSeconds(25) };
            var bytes = http.GetByteArrayAsync("https://curl.se/ca/cacert.pem").GetAwaiter().GetResult();
            // Sanity: the real bundle is ~200+ KB of PEM blocks — never install an error page.
            if (bytes.Length > 100_000 &&
                System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096))
                      .Contains("##") &&
                System.Text.Encoding.ASCII.GetString(bytes).Contains("BEGIN CERTIFICATE"))
                File.WriteAllBytes(CaBundlePath, bytes);
        }
        catch { }   // no network yet → PHP still runs; picked up on a later spawn
    }

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
            // Point curl + openssl at the shared CA bundle (see EnsureCaBundle) so PHP HTTPS calls
            // verify certificates. Only when the bundle actually exists — never point at a void.
            if (File.Exists(CaBundlePath))
            {
                var ca = CaBundlePath.Replace('\\', '/');
                foreach (var key in new[] { "curl.cainfo", "openssl.cafile" })
                {
                    var rx = new Regex($@"(?m)^[ \t]*;?[ \t]*{Regex.Escape(key)}[ \t]*=.*$");
                    var line = $"{key} = \"{ca}\"";
                    text = rx.IsMatch(text) ? rx.Replace(text, line, 1) : text.TrimEnd() + $"\n{line}\n";
                }
            }
            // Pin sessions/uploads/temp to a guaranteed-writable BHServe dir. Unset, PHP falls back
            // to the process TEMP env — which can be missing for a service-context worker (then
            // GetTempPath returns C:\Windows, NOT user-writable → "session storage is not writeable").
            // Sites imported from Linux servers also carry .user.ini `session.save_path=/tmp` — that
            // still overrides per-site, but at least the php.ini default is always a real, writable dir.
            var sess = Path.Combine(Paths.Tmp, "php-sessions");
            Directory.CreateDirectory(sess);
            foreach (var (key, val) in new[]
            {
                ("session.save_path", sess),
                ("upload_tmp_dir",    Paths.Tmp),
                ("sys_temp_dir",      Paths.Tmp),
            })
            {
                var rx = new Regex($@"(?m)^[ \t]*;?[ \t]*{Regex.Escape(key)}[ \t]*=.*$");
                var line = $"{key} = \"{val.Replace('\\', '/')}\"";
                text = rx.IsMatch(text) ? rx.Replace(text, line, 1) : text.TrimEnd() + $"\n{line}\n";
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
        DeleteRunFile(version);   // share-tolerant + retried (the 2s status refresh may be reading it)
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
