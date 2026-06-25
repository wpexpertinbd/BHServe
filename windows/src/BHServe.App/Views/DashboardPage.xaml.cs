using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

/// <summary>Row shown in the dashboard website list.</summary>
public sealed class WebsiteRow
{
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public required string Url { get; init; }
    public required string Badge { get; init; }
    public required bool Enabled { get; init; }
    public Brush DotBrush => new SolidColorBrush(Enabled ? Colors.SeaGreen : Colors.Gray);
}

public sealed partial class DashboardPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loading;
    private string _pmaUrl = "", _admUrl = "", _mailUrl = "";

    private static readonly SolidColorBrush On  = new(Colors.SeaGreen);
    private static readonly SolidColorBrush Off = new(Colors.Gray);

    public DashboardPage()
    {
        InitializeComponent();
        EngineHost.Instance.LogAppended += OnLog;
        _timer.Tick += (_, _) => Refresh();
        LogBox.Text = EngineHost.Instance.LogText;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { Refresh(); _timer.Start(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private void OnLog(string line) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            LogBox.Text = EngineHost.Instance.LogText;
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
        });

    private async void Refresh()
    {
        Snapshot snap;
        try { snap = await EngineHost.Instance.Snapshot(); } catch { return; }

        bool Running(string key) => snap.Services.FirstOrDefault(s => s.Key == key)?.Running ?? false;
        var phpVers = snap.Services.Where(s => s.Role == ServiceRole.Php && s.Key.StartsWith("php@") && s.Installed)
                                   .Select(s => s.Key["php@".Length..]).OrderByDescending(v => v).ToList();
        var sites = snap.Sites.Where(s => !Engine.IsTool(s.Name)).OrderBy(s => s.Name).ToList();

        // ── status cards ──
        var nginx = Running("nginx"); var apache = Running("apache");
        WebVal.Text = apache && nginx ? "nginx + apache" : nginx ? "nginx" : apache ? "apache" : "nginx";
        WebSub.Text = $"{sites.Count} site{(sites.Count == 1 ? "" : "s")}";
        WebDot.Fill = nginx || apache ? On : Off;

        PhpVal.Text = phpVers.Count > 0 ? string.Join(", ", phpVers) : "not installed";
        PhpSub.Text = $"{phpVers.Count} installed";
        PhpDot.Fill = snap.Services.Any(s => s.Role == ServiceRole.Php && s.Running) ? On : Off;

        var db = Running("mariadb");
        DbVal.Text = "MariaDB"; DbSub.Text = db ? "running" : "stopped"; DbDot.Fill = db ? On : Off;

        var redis = Running("redis"); var memc = Running("memcached");
        CacheVal.Text = "Redis · Memcached";
        CacheSub.Text = $"redis {(redis ? "on" : "off")}, memcached {(memc ? "on" : "off")}";
        CacheDot.Fill = redis || memc ? On : Off;

        // ── metrics ──
        var cpu = SystemMetrics.CpuPercent(); CpuText.Text = $"{cpu:0}%"; CpuBar.Value = cpu;
        var (mu, mt, mp) = SystemMetrics.Memory(); MemText.Text = $"{mu:0.0} / {mt:0.0} GB"; MemBar.Value = mp;
        var (du, dt, dp) = SystemMetrics.Disk(); DiskText.Text = $"{du:0} / {dt:0} GB"; DiskBar.Value = dp;
        var (down, up) = SystemMetrics.Network();
        NetDown.Text = $"Down  {Rate(down)}"; NetUp.Text = $"Up  {Rate(up)}";

        SubTitle.Text = $"{snap.Services.Count(s => s.Running)} services running · {sites.Count} sites";

        // ── websites ──
        WebsitesList.ItemsSource = sites.Select(s => new WebsiteRow
        {
            Name = s.Name, Domain = s.Domain, Enabled = s.Enabled,
            Url = (s.Secure ? "https://" : "http://") + s.Domain,
            Badge = !string.IsNullOrEmpty(s.Php) && s.Php != "-" ? $"{s.Server} · php {s.Php}" : s.Server,
        }).ToList();
        NoSites.Visibility = sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WebHeader.Text = $"Websites ({sites.Count})";

        // ── web tools ──
        _loading = true;
        SetTool(snap, "phpmyadmin", PmaToggle, PmaOpen, PmaStatus, ref _pmaUrl);
        SetTool(snap, "adminer",    AdmToggle, AdmOpen, AdmStatus, ref _admUrl);
        SetTool(snap, "mailpit",    MailToggle, MailOpen, MailStatus, ref _mailUrl);
        _loading = false;
    }

    private static void SetTool(Snapshot snap, string name, ToggleSwitch toggle, Button open, TextBlock status, ref string url)
    {
        var site = snap.Sites.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        var active = site is { Enabled: true };
        toggle.IsOn = active;
        open.IsEnabled = active;
        status.Text = active ? (site!.Secure ? "Active · https" : "Active") : "Off";
        url = active ? (site!.Secure ? "https://" : "http://") + site.Domain : "";
    }

    private static string Rate(double kbps) => kbps >= 1024 ? $"{kbps / 1024:0.0} MB/s" : $"{kbps:0} KB/s";

    // ── websites ──
    private void OpenSite_Click(object s, RoutedEventArgs e) { if ((s as FrameworkElement)?.Tag is string u && u.Length > 0) Launch(u); }

    // ── web tools ──
    private async void Pma_Toggled(object s, RoutedEventArgs e)  { if (!_loading) await ToolOp("phpmyadmin", PmaToggle.IsOn); }
    private async void Adm_Toggled(object s, RoutedEventArgs e)  { if (!_loading) await ToolOp("adminer",    AdmToggle.IsOn); }
    private async void Mail_Toggled(object s, RoutedEventArgs e) { if (!_loading) await ToolOp("mailpit",    MailToggle.IsOn); }

    private void PmaOpen_Click(object s, RoutedEventArgs e)  { if (_pmaUrl.Length > 0)  Launch(_pmaUrl); }
    private void AdmOpen_Click(object s, RoutedEventArgs e)  { if (_admUrl.Length > 0)  Launch(_admUrl); }
    private void MailOpen_Click(object s, RoutedEventArgs e) { if (_mailUrl.Length > 0) Launch(_mailUrl); }

    private async Task ToolOp(string tool, bool on)
    {
        Busy.IsActive = true;
        var (ok, output) = await EngineHost.Instance.RunCaptured(() => EngineHost.Instance.Engine.ToolSet(tool, on));
        Busy.IsActive = false;
        Refresh();
        if (!ok && output.Length > 0)
            await new ContentDialog { Title = "Web tool", Content = output, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync();
    }

    // ── global ──
    private async void StartAll_Click(object sender, RoutedEventArgs e)   => await Op(() => EngineHost.Instance.Engine.Start("all"));
    private async void StopAll_Click(object sender, RoutedEventArgs e)    => await Op(() => EngineHost.Instance.Engine.Stop("all"));
    private async void RestartAll_Click(object sender, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.Restart("all"));

    private async Task Op(Action action)
    {
        Busy.IsActive = true;
        StartBtn.IsEnabled = StopBtn.IsEnabled = RestartBtn.IsEnabled = false;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        StartBtn.IsEnabled = StopBtn.IsEnabled = RestartBtn.IsEnabled = true;
        Refresh();
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
    }
}
