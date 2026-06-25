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

    /// <summary>True if a dedicated user named after the database exists.</summary>
    public static bool HasUser(string name)
    {
        if (!DbServer.Running()) return false;
        var (code, outp) = Mysql($"-N -e \"SELECT COUNT(*) FROM mysql.user WHERE user='{Esc(name)}';\"");
        return code == 0 && int.TryParse(outp.Trim(), out var n) && n > 0;
    }

    /// <summary>Create the database; if <paramref name="password"/> is non-empty, also create a
    /// dedicated user named after the DB (@localhost + @127.0.0.1) with all privileges on it.</summary>
    public static string Create(string name, string password)
    {
        Create(name);   // the no-user create (validates + creates the schema)
        if (string.IsNullOrEmpty(password)) return name;
        SetPassword(name, password);
        return name;
    }

    /// <summary>Create-or-update the dedicated user for a database and grant it full access.</summary>
    public static void SetPassword(string name, string password)
    {
        ValidName(name);
        if (!DbServer.Running()) throw new BhException("database server not running — bhserve start mariadb");
        var p = Esc(password);
        var sql =
            $"CREATE USER IF NOT EXISTS '{name}'@'localhost' IDENTIFIED BY '{p}';" +
            $"CREATE USER IF NOT EXISTS '{name}'@'127.0.0.1' IDENTIFIED BY '{p}';" +
            $"ALTER USER '{name}'@'localhost' IDENTIFIED BY '{p}';" +
            $"ALTER USER '{name}'@'127.0.0.1' IDENTIFIED BY '{p}';" +
            $"GRANT ALL PRIVILEGES ON `{name}`.* TO '{name}'@'localhost';" +
            $"GRANT ALL PRIVILEGES ON `{name}`.* TO '{name}'@'127.0.0.1';" +
            "FLUSH PRIVILEGES;";
        var (code, outp) = Mysql($"-e \"{sql}\"");
        if (code != 0) throw new BhException("set password failed:\n" + outp);
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\"", "\\\"");
}
