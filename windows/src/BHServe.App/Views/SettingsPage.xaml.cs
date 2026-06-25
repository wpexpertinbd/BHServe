using System.Diagnostics;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        var cfg = Config.Load();
        TldText.Text   = "." + cfg.Tld;
        PortsText.Text = $"{cfg.HttpPort} / {cfg.HttpsPort}";
        PhpText.Text   = cfg.DefaultPhp;
        RootText.Text  = cfg.SitesRoot;
        HomeText.Text  = Paths.Home;
        AutostartToggle.IsOn = Autostart.IsEnabled();
        Version.Text = "BHServe for Windows · 0.1.0";
        _loading = false;
    }

    private void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AutostartToggle.IsOn) Autostart.Enable(); else Autostart.Disable();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Paths.Home, UseShellExecute = true }); } catch { }
    }
}
