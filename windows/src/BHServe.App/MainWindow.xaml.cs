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
    private Updater.Result? _pendingUpdate;   // an available update waiting to be offered (shown when the window is visible)
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(24) };   // daily re-check for long-running (tray) instances

    public MainWindow()
    {
        InitializeComponent();

        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(icon)) { try { AppWindow.SetIcon(icon); } catch { } }

        _tray = new TrayIcon($"BHServe {Updater.CurrentVersion} — local web stack", icon);
        _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowFromTray);
        _tray.QuitRequested += () => DispatcherQueue.TryEnqueue(QuitApp);
        _tray.StartAllRequested   += () => System.Threading.Tasks.Task.Run(() => { try { EngineHost.Instance.Engine.Start("all"); } catch { } });
        _tray.StopAllRequested    += () => System.Threading.Tasks.Task.Run(() => { try { EngineHost.Instance.Engine.Stop("all"); } catch { } });
        _tray.RestartAllRequested += () => System.Threading.Tasks.Task.Run(() => { try { EngineHost.Instance.Engine.Restart("all"); } catch { } });

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

        _ = CheckForUpdateOnLaunch();
        // Re-check once a day so an instance that stays open (tray/autostart) still notices updates
        // without needing a restart. Same gating + prompt as the launch check.
        _updateTimer.Tick += (_, _) => _ = CheckForUpdateOnLaunch();
        _updateTimer.Start();
    }

    /// <summary>On launch (auto-update on), check GitHub for a newer build and PROACTIVELY tell the user —
    /// a popup if the window is visible, or a tray balloon if BHServe started hidden (autostart). Also
    /// puts the attention dot on the Settings item. The user no longer has to open Settings to discover
    /// an update.</summary>
    private async System.Threading.Tasks.Task CheckForUpdateOnLaunch()
    {
        try
        {
            if (!Config.Load().AutoUpdate) return;
            var r = await Updater.Check();
            if (!r.UpdateAvailable || r.AssetUrl is null) return;

            if (Nav.SettingsItem is NavigationViewItem si) si.InfoBadge = new InfoBadge();   // attention dot
            _pendingUpdate = r;

            if (AppWindow.IsVisible)
                await ShowUpdatePromptIfPending();
            else
                _tray.ShowBalloon($"BHServe {r.Latest} is available",
                    "A new version is ready — open BHServe to update.");
        }
        catch { }
    }

    /// <summary>If an update is waiting, show the "update now / later" prompt. Cleared after one show so
    /// it doesn't re-nag within a session (it re-checks next launch). Safe to call when nothing's pending.</summary>
    private async System.Threading.Tasks.Task ShowUpdatePromptIfPending()
    {
        if (_pendingUpdate is not { AssetUrl: { } asset } r) return;
        if ((Content as FrameworkElement)?.XamlRoot is not { } xamlRoot) return;   // window not ready yet — retry on next show
        _pendingUpdate = null;

        var dlg = new ContentDialog
        {
            Title = $"Update available — BHServe {r.Latest}",
            Content = $"A new version is ready.\n\nYou have {Updater.CurrentVersion} · latest is {r.Latest}.\n\n" +
                      "Update now? BHServe will close, install the update, and reopen on its own.",
            PrimaryButtonText = "Update now", CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = xamlRoot,
        };
        try
        {
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                await Updater.DownloadAndRun(asset);
        }
        catch { /* another dialog already open / UAC declined / network — re-offered on the next check */ }
    }

    /// <summary>Autostart-at-login entry point: keep BHServe running in the tray ONLY. The window is
    /// never Activate()'d (so it never appears on screen and never gets a taskbar button), and we also
    /// Hide() it as a belt-and-suspenders. The tray icon is the only entry point until the user opens it.</summary>
    public void StartHiddenInTray() => AppWindow.Hide();

    private void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p) p.Restore();
        Activate();
        _ = ShowUpdatePromptIfPending();   // if an update was found while hidden, offer it now that we're visible
    }

    private void QuitApp()
    {
        _reallyQuit = true;
        _tray.Dispose();
        Application.Current.Exit();
    }

    /// <summary>Self-updater path: mark a real quit (so the close handler doesn't hide to tray) and
    /// remove the tray icon. App.ForceQuit() then exits the process, unlocking the files for the installer.</summary>
    public void QuitForUpdate()
    {
        _reallyQuit = true;
        try { _tray.Dispose(); } catch { }
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
