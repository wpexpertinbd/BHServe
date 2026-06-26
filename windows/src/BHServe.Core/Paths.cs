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
            : System.IO.Path.Combine(LocalAppData(), "BHServe");

    /// <summary>Resolve %LOCALAPPDATA% robustly. <c>GetFolderPath(LocalApplicationData)</c> calls
    /// SHGetKnownFolderPath, which on Windows can return the profile ROOT (…\Users\name) or an empty
    /// string when the app is launched at login / via shell activation before the user profile is
    /// fully ready. That made the autostart instance resolve <see cref="Home"/> to a wrong, near-empty
    /// data dir (the "1 site" dashboard). The LOCALAPPDATA env var is reliably set for the user session,
    /// so prefer it; fall back to the known-folder API only if it looks like a real AppData\Local path,
    /// then build from USERPROFILE as a last resort. Never returns "" (which would make the path relative).</summary>
    private static string LocalAppData()
    {
        var env = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var known = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localTail = System.IO.Path.Combine("AppData", "Local");
        if (!string.IsNullOrWhiteSpace(known) && known.EndsWith(localTail, StringComparison.OrdinalIgnoreCase))
            return known;

        var profile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrWhiteSpace(profile))
            profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            return System.IO.Path.Combine(profile, "AppData", "Local");

        // Truly degenerate environment — fall back to the known value (even if imperfect) so Home is never relative.
        return string.IsNullOrWhiteSpace(known) ? System.IO.Path.Combine("C:\\", "BHServe-data") : known;
    }

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
