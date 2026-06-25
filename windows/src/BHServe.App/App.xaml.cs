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
        Window.Activate();
        // Launched with --tray (autostart at login) → start minimized (tray icon TBD).
        var startMinimized = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (startMinimized && Window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            p.Minimize();
    }
}
