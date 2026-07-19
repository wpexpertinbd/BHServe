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

        // One-time cleanup: remove the old BHServeHeal scheduled task from 1.0.44–46 (it caused a
        // visible CMD popup at login). No new task is created — see below for the real fix.
        System.Threading.Tasks.Task.Run(() =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = "schtasks.exe", Arguments = "/Delete /F /TN BHServeHeal",
                  UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(10000); } catch { }
        });

        // Bring services up on launch, then GUARANTEE ionCube. THE reboot fix: on a cold boot the
        // ionCube Loader's VC-runtime dependency fails to resolve for php-cgi's workers during the
        // first minutes of the session (PROVEN: a spawn at +75s comes up without ionCube, the same
        // spawn minutes later comes up WITH it) — and those failed workers never self-correct. We
        // can't reliably predict the "warm enough" moment, so instead of a fixed delay we keep
        // re-checking and respawning php until ionCube actually loads. nginx/DB come up right away
        // (only a small settle when autostarted); the heal loop is fully in-process — no console
        // window, no scheduled task — and no-ops the instant every version reports ionCube.
        if (BHServe.Core.Config.Load().StartServicesOnLaunch)
            System.Threading.Tasks.Task.Run(async () =>
            {
                if (startInTray) await System.Threading.Tasks.Task.Delay(15_000);   // brief settle after login
                try { BHServe.App.Services.EngineHost.Instance.Engine.Start("all"); } catch { }
                try { BHServe.App.Services.EngineHost.Instance.Engine.PhpHealUntilHealthy(); } catch { }
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
