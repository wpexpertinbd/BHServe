using System.Diagnostics;

namespace BHServe.Core;

/// <summary>
/// Add BHServe's folders to Windows Defender's exclusion list so the antivirus doesn't quarantine the
/// server binaries BHServe downloads (PHP/nginx/MariaDB/redis/memcached…). Defender-only — there's no
/// standard API for third-party AVs (ESET/Avast/…), which the README documents for manual setup.
/// </summary>
public static class WindowsDefender
{
    /// <summary>Add folders to Defender's exclusions via an ELEVATED PowerShell (Add-MpPreference needs
    /// admin). Returns ok=false if the user declines the UAC prompt, or it couldn't be applied (Tamper
    /// Protection on, or a third-party AV is primary). Verified by reading the exclusions back.</summary>
    public static (bool ok, string msg) AddExclusions(params string[] paths)
    {
        var clean = paths.Where(p => !string.IsNullOrWhiteSpace(p))
                         .Select(p => p.TrimEnd('\\', '/'))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (clean.Length == 0) return (false, "no paths");

        var arg = string.Join(",", clean.Select(p => "'" + p.Replace("'", "''") + "'"));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command " +
                        $"\"Add-MpPreference -ExclusionPath {arg} -ErrorAction Stop\"",
            UseShellExecute = true,   // required for the runas verb (UAC)
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        int exit;
        try
        {
            var p = Process.Start(psi);
            if (p is null) return (false, "could not start PowerShell");
            if (!p.WaitForExit(30000)) return (false, "timed out");
            try { exit = p.ExitCode; } catch { exit = -1; }
        }
        catch (Exception ex) { return (false, ex.Message); }   // 1223 = UAC declined, etc.

        // `Add-MpPreference -ErrorAction Stop` exits non-zero if it was blocked (Tamper Protection) or
        // Defender isn't the active AV; exit 0 = applied. Read-only Get-MpPreference is only a fallback
        // (it can itself be unavailable on some setups, so it must not be the primary success signal).
        if (exit == 0) return (true, "added " + clean.Length + " folder(s) to Windows Defender exclusions");
        return AllExcluded(clean)
            ? (true, "added to Windows Defender exclusions")
            : (false, "exclusion didn't apply — Tamper Protection may be on, or another antivirus is active");
    }

    /// <summary>True if every given path is currently in Defender's exclusion list.</summary>
    public static bool AllExcluded(IEnumerable<string> paths)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"(Get-MpPreference).ExclusionPath\"",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
            };
            var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            var have = output.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimEnd('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return paths.All(want => have.Contains(want.TrimEnd('\\', '/')));
        }
        catch { return false; }
    }
}
