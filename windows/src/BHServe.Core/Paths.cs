namespace BHServe.Core;

/// <summary>
/// Filesystem layout for BHServe on Windows — the analog of the mac engine's
/// <c>~/.bhserve</c> root. Everything lives under <c>%LOCALAPPDATA%\BHServe</c>.
/// </summary>
public static class Paths
{
    /// <summary>Root data dir, overridable via the BHSERVE_HOME env var (mirrors the mac engine).</summary>
    public static string Home =>
        Environment.GetEnvironmentVariable("BHSERVE_HOME") is { Length: > 0 } h
            ? h
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BHServe");

    public static string Config     => Sub("config");
    public static string Bin        => Sub("bin");          // downloaded portable php/nginx/mariadb/...
    public static string NginxSites => Sub("nginx", "sites");
    public static string Run        => Sub("run");          // pid/port json per service
    public static string Logs       => Sub("logs");
    public static string Sites      => Sub("sites");        // default web roots
    public static string Certs      => Sub("certs");        // mkcert output
    public static string Tmp        => Sub("tmp");

    public static string ConfigJson => System.IO.Path.Combine(Config, "bhserve.json");

    /// <summary>The Windows hosts file (writing it requires elevation).</summary>
    public static string HostsFile =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");

    private static string Sub(params string[] parts) =>
        System.IO.Path.Combine(new[] { Home }.Concat(parts).ToArray());

    /// <summary>Create the full directory skeleton (idempotent) — the `init` step.</summary>
    public static void EnsureSkeleton()
    {
        foreach (var d in new[] { Config, Bin, NginxSites, Run, Logs, Sites, Certs, Tmp })
            System.IO.Directory.CreateDirectory(d);
    }
}
