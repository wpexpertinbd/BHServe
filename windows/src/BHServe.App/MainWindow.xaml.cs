using BHServe.App.Services;
using BHServe.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BHServe.App;

public sealed partial class MainWindow : Window
{
    private readonly TrayIcon _tray;
    private bool _reallyQuit;

    public MainWindow()
    {
        InitializeComponent();

        _tray = new TrayIcon("BHServe — local web stack");
        _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowFromTray);
        _tray.QuitRequested += () => DispatcherQueue.TryEnqueue(QuitApp);

        // Close → hide to tray (matches the mac menu-bar behavior); Quit really exits.
        AppWindow.Closing += (_, e) =>
        {
            if (_reallyQuit) { _tray.Dispose(); return; }
            e.Cancel = true;
            AppWindow.Hide();
        };
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
                "sites"    => typeof(SitesPage),
                "services" => typeof(ServicesPage),
                _          => typeof(DashboardPage),
            });
    }
}
