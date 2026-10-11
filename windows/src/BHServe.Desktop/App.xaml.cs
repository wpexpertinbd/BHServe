using System;
using System.Linq;
using Microsoft.UI.Xaml;

namespace BHServe.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }

    public App() => InitializeComponent();

    /// <summary>Windows is ending the session: stop EVERY BHServe process so nothing blocks shutdown.
    /// Windows only allows a few seconds here, so PHP (the process users actually see listed)
    /// goes first, then the rest — MariaDB via the normal stop path so it closes its files cleanly.</summary>
    private static void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        BHServe.Core.ShutdownGuard.StopAllForShutdown();
    }


    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Window = new MainWindow();
        // Stop our services when Windows shuts down / restarts / logs off.
        // Without this, the php-cgi.exe workers (and nginx/MariaDB) are still running when the
        // session ends, so Windows puts up its "this app is preventing you from shutting down"
        // screen naming php-cgi.exe — clicking OK does nothing because nobody ever asks the
        // processes to exit, and the user has to force the shutdown. Linux/macOS never showed this
        // because systemd/launchd signal the services on session end; on Windows nothing did.
        // NOTE: this is deliberately tied to SESSION END only — quitting the GUI leaves the servers
        // running on purpose (BHServe keeps serving from the tray / after the window is closed).
        try { Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding; } catch { }

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

        // One-time cleanup: remove the old scheduled tasks from earlier builds — BHServeHeal
        // (1.0.44–46, caused a visible CMD popup at login) and BHServeIonRestart (1.0.57-era
        // experiment; the real ionCube cause was a missing loader DLL, no task needed).
        System.Threading.Tasks.Task.Run(() =>
        {
            foreach (var tn in new[] { "BHServeHeal", "BHServeIonRestart" })
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    { FileName = "schtasks.exe", Arguments = $"/Delete /F /TN {tn}",
                      UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(10000); } catch { }
        });

        // Bring services up on launch (fast reachability), then — once the boot storm settles — verify
        // ionCube actually loaded in the workers and heal if not (re-installs the loader DLL when the
        // file itself is missing; respawns cold workers otherwise). Fully in-process.
        if (BHServe.Core.Config.Load().StartServicesOnLaunch)
            System.Threading.Tasks.Task.Run(async () =>
            {
                if (startInTray) await System.Threading.Tasks.Task.Delay(15_000);   // brief settle after login
                try { BHServe.App.Services.EngineHost.Instance.Engine.Start("all"); } catch { }

                await System.Threading.Tasks.Task.Delay(startInTray ? 90_000 : 5_000);
                try
                {
                    var eng = BHServe.App.Services.EngineHost.Instance.Engine;
                    if (!eng.IonCubeAllHealthy()) eng.EnableIonCube(quiet: true);
                }
                catch { }
            });
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
