using System.Diagnostics;

namespace BHServe.Core;

/// <summary>
/// Bridges the unprivileged CLI/GUI to <c>bhserve-elevate.exe</c> for the few
/// admin-only operations (hosts file, mkcert CA). Launching with verb "runas"
/// triggers a single UAC prompt — the analog of the mac engine's osascript
/// "with administrator privileges".
/// </summary>
public static class Elevation
{
    /// <summary>Locate bhserve-elevate.exe (next to the running exe, or the published layout).</summary>
    public static string? HelperPath()
    {
        var dir = AppContext.BaseDirectory;
        foreach (var name in new[] { "bhserve-elevate.exe", "BHServe.Elevate.exe" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Run an elevated verb (e.g. "hosts-add foo.test"). Returns true on success
    /// (helper exit 0). Pops one UAC prompt; if the user cancels, returns false (no throw).</summary>
    public static bool Run(string verb, params string[] args)
    {
        var helper = HelperPath();
        if (helper is null) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = true,   // required for the "runas" verb (UAC)
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add(verb);
            foreach (var a in args) psi.ArgumentList.Add(a);
            var p = Process.Start(psi);
            if (p is null) return false;
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; }   // user cancelled UAC, or helper missing
    }
}
