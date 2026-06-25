using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>
/// BHServe's own PostgreSQL server on Windows — a fresh data dir under
/// <c>%LOCALAPPDATA%\BHServe\pgdata</c>, initialized with <c>--auth=trust</c> (passwordless
/// local, the local-dev convention), bound to 127.0.0.1:5432 via pg_ctl.
/// </summary>
public static class PgServer
{
    public const int Port = 5432;
    private static string DataDir => Path.Combine(Paths.Home, "pgdata");
    private static string LogFile => Path.Combine(Paths.Logs, "postgresql.log");

    public static bool Initialized => File.Exists(Path.Combine(DataDir, "PG_VERSION"));

    public static bool Running()
    {
        try { using var c = new TcpClient(); return c.ConnectAsync("127.0.0.1", Port).Wait(600) && c.Connected; }
        catch { return false; }
    }

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

    public static (bool ok, string msg) EnsureInitialized()
    {
        if (Initialized) return (true, "data dir ready");
        var initdb = Tools.InitdbExe();
        if (initdb is null) return (false, "initdb not found — install PostgreSQL");
        Directory.CreateDirectory(DataDir);
        if (Directory.EnumerateFileSystemEntries(DataDir).Any())
            return (false, $"data dir not empty and not initialized: {DataDir}");
        // -U postgres superuser, trust auth (passwordless local), UTF8.
        var (code, outp) = RunWait(initdb, $"-U postgres -A trust --encoding=UTF8 -D \"{DataDir}\"");
        if (!Initialized) return (false, "initdb failed:\n" + outp);
        return (true, "initialized fresh PostgreSQL data dir (postgres · trust auth)");
    }

    public static (bool ok, string msg) Start()
    {
        if (Running()) return (true, "PostgreSQL already running");
        var init = EnsureInitialized();
        if (!init.ok) return init;
        var pgctl = Tools.PgCtlExe();
        if (pgctl is null) return (false, "pg_ctl not found");
        Directory.CreateDirectory(Paths.Logs);
        // -w wait for ready; bind loopback only.
        var (code, outp) = RunWait(pgctl,
            $"start -D \"{DataDir}\" -l \"{LogFile}\" -o \"-p {Port} -c listen_addresses=127.0.0.1\" -w -t 30");
        for (var i = 0; i < 20 && !Running(); i++) System.Threading.Thread.Sleep(400);
        return Running() ? (true, $"PostgreSQL started on :{Port} (postgres · trust)") : (false, "PostgreSQL failed to start:\n" + outp);
    }

    public static void Stop()
    {
        var pgctl = Tools.PgCtlExe();
        if (pgctl is not null && Running())
            RunWait(pgctl, $"stop -D \"{DataDir}\" -m fast -w -t 20");
    }
}

/// <summary>PostgreSQL database ops via psql as the passwordless <c>postgres</c> superuser.</summary>
public static class PgDatabase
{
    private static readonly string[] SystemDbs = { "postgres", "template0", "template1" };

    private static (int code, string output) Psql(string args)
    {
        var psql = Tools.PsqlExe() ?? throw new BhException("psql not found — install PostgreSQL");
        var psi = new ProcessStartInfo
        {
            FileName = psql,
            Arguments = $"-h 127.0.0.1 -p {PgServer.Port} -U postgres {args}",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(psql)!,
        };
        psi.Environment["PGPASSWORD"] = "";   // trust auth
        var p = Process.Start(psi)!;
        var outp = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, outp + err);
    }

    private static void ValidName(string name)
    {
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]+$"))
            throw new BhException($"invalid db name '{name}' (letters, digits, underscore)");
    }

    public static IReadOnlyList<string> List()
    {
        if (!PgServer.Running()) return System.Array.Empty<string>();
        var (code, outp) = Psql("-At -c \"SELECT datname FROM pg_database WHERE datistemplate = false;\"");
        if (code != 0) return System.Array.Empty<string>();
        return outp.Split('\n', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries)
                   .Where(d => !SystemDbs.Contains(d)).ToList();
    }

    public static string Create(string name)
    {
        ValidName(name);
        if (!PgServer.Running()) throw new BhException("PostgreSQL not running — bhserve start postgresql");
        var (code, outp) = Psql($"-c \"CREATE DATABASE \\\"{name}\\\";\"");
        if (code != 0) throw new BhException("create failed:\n" + outp);
        return name;
    }

    public static void Drop(string name)
    {
        ValidName(name);
        if (!PgServer.Running()) throw new BhException("PostgreSQL not running — bhserve start postgresql");
        var (code, outp) = Psql($"-c \"DROP DATABASE IF EXISTS \\\"{name}\\\";\"");
        if (code != 0) throw new BhException("drop failed:\n" + outp);
    }
}
