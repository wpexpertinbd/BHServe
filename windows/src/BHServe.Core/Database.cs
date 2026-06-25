using System.Diagnostics;
using System.Text.RegularExpressions;

namespace BHServe.Core;

/// <summary>
/// MySQL/MariaDB database operations via the bundled <c>mysql.exe</c> client over
/// TCP as passwordless root (the <c>cmd_db</c> analog). System schemas are hidden.
/// </summary>
public static class Database
{
    private static readonly string[] SystemSchemas = { "information_schema", "performance_schema", "mysql", "sys" };

    private static string BaseArgs => $"-u root -h 127.0.0.1 -P {DbServer.Port} --connect-timeout=5";

    private static (int code, string output) Mysql(string sqlArgs)
    {
        var cli = Tools.MysqlClientExe() ?? throw new BhException("mysql client not found");
        var psi = new ProcessStartInfo
        {
            FileName = cli, Arguments = $"{BaseArgs} {sqlArgs}",
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(cli)!,
        };
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
        if (!DbServer.Running()) return Array.Empty<string>();
        var (code, outp) = Mysql("-N -e \"SHOW DATABASES;\"");
        if (code != 0) return Array.Empty<string>();
        return outp.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Where(d => !SystemSchemas.Contains(d))
                   .ToList();
    }

    public static string Create(string name)
    {
        ValidName(name);
        if (!DbServer.Running()) throw new BhException("database server not running — bhserve start mariadb");
        var (code, outp) = Mysql(
            $"-e \"CREATE DATABASE IF NOT EXISTS `{name}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;\"");
        if (code != 0) throw new BhException("create failed:\n" + outp);
        return name;
    }

    public static void Drop(string name)
    {
        ValidName(name);
        if (!DbServer.Running()) throw new BhException("database server not running — bhserve start mariadb");
        var (code, outp) = Mysql($"-e \"DROP DATABASE IF EXISTS `{name}`;\"");
        if (code != 0) throw new BhException("drop failed:\n" + outp);
    }
}
