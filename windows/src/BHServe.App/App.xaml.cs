using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace BHServe.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        // Launched with --tray (autostart at login) → run in the TRAY ONLY: never show the window
        // and keep it out of the taskbar/Alt-Tab. The old code Activate()'d then Minimize()'d, which
        // flashed the window on screen and left a taskbar button. The tray icon is the only UI until
        // the user opens it.
        var startInTray = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (startInTray) Window.StartHiddenInTray();
        else             Window.Activate();

        // Auto-repair the Windows "localhost" DB stall on imported sites (idempotent, best-effort) so
        // users don't have to touch any config — pages that felt like they loaded from a remote server
        // become instant. New BHServe sites already use 127.0.0.1.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { BHServe.Core.SiteDbHostFix.Run(BHServe.Core.Config.Load().SitesRoot); } catch { }
        });

        // Optionally bring all services up on launch (Settings → Start services when BHServe launches).
        if (BHServe.Core.Config.Load().StartServicesOnLaunch)
            System.Threading.Tasks.Task.Run(() =>
            {
                try { BHServe.App.Services.EngineHost.Instance.Engine.Start("all"); } catch { }
            });

        // Delayed php verify-and-heal passes. At COLD BOOT the spawn-time ionCube probe can time out
        // (workers too slow during the login storm) and php then serves WITHOUT ionCube. Re-verify on
        // a warm system via the console helper (spawns must never come from this GUI process).
        // FIVE retries with growing delays: at boot even LAUNCHING the helper can fail (observed: the
        // 90s+5min passes never ran at login while the identical code fired fine on a warm relaunch),
        // so each attempt is LOGGED to php-heal.log — no silent path — and later attempts run after
        // the storm has passed. Passes are cheap no-ops when healthy (mutex-guarded, probe-only).
        System.Threading.Tasks.Task.Run(async () =>
        {
            var delays = new[] { 90_000, 90_000, 120_000, 300_000, 300_000 };  // t+1.5m, 3m, 5m, 10m, 15m
            for (var i = 0; i < delays.Length; i++)
            {
                await System.Threading.Tasks.Task.Delay(delays[i]);
                LaunchHealPass($"scheduler pass {i + 1}/5");
            }
        });
    }

    /// <summary>Launch `bhserve.exe __heal-php` (console helper) and log the outcome — used by the
    /// async scheduler above AND MainWindow's redundant timer. Never throws; everything it does is
    /// visible in logs/php-heal.log.</summary>
    internal static void LaunchHealPass(string reason)
    {
        try
        {
            var cli = System.IO.Path.Combine(AppContext.BaseDirectory, "bhserve.exe");
            if (!System.IO.File.Exists(cli))
            { BHServe.Core.PhpCgi.HealLog($"app: {reason} — bhserve.exe NOT FOUND at {cli}"); return; }
            BHServe.Core.PhpCgi.HealLog($"app: {reason} — launching heal helper");
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = cli, Arguments = "__heal-php", UseShellExecute = false, CreateNoWindow = true });
            if (p is null) { BHServe.Core.PhpCgi.HealLog($"app: {reason} — Process.Start returned null"); return; }
            p.WaitForExit(300000);
            BHServe.Core.PhpCgi.HealLog($"app: {reason} — helper exit={(p.HasExited ? p.ExitCode.ToString() : "TIMEOUT")}");
        }
        catch (Exception e)
        { try { BHServe.Core.PhpCgi.HealLog($"app: {reason} — launch FAILED: {e.GetType().Name}: {e.Message}"); } catch { } }
    }

    /// <summary>Fully exit the app — including the tray — bypassing the "hide to tray on close"
    /// behavior. Used by the self-updater so the running BHServe.App.exe / Core.dll unlock and the
    /// installer can replace them (otherwise the close request just hides the window to the tray and
    /// the installer reports it couldn't close the app).</summary>
    public static void ForceQuit()
    {
        Window?.QuitForUpdate();
        Application.Current.Exit();
    }
}
