using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>
/// Manages BHServe's MySQL/MariaDB server on :3306. MySQL and MariaDB are separate engines
/// with SEPARATE data dirs (incompatible system tables), but only one runs on the port at a
/// time. The run-file records which engine is actually running, so status reflects reality
/// rather than which engine happens to be installed.
/// </summary>
public static class DbServer
{
    public const int Port = 3306;
    private static string RunFile => Path.Combine(Paths.Run, "mysqld.json");

    private static string DataDirFor(string engine) => Path.Combine(Paths.Home, engine == "mariadb" ? "data-mariadb" : "data");
    private static bool InitializedFor(string engine) => Directory.Exists(Path.Combine(DataDirFor(engine), "mysql"));
    private static string DefaultEngine() => Tools.MysqlInstalled ? "mysql" : Tools.MariadbInstalled ? "mariadb" : "mysql";

    public static bool Running()
    {
        try { using var c = new TcpClient(); var ok = c.ConnectAsync("127.0.0.1", Port).Wait(600); return ok && c.Connected; }
        catch { return false; }
    }

    /// <summary>The engine ACTUALLY running on :3306 (from the run-file), or null if nothing is.</summary>
    public static string? ActiveEngine()
    {
        if (!Running()) return null;
        try
        {
            if (File.Exists(RunFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(RunFile));
                if (doc.RootElement.TryGetProperty("engine", out var e) && e.GetString() is { } s) return s;
            }
        }
        catch { }
        return "mysql";   // a legacy run-file (no engine field) was the MySQL default
    }

    public static bool ActiveIsMariadb => ActiveEngine() == "mariadb";
    public static bool Initialized => InitializedFor(DefaultEngine());

