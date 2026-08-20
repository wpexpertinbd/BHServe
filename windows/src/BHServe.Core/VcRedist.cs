using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;

namespace BHServe.Core;

/// <summary>
/// The Microsoft Visual C++ runtime that every php.net Windows build links against.
///
/// PHP for Windows (VS16/VS17 builds) needs <c>vcruntime140.dll</c>, <c>vcruntime140_1.dll</c>
/// and <c>msvcp140.dll</c>. A clean Windows that has never had Visual Studio — or an app that
/// bundled the redistributable — does NOT ship them. BHServe downloads a portable PHP, so PHP
/// "installs" fine and then cannot start: Windows shows a modal
/// <c>php-cgi.exe - System Error: The code execution cannot proceed because VCRUNTIME140.dll
/// was not found</c>, once per spawn attempt, and every site 502s.
///
/// So: detect it, tell the user plainly, and offer to install Microsoft's official
/// redistributable. (XAMPP makes the user do this by hand; we shouldn't.)
/// </summary>
public static class VcRedist
{
    /// The x64 redistributable covers every PHP we ship: 7.4 (VC15) through 8.5 (VS17).
    /// Official Microsoft permalink, documented on learn.microsoft.com. HTTPS only.
    private const string RedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe";

    /// vcruntime140_1.dll is x64-only and absent from the older 2015 redist — check it too,
    /// or a machine with just the ancient runtime looks "installed" and PHP 8.x still fails.
    private static readonly string[] Required = { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll" };

    /// <summary>True when the VC runtime is available to a PHP build in <paramref name="phpDir"/>.
    /// Checks app-local first (Microsoft explicitly supports shipping these DLLs beside the exe),
    /// then the system directory.</summary>
    public static bool Installed(string? phpDir, out string missing)
    {
        var sys = Environment.SystemDirectory;
        var gone = Required
            .Where(d => !(phpDir is not null && File.Exists(Path.Combine(phpDir, d)))
                     && !File.Exists(Path.Combine(sys, d)))
            .ToList();
        missing = string.Join(", ", gone);
        return gone.Count == 0;
    }

    public static bool Installed() => Installed(null, out _);
    /// <summary>The one DLL whose absence makes php-cgi.exe fail in the OS loader (it is what the
    /// modal error names). Used for the BLOCKING guard, deliberately narrower than Installed():
    /// refusing to start PHP on a machine where it would actually run would be a worse bug than the
    /// one we are fixing, so we only block on the hard dependency and merely advise about the rest.</summary>
    public static bool CanRunPhp(string? phpDir)
    {
        const string core = "vcruntime140.dll";
        return (phpDir is not null && File.Exists(Path.Combine(phpDir, core)))
            || File.Exists(Path.Combine(Environment.SystemDirectory, core));
    }


    /// <summary>One-line, actionable guidance — used by install, doctor and the php-cgi heal log.</summary>
    public static string Guidance(string missing) =>
        $"PHP can't start: the Microsoft Visual C++ runtime is missing ({missing}). " +
        $"Install it once from {RedistUrl} (or run: bhserve install vcredist), then start PHP again.";

    /// <summary>Download Microsoft's official redistributable and run it silently (UAC-elevated).
    /// Returns true when the runtime is present afterwards.</summary>
    public static bool Install(Action<string>? log = null)
    {
        void Say(string m) => log?.Invoke(m);
        if (Installed()) { Say("Visual C++ runtime already installed"); return true; }

        string exe;
        try
        {
            Say("downloading the Microsoft Visual C++ redistributable…");
            exe = Downloader.DownloadPublic(RedistUrl, "vc_redist.x64.exe").GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Say($"download failed: {e.Message}");
            return false;
        }

        // Supply-chain guard: we are about to run an installer as administrator, so refuse
        // anything that isn't Authenticode-signed by Microsoft (a hijacked mirror/proxy, or a
        // captive-portal HTML page saved as .exe, never gets executed).
        if (!SignedByMicrosoft(exe))
        {
            Say("refused: the downloaded installer is not signed by Microsoft — not running it");
            try { File.Delete(exe); } catch { }
            return false;
        }

        try
        {
            Say("installing (a Windows admin prompt will appear)…");
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName        = exe,
                Arguments       = "/install /quiet /norestart",
                UseShellExecute = true,     // required for the "runas" verb (UAC)
                Verb            = "runas",
            });
            p?.WaitForExit();
            // 0 = installed, 3010 = installed + reboot pending, 1638 = a newer version is present.
            var rc = p?.ExitCode ?? -1;
            if (rc is not (0 or 3010 or 1638)) { Say($"installer exited with code {rc}"); }
        }
        catch (Exception e)
        {
            // Most common: the user dismissed the UAC prompt.
            Say($"install did not complete: {e.Message}");
        }
        finally { try { File.Delete(exe); } catch { } }

        if (Installed()) { Say("Visual C++ runtime installed"); return true; }
        Say("still missing — install it manually from " + RedistUrl);
        return false;
    }

    private static bool SignedByMicrosoft(string path)
    {
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            return cert.Subject.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // unsigned / unreadable / not a PE file
    }
}
