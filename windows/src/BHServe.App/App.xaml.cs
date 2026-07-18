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

        // Bring services up on launch. THE ROOT-CAUSE FIX for "ionCube missing after reboot": on Windows
        // the ionCube Loader's dependency DLLs (VC runtime) fail to resolve when php-cgi is spawned
        // during the LOGIN STORM (a degraded early-session context) — the exact thing XAMPP/Laragon
        // avoid by only starting when the user manually clicks Start on a WARM desktop. So when we
        // autostart at login (--tray), we WAIT for the session to warm up before starting PHP, mirroring
        // that manual-start timing. A user-opened window means it's already warm → start immediately.
        if (BHServe.Core.Config.Load().StartServicesOnLaunch)
            System.Threading.Tasks.Task.Run(async () =>
            {
                if (startInTray) await System.Threading.Tasks.Task.Delay(75_000);   // let the login storm pass
                try { BHServe.App.Services.EngineHost.Instance.Engine.Start("all"); } catch { }
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