    private static (int code, string output) RunWait(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        using var p = ChildProc.Start(psi)!;   // dispose: each call otherwise leaks a handle + 2 pipes
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp);
    }

    public static (bool ok, string msg) EnsureInitialized(string engine)
    {
        if (InitializedFor(engine)) return (true, "data dir ready");
        var data = DataDirFor(engine);
        Directory.CreateDirectory(data);
        if (Directory.EnumerateFileSystemEntries(data).Any())
            return (false, $"data dir not empty and not initialized: {data}");

        (int code, string output) res;
        if (engine == "mariadb")
        {
            var installer = Tools.MariadbInstallDbExe();
            if (installer is null) return (false, "mariadb-install-db not found");
            // Windows mariadb-install-db.exe uses --datadir; no password arg = passwordless root.
            // (The --auth-root-authentication-method flag is Linux-only and errors out here.)
            res = RunWait(installer, $"--datadir=\"{data}\"");
        }
        else
        {
            var mysqld = Tools.MysqldExe("mysql");
            if (mysqld is null) return (false, "mysqld not found — install MySQL");
            res = RunWait(mysqld, $"--initialize-insecure --datadir=\"{data}\" --console");
        }
        if (!InitializedFor(engine))
        {
            try { Directory.Delete(data, true); } catch { }   // leave no half-init dir to block a retry
            var why = res.output.Trim();
            return (false, $"{engine} initialize failed" + (why.Length > 0 ? $": {why[^Math.Min(why.Length, 300)..]}" : ""));
        }
        return (true, $"initialized fresh {engine} data dir (root has no password)");
    }

    /// <summary>Start a specific engine on :3306. Refuses if the OTHER engine already holds the port.</summary>
    public static (bool ok, string msg) Start(string engine)
    {
        // Windows is ending the session — never start a server into a machine that is shutting
        // down (the shutdown sweep would kill it mid-initialisation, which is what produced the
        // "unable to start correctly" dialogs). See ShutdownGuard.
        if (ShutdownGuard.IsShuttingDown) return (false, "shutting down");
        engine = engine == "mariadb" ? "mariadb" : "mysql";
        if (Running())
        {
            var cur = ActiveEngine();
            return cur == engine
                ? (true, $"{engine} already running")
                : (false, $"{cur} is already running on :{Port} — stop it before starting {engine}");
        }
        var mysqld = Tools.MysqldExe(engine);
        if (mysqld is null) return (false, $"{engine} not installed");
        var init = EnsureInitialized(engine);
        if (!init.ok) return init;
        var data = DataDirFor(engine);

        var psi = new ProcessStartInfo
        {
            FileName = mysqld,
            // bind loopback only; big packets; dev-tuned InnoDB. --mysqlx=0 is MySQL-only.
            // --enable-named-pipe: many apps use DB_HOST='localhost', which on Windows means the
            // named pipe (not TCP) — without this their connection hangs. Matches Laragon.
            Arguments = $"--datadir=\"{data}\" --bind-address=127.0.0.1 --enable-named-pipe --max-allowed-packet=1024M " +
                        $"--innodb-buffer-pool-size=256M --innodb-flush-log-at-trx-commit=2 --port={Port}" +
                        (engine == "mariadb" ? "" : " --mysqlx=0"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(mysqld)!,
        };
        var proc = ChildProc.Start(psi);
        if (proc is null) return (false, "failed to spawn mysqld");
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile, JsonSerializer.Serialize(new { pid = proc.Id, port = Port, engine }));

        for (var i = 0; i < 30; i++)
        {
            if (Running()) return (true, $"{engine} started on :{Port} (root · no password)");
            System.Threading.Thread.Sleep(500);
        }
        return (false, $"{engine} started but port never opened (see {DataDirFor(engine)}\\*.err)");
    }

    /// <summary>Parameterless start (provisioning) — brings up the default installed engine.</summary>
    public static (bool ok, string msg) Start() => Running() ? (true, "database already running") : Start(DefaultEngine());

    /// <summary>Wait until the server actually ANSWERS A QUERY — not merely until its port is open.
    /// Running() is a TCP connect to :3306, and on a MAJOR version bump (MariaDB 12.3 → 13.0) the
    /// port is bound seconds before InnoDB finishes recovery and starts accepting connections.
    /// Start() polls Running(), so it can hand back "started" while a query still fails. Mirrors the
    /// shared engine's db_wait_ready() (macOS hit exactly this: both mariadb-upgrade attempts failed
    /// on "can't connect" and the engine warned about a database that was completely fine — and the
    /// user's instinct on seeing that is to roll back something that needs nothing).</summary>
    public static bool WaitReady(int timeoutSeconds = 60)
    {
        var cli = Tools.MysqlClientExe();
        if (cli is null) return true;   // nothing to probe with — don't block the caller
        var pw = Config.Load().RootPassword;
        // WALL-CLOCK deadline, not an iteration count: each probe costs its own connect timeout plus
        // the sleep, so "60 iterations" was really ~180s of elapsed time.
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (true)
        {
            if (ProbeAnswersQuery(cli, pw)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            System.Threading.Thread.Sleep(1000);
        }
    }

    /// <summary>One readiness probe: does the server answer `SELECT 1`?
    /// Deliberately NOT routed through RunWait:
    /// - The password goes in via <c>MYSQL_PWD</c>, never argv. RunWait passes a single Arguments
    ///   STRING, so `-p{pw}` splits on whitespace — a root password of "correct horse" would send
    ///   `-pcorrect` plus a stray `horse` and could never authenticate, so this loop would burn its
    ///   whole budget and report "not ready" about a perfectly healthy server. It also keeps the
    ///   password off the child's visible command line.
    /// - The wait is BOUNDED and the child is killed if it overruns. RunWait's WaitForExit() has no
    ///   timeout, so a client that connects and then stalls would hang the caller forever.
    /// - Both pipes are drained concurrently and discarded; reading stdout fully and only then stderr
    ///   (as RunWait does) can deadlock if the child fills stderr first — and this client does write
    ///   to stderr (the passwordless-login SSL warning).</summary>
    private static bool ProbeAnswersQuery(string cli, string pw)
    {
        var psi = new ProcessStartInfo
        {
            FileName = cli,
            Arguments = $"-u root -h 127.0.0.1 -P {Port} --connect-timeout=2 -e \"SELECT 1\"",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
        };
        if (pw.Length > 0) psi.Environment["MYSQL_PWD"] = pw;
        try
        {
            using var p = ChildProc.Start(psi);
            if (p is null) return false;
            p.OutputDataReceived += static (_, _) => { };   // drain + discard, concurrently
            p.ErrorDataReceived  += static (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { } return false; }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Reconcile the data dir's system tables with the (just-installed) new engine binary —
    /// the standard post-upgrade step. MariaDB needs mariadb-upgrade; MySQL 8+/9 self-upgrades on
    /// start, so it's a no-op there. The data dir is never dumped/rebuilt — this only fixes the
    /// system schema. Returns a short status, or "" if nothing to do.</summary>
    public static string RunUpgrade(string engine)
    {
        if (engine != "mariadb" || !Running()) return "";
        var exe = Tools.MariadbUpgradeExe();
        if (exe is null) return "";
        // The port being open is not the same as the server being ready — see WaitReady. If it never
        // becomes ready we still RUN the upgrade: waiting exists to avoid a FALSE failure, not to
        // replace a real diagnosis. Returning a "not ready" string here instead would be worse than
        // the bug it fixes — Engine.Update prints ANY non-empty return through Ok(), so a wrong saved
        // root password (every probe fails identically) would have printed as a success line while the
        // system-table upgrade silently never happened. Running it surfaces mariadb-upgrade's own
        // error text, which is what we had before the wait was added.
        WaitReady();
        var pw = Config.Load().RootPassword;
        var (code, outp) = RunWait(exe, $"-u root -h 127.0.0.1 -P {Port} {(pw.Length > 0 ? $"-p{pw} " : "")}");
        return code == 0 ? "system tables upgraded (mariadb-upgrade)"
                         : $"mariadb-upgrade reported: {outp.Trim()}";
    }

    public static void Stop()
    {
        var admin = Tools.MysqlClientExe() is { } cli ? Path.Combine(Path.GetDirectoryName(cli)!, "mysqladmin.exe") : null;
        if (admin is not null && File.Exists(admin) && Running())
        {
            var pw = Config.Load().RootPassword;
            RunWait(admin, $"-u root {(pw.Length > 0 ? $"-p{pw} " : "")}-h 127.0.0.1 -P {Port} --connect-timeout=5 shutdown");
        }
        try
        {
            if (File.Exists(RunFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(RunFile));
                var pid = doc.RootElement.GetProperty("pid").GetInt32();
                System.Threading.Thread.Sleep(500);
                if (Running()) { try { Process.GetProcessById(pid).Kill(true); } catch { } }   // graceful shutdown didn't take → force it
            }
        }
        catch { }
        try { File.Delete(RunFile); } catch { }
    }
}
