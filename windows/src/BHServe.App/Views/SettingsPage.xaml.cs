using System;
using System.Diagnostics;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading;
    private (string tld, int http, int https) _orig;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var v in BHServe.Core.Services.PhpVersions) PhpDefBox.Items.Add(new ComboBoxItem { Content = v });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Load();

    private void Load()
    {
        _loading = true;
        var cfg = Config.Load();
        TldBox.Text   = cfg.Tld;
        HttpBox.Value  = cfg.HttpPort;
        HttpsBox.Value = cfg.HttpsPort;
        PhpDefBox.SelectedIndex = Math.Max(0, Array.IndexOf(BHServe.Core.Services.PhpVersions, cfg.DefaultPhp));
        WebDefBox.SelectedIndex = cfg.DefaultWeb == "apache" ? 1 : 0;
        RootBox.Text  = cfg.SitesRoot;
        HomeText.Text = Paths.Home;

        AutostartToggle.IsOn  = Autostart.IsEnabled();
        StartSvcToggle.IsOn   = cfg.StartServicesOnLaunch;
        TrayToggle.IsOn       = cfg.MinimizeToTray;
        AutoUpdateToggle.IsOn = cfg.AutoUpdate;
        DashSizeBox.Value     = cfg.DashboardPageSize;
        SitesSizeBox.Value    = cfg.SitesPageSize;
        Version.Text = $"BHServe for Windows · {Updater.CurrentVersion}";

        _orig = (cfg.Tld, cfg.HttpPort, cfg.HttpsPort);
        SaveStatus.Text = "";
        _loading = false;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var tld   = TldBox.Text.Trim().TrimStart('.');
        var http  = double.IsNaN(HttpBox.Value) ? _orig.http : (int)HttpBox.Value;
        var https = double.IsNaN(HttpsBox.Value) ? _orig.https : (int)HttpsBox.Value;
        var dphp  = (PhpDefBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        var dweb  = (WebDefBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "nginx";
        var root  = RootBox.Text.Trim();
        var topologyChanged = tld != _orig.tld || http != _orig.http || https != _orig.https;

        SaveBtn.IsEnabled = false; Busy.IsActive = true; SaveStatus.Text = "Saving…";
        var (ok, output) = await EngineHost.Instance.RunCaptured(() =>
        {
            var eng = EngineHost.Instance.Engine;
            eng.ConfigSet("tld", tld);
            eng.ConfigSet("http_port", http.ToString());
            eng.ConfigSet("https_port", https.ToString());
            eng.ConfigSet("default_php", dphp);
            eng.ConfigSet("default_web", dweb);
            eng.ConfigSet("sites_root", root);
            if (topologyChanged && Nginx.Running()) eng.Restart("nginx");
        });
        Busy.IsActive = false; SaveBtn.IsEnabled = true;
        SaveStatus.Text = ok ? "Saved." : "Some values were invalid — nothing partial was kept consistent; check and retry.";
        if (!ok && output.Length > 0)
            await new ContentDialog { Title = "Couldn't save", Content = output, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync();
        Load();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => Load();

    private async void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        var path = await Picker.FolderAsync();
        if (!string.IsNullOrEmpty(path)) RootBox.Text = path;
    }

    private void Autostart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (AutostartToggle.IsOn) Autostart.Enable(); else Autostart.Disable();
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = Config.Load(); cfg.MinimizeToTray = TrayToggle.IsOn; cfg.Save();
    }

    private void Flag_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var cfg = Config.Load();
        cfg.StartServicesOnLaunch = StartSvcToggle.IsOn;
        cfg.AutoUpdate = AutoUpdateToggle.IsOn;
        cfg.Save();
    }

    private void ListSize_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_loading) return;
        var cfg = Config.Load();
        if (!double.IsNaN(DashSizeBox.Value))  cfg.DashboardPageSize = Math.Clamp((int)DashSizeBox.Value, 1, 500);
        if (!double.IsNaN(SitesSizeBox.Value)) cfg.SitesPageSize = Math.Clamp((int)SitesSizeBox.Value, 1, 500);
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
            catch (Exception ex) { UpdateStatus.Text = "Download failed: " + ex.Message; }
        }
    }
}
