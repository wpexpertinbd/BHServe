using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace BHServe.Core;

/// <summary>
/// Manages BHServe's own MySQL/MariaDB server on Windows — a fresh data directory
/// under <c>%LOCALAPPDATA%\BHServe\data</c>, started as a detached <c>mysqld.exe</c>
/// on TCP 3306 with a passwordless <c>root</c> (the universal local-dev convention).
/// The mac engine leans on <c>brew services</c> + a unix socket; Windows has neither,
/// so we own the lifecycle.
/// </summary>
public static class DbServer
{
    public const int Port = 3306;
    private static string DataDir => Path.Combine(Paths.Home, "data");
    private static string SystemSchemaDir => Path.Combine(DataDir, "mysql");
    private static string RunFile => Path.Combine(Paths.Run, "mysqld.json");

    public static bool Initialized => Directory.Exists(SystemSchemaDir);

    public static bool Running()
    {
        // Authoritative check: can we open a TCP connection to the port?
        try
        {
            using var c = new TcpClient();
            var ok = c.ConnectAsync("127.0.0.1", Port).Wait(600);
            return ok && c.Connected;
        }
        catch { return false; }
    }

    private static (int code, string output) RunWait(string exe, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, Arguments = args,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp);
    }

    /// <summary>Create the data dir on first use (MySQL 8 --initialize-insecure → passwordless root).</summary>
    public static (bool ok, string msg) EnsureInitialized()
    {
        if (Initialized) return (true, "data dir ready");
        var mysqld = Tools.MysqldExe();
        if (mysqld is null) return (false, "mysqld not found — install MySQL/MariaDB");

        Directory.CreateDirectory(DataDir);
        if (Directory.EnumerateFileSystemEntries(DataDir).Any())
            return (false, $"data dir not empty and not initialized: {DataDir}");

        // MySQL 8.x path. (MariaDB builds would use mysql_install_db instead — detectable later.)
        var (code, outp) = RunWait(mysqld,
            $"--initialize-insecure --datadir=\"{DataDir}\" --console");
        if (!Initialized)
            return (false, "DB initialize failed:\n" + outp);
        return (true, "initialized fresh data dir (root has no password)");
    }

    public static (bool ok, string msg) Start()
    {
        if (Running()) return (true, "database already running");
        var init = EnsureInitialized();
        if (!init.ok) return init;
        var mysqld = Tools.MysqldExe()!;

        var psi = new ProcessStartInfo
        {
            FileName = mysqld,
            // Security: --bind-address=127.0.0.1 (never expose the DB to the LAN).
            // Capacity: --max-allowed-packet=1G (big SQL imports).
            // Performance (dev-tuned): a 256M InnoDB buffer pool keeps WordPress's working set in
            //   RAM, and flush-log-at-trx-commit=2 avoids an fsync per commit (safe enough for local
            //   dev) — together a big speedup for query-heavy pages.
            Arguments = $"--datadir=\"{DataDir}\" --bind-address=127.0.0.1 --max-allowed-packet=1024M " +
                        $"--innodb-buffer-pool-size=256M --innodb-flush-log-at-trx-commit=2 --port={Port} --mysqlx=0",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(mysqld)!,
        };
        var proc = Process.Start(psi);
        if (proc is null) return (false, "failed to spawn mysqld");
        Directory.CreateDirectory(Paths.Run);
        File.WriteAllText(RunFile, JsonSerializer.Serialize(new { pid = proc.Id, port = Port }));

        // Wait for the port to accept connections (init of a cold server can take a few s).
        for (var i = 0; i < 30; i++)
        {
            if (Running()) return (true, $"database started on :{Port} (root · no password)");
            System.Threading.Thread.Sleep(500);
        }
        return (false, "mysqld started but port never opened (see data\\*.err)");
    }

    public static void Stop()
    {
        var admin = Tools.MysqlClientExe() is { } cli
            ? Path.Combine(Path.GetDirectoryName(cli)!, "mysqladmin.exe") : null;
        if (admin is not null && File.Exists(admin) && Running())
            RunWait(admin, $"-u root -h 127.0.0.1 -P {Port} --connect-timeout=5 shutdown");

        try
        {
            if (File.Exists(RunFile))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(RunFile));
                var pid = doc.RootElement.GetProperty("pid").GetInt32();
                if (Running()) { /* graceful shutdown above handled it */ }
                else Process.GetProcessById(pid).Kill(true);
            }
        }
        catch { }
        try { File.Delete(RunFile); } catch { }
    }
}
