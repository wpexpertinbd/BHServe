using BHServe.App.Services;
using BHServe.App.Views;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BHServe.App;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _tray;
    private bool _reallyQuit;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();

        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon)) { try { AppWindow.SetIcon(icon); } catch { } }

        _tray = new TrayIcon("BHServe — local web stack", icon);
        _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowFromTray);
        _tray.QuitRequested += () => DispatcherQueue.TryEnqueue(QuitApp);

        // Close → hide to tray when "keep running" is on (Settings); otherwise really quit.
        AppWindow.Closing += (_, e) =>
        {
            if (_reallyQuit || !Config.Load().MinimizeToTray) { _tray.Dispose(); return; }
            e.Cancel = true;
            AppWindow.Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloon("BHServe is still running",
                    "Your sites stay up in the background. Click this icon to reopen — use the ^ to show hidden icons if you don't see it. Turn this off in Settings.");
            }
        };

        if (Config.Load().AutoUpdate) _ = CheckUpdateBadge();
    }

    /// <summary>If an update is available, show an attention dot on the Settings nav item (mac sidebar badge).</summary>
    private async System.Threading.Tasks.Task CheckUpdateBadge()
    {
        try
        {
            var r = await Updater.Check();
            if (r.UpdateAvailable && Nav.SettingsItem is NavigationViewItem si)
                si.InfoBadge = new InfoBadge();   // default style = attention dot
        }
        catch { }
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Restore();
        Activate();
    }

    private void QuitApp()
    {
        _reallyQuit = true;
        _tray.Dispose();
        Application.Current.Exit();
    }

    private void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.MenuItems[0];   // Dashboard
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { ContentFrame.Navigate(typeof(SettingsPage)); return; }
        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag })
            ContentFrame.Navigate(tag switch
            {
                "sites"     => typeof(SitesPage),
                "databases" => typeof(DatabasesPage),
                "node"      => typeof(NodePage),
                "services"  => typeof(ServicesPage),
                "logs"      => typeof(LogsPage),
                _           => typeof(DashboardPage),
            });
    }
}
