using System;
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
        TrayToggle.IsOn = cfg.MinimizeToTray;
        DashSizeBox.Value = cfg.DashboardPageSize;
        SitesSizeBox.Value = cfg.SitesPageSize;
        Version.Text = "BHServe for Windows · 0.1.0";
        _loading = false;
    }

    private void ListSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading) return;
        var cfg = Config.Load();
        if (!double.IsNaN(DashSizeBox.Value))  cfg.DashboardPageSize = Math.Clamp((int)DashSizeBox.Value, 1, 500);
        if (!double.IsNaN(SitesSizeBox.Value)) cfg.SitesPageSize = Math.Clamp((int)SitesSizeBox.Value, 1, 500);
        cfg.Save();
    }

    private void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AutostartToggle.IsOn) Autostart.Enable(); else Autostart.Disable();
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = Config.Load();
        cfg.MinimizeToTray = TrayToggle.IsOn;
        cfg.Save();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = Paths.Home, UseShellExecute = true }); } catch { }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false; UpdateBusy.IsActive = true;
        UpdateStatus.Text = "Checking…";
        var r = await Updater.Check();
        UpdateBusy.IsActive = false; UpdateBtn.IsEnabled = true;

        if (r.Error is not null) { UpdateStatus.Text = $"Update check: {r.Error}  (you're on {Updater.CurrentVersion})"; return; }
        if (!r.UpdateAvailable) { UpdateStatus.Text = $"You're on the latest version ({Updater.CurrentVersion})."; return; }

        UpdateStatus.Text = $"Update available: {r.Latest} (you have {Updater.CurrentVersion}).";
        if (r.AssetUrl is null) return;
        var dlg = new ContentDialog
        {
            Title = $"BHServe {r.Latest} available",
            Content = (string.IsNullOrWhiteSpace(r.Notes) ? "" : r.Notes + "\n\n") + "Download and run the installer now?",
            PrimaryButtonText = "Download & install", CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            UpdateStatus.Text = "Downloading installer…";
            try { await Updater.DownloadAndRun(r.AssetUrl); }
            catch (System.Exception ex) { UpdateStatus.Text = "Download failed: " + ex.Message; }
        }
    }
}
