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

public sealed partial class DashboardPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _loading, _pageSizeSet;
    private readonly System.Collections.Generic.Queue<double> _cpuHist = new();
    private string _pmaUrl = "", _admUrl = "", _mailUrl = "";

    private static readonly SolidColorBrush On  = new(Colors.SeaGreen);
    private static readonly SolidColorBrush Off = new(Colors.Gray);
    private static readonly Style? _accent =
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) ? s as Style : null;

    public DashboardPage()
    {
        InitializeComponent();
        EngineHost.Instance.LogAppended += OnLog;
        _timer.Tick += (_, _) => Refresh();
        LogBox.Text = EngineHost.Instance.LogText;
        SiteList.Changed += (_, _) => Refresh();   // re-pull after a per-site action
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { Refresh(); _timer.Start(); AutoEnableIonCube(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    // ── ionCube ──────────────────────────────────────────────────────────────────────────────
    // Fully automatic — no button. Engine.EnableIonCube re-installs the loader DLL when the FILE is
    // missing (the actual 2026-07 root cause — respawning could never fix that), then verifies the
    // running workers and respawns any that didn't load it. Runs at app launch (App.xaml.cs) and
    // whenever the window is opened (below).
    private DateTime _lastIonCubeAuto = DateTime.MinValue;

    /// <summary>When the user OPENS the window, heal ionCube if broken. Read-only health check first —
    /// a no-op when already healthy, so it's never disruptive. Debounced. Callable from MainWindow when
    /// the window is shown from the tray.</summary>
    public void AutoEnableIonCube()
    {
        if ((DateTime.UtcNow - _lastIonCubeAuto).TotalSeconds < 30) return;
        _lastIonCubeAuto = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            try
            {
                var eng = EngineHost.Instance.Engine;
                if (!eng.IonCubeAllHealthy()) eng.EnableIonCube(quiet: true);
            }
            catch { }
        });
    }

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

        bool myRun = Running("mysql"), mariaRun = Running("mariadb"), pgRun = Running("postgresql");
        var dbRun = myRun || mariaRun || pgRun;
        DbVal.Text = mariaRun ? "MariaDB" : myRun ? "MySQL" : pgRun ? "PostgreSQL" : "MySQL / MariaDB";
        DbSub.Text = dbRun ? "running" : "stopped";
        DbDot.Fill = dbRun ? On : Off;

        var redis = Running("redis"); var memc = Running("memcached");
        CacheVal.Text = "Redis · Memcached";
        CacheSub.Text = $"redis {(redis ? "on" : "off")}, memcached {(memc ? "on" : "off")}";
        CacheDot.Fill = redis || memc ? On : Off;

        // ── metrics ──
        var cpu = SystemMetrics.CpuPercent(); CpuText.Text = $"{cpu:0}%";
        _cpuHist.Enqueue(cpu);
        while (_cpuHist.Count > 40) _cpuHist.Dequeue();
        var arr = _cpuHist.ToArray();
        var pts = new Microsoft.UI.Xaml.Media.PointCollection();
        for (var i = 0; i < arr.Length; i++)
        {
            var x = arr.Length <= 1 ? 0 : i * 200.0 / (arr.Length - 1);
            var y = 30 - arr[i] / 100.0 * 30;
            pts.Add(new Windows.Foundation.Point(x, y));
        }
        CpuSpark.Points = pts;
        var (mu, mt, mp) = SystemMetrics.Memory(); MemText.Text = $"{mu:0.0} / {mt:0.0} GB"; MemBar.Value = mp;
        var (du, dt, dp) = SystemMetrics.Disk(); DiskText.Text = $"{du:0} / {dt:0} GB"; DiskBar.Value = dp;
        var (down, up) = SystemMetrics.Network();
        NetDown.Text = $"Down  {Rate(down)}"; NetUp.Text = $"Up  {Rate(up)}";

        SubTitle.Text = $"{snap.Services.Count(s => s.Running)} services running · {sites.Count} sites";

        // ── global buttons reflect real service state ──
        // "active" = installed + auto-start (★). Start all only has work when an active service
        // isn't running yet; once everything active is up, Stop becomes the highlighted action.
        if (!Busy.IsActive)
        {
            string[] daemonKeys = { "nginx", "apache", "mysql", "mariadb", "postgresql", "redis", "memcached", "mailpit" };
            var daemons = snap.Services.Where(s => daemonKeys.Contains(s.Key)).ToList();
            var toStart = daemons.Count(s => s.Installed && s.AutoStart && !s.Running);
            var anyRunning = snap.Services.Any(s => s.Running);
            var somethingToStart = toStart > 0;
            SetBtn(StartBtn, somethingToStart, somethingToStart);
            SetBtn(StopBtn, anyRunning, !somethingToStart && anyRunning);
            SetBtn(RestartBtn, anyRunning, false);   // can't restart what isn't running
        }

        // ── websites (delegated to the shared list control: Show + search + actions + paging) ──
        if (!_pageSizeSet) { SiteList.SetDefaultPageSize(Config.Load().DashboardPageSize); _pageSizeSet = true; }
        SiteList.SetData(sites.Select(s => new SiteRow
        {
            Name = s.Name, Domain = s.Domain, Php = s.Php, Root = s.Root,
            Secure = s.Secure, Enabled = s.Enabled, Server = s.Server,
        }));
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

    // Quick "Add site" from the dashboard → jump to the Sites tab with the name box focused.
    private void AddSite_Click(object sender, RoutedEventArgs e) => App.Window?.GoToSites(addNew: true);

    private void SetBtn(Button b, bool enabled, bool accent)
    {
        b.IsEnabled = enabled;
        var style = accent ? _accent : null;
        if (!ReferenceEquals(b.Style, style)) b.Style = style;
    }

    private async Task Op(Action action)
    {
        Busy.IsActive = true;
        StartBtn.IsEnabled = StopBtn.IsEnabled = RestartBtn.IsEnabled = false;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Refresh();   // recomputes the correct enabled/highlight state
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
    }
}
