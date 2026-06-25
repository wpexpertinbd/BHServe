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
        var p = Process.Start(psi)!;
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

        if (engine == "mariadb")
        {
            var installer = Tools.MariadbInstallDbExe();
            if (installer is null) return (false, "mariadb-install-db not found");
            RunWait(installer, $"--datadir=\"{data}\" --auth-root-authentication-method=normal");
        }
        else
        {
            var mysqld = Tools.MysqldExe("mysql");
            if (mysqld is null) return (false, "mysqld not found — install MySQL");
            RunWait(mysqld, $"--initialize-insecure --datadir=\"{data}\" --console");
        }
        if (!InitializedFor(engine)) return (false, $"{engine} initialize failed");
        return (true, $"initialized fresh {engine} data dir (root has no password)");
    }

    /// <summary>Start a specific engine on :3306. Refuses if the OTHER engine already holds the port.</summary>
    public static (bool ok, string msg) Start(string engine)
    {
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
            Arguments = $"--datadir=\"{data}\" --bind-address=127.0.0.1 --max-allowed-packet=1024M " +
                        $"--innodb-buffer-pool-size=256M --innodb-flush-log-at-trx-commit=2 --port={Port}" +
                        (engine == "mariadb" ? "" : " --mysqlx=0"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(mysqld)!,
        };
        var proc = Process.Start(psi);
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
