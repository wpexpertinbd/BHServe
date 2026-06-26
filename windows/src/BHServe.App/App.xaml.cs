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

        // Optionally bring all services up on launch (Settings → Start services when BHServe launches).
        if (BHServe.Core.Config.Load().StartServicesOnLaunch)
            System.Threading.Tasks.Task.Run(() =>
            {
                try { BHServe.App.Services.EngineHost.Instance.Engine.Start("all"); } catch { }
            });
    }
}
