using System;
using System.Collections.Generic;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

/// <summary>Row shown in the services list.</summary>
public sealed class SvcRow
{
    public required string Key { get; init; }
    public required string Version { get; init; }
    public required bool Installed { get; init; }
    public required bool Running { get; init; }
    public required bool AutoStart { get; init; }
    public required bool Manageable { get; init; }
    public bool NotInstalled => !Installed;
    public bool CanStart => Installed && !Running && Manageable;
    public bool IsPhp => Key.StartsWith("php");
    public Visibility PhpVis => IsPhp ? Visibility.Visible : Visibility.Collapsed;
    // mkcert / fnm are one-shot tools, not daemons — they have no run state, so don't show
    // Start/Stop or the ★ auto-start, and treat "installed" as ready (green).
    public Visibility ManageVis => Manageable ? Visibility.Visible : Visibility.Collapsed;
    public bool ReadyTool => Installed && !Manageable;
    public Brush DotBrush => new SolidColorBrush(
        Running || ReadyTool ? Colors.SeaGreen : Installed ? Colors.Gray : Colors.DimGray);
    public string StatusText
    {
        get
        {
            var st = !Installed ? "not installed" : !Manageable ? "installed" : Running ? "running" : "stopped";
            return Version.Length > 0 ? $"{Version}  ·  {st}" : st;
        }
    }
}

/// <summary>A titled group of services (PHP, Web servers, …).</summary>
public sealed class SvcGroup
{
    public required string Title { get; init; }
    public required List<SvcRow> Rows { get; init; }
}

public sealed partial class ServicesPage : Page
{
    private static readonly (ServiceRole role, string title)[] Order =
    {
        (ServiceRole.Php, "PHP"), (ServiceRole.Web, "Web servers"), (ServiceRole.Db, "Databases"),
        (ServiceRole.Cache, "Cache"), (ServiceRole.Mail, "Mail"), (ServiceRole.Node, "Node"), (ServiceRole.Tool, "Tools"),
    };

    public ServicesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        EngineHost.Instance.OpChanged += OnOpChanged;
        RenderOp();   // re-attach to an install that's still running from before we navigated away
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => EngineHost.Instance.OpChanged -= OnOpChanged;

    private void OnOpChanged() => DispatcherQueue.TryEnqueue(() => { RenderOp(); if (EngineHost.Instance.CurrentOp is { Running: false }) Refresh(); });

    private void RenderOp()
    {
        var op = EngineHost.Instance.CurrentOp;
        if (op is null) { OpBanner.Visibility = Visibility.Collapsed; return; }
        OpBanner.Visibility = Visibility.Visible;
        OpName.Text = op.Name;
        OpMsg.Text = op.Message;
        OpBar.IsIndeterminate = op.Running && op.Progress < 0;
        OpBar.Value = op.Progress < 0 ? 0 : op.Progress;
        OpPct.Text = op.Running && op.Progress >= 0 ? $"{op.Progress:0}%" : op.Running ? "working…" : op.Success ? "done" : "failed";
        OpDismiss.Visibility = op.Running ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OpDismiss_Click(object sender, RoutedEventArgs e) { EngineHost.Instance.DismissOp(); RenderOp(); }

    private async void Refresh()
    {
        Snapshot snap;
        try { snap = await EngineHost.Instance.Snapshot(); } catch { return; }
        var cfg = Config.Load();

        var rows = snap.Services.Select(s => new SvcRow
        {
            Key = s.Key, Version = BHServe.Core.Services.ShortVersion(s.Key, cfg),
            Installed = s.Installed, Running = s.Running, AutoStart = s.AutoStart,
            Manageable = s.Role is ServiceRole.Php
                || s.Key is "nginx" or "apache" or "mysql" or "mariadb" or "postgresql" or "redis" or "memcached" or "mailpit",
        }).ToList();

        Groups.ItemsSource = Order
            .Select(o => new SvcGroup
            {
                Title = o.title,
                Rows = rows.Where(r => BHServe.Core.Services.RoleOf(r.Key) == o.role).ToList(),
            })
            .Where(g => g.Rows.Count > 0)
            .ToList();
    }

    private static string InstallToken(string key) => key;   // "php@8.4" / "nginx" / … pass through

    private void AutoStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb || tb.Tag is not string key) return;
        var cfg = Config.Load();
        if (tb.IsChecked == true) BHServe.Core.Services.Enable(key, cfg);
        else BHServe.Core.Services.Disable(key, cfg);
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    { if ((sender as Button)?.Tag is string key) await Track($"Installing {key}", () => EngineHost.Instance.Engine.Install(InstallToken(key))); }

    private async void Start_Click(object sender, RoutedEventArgs e)
    { if ((sender as Button)?.Tag is string key) await Track($"Starting {key}", () => EngineHost.Instance.Engine.Start(key)); }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.Tag is string key) await Track($"Stopping {key}", () => EngineHost.Instance.Engine.Stop(key)); }

    private async void Update_Click(object sender, RoutedEventArgs e)
    { if ((sender as FrameworkElement)?.Tag is string key) await Track($"Updating {key}", () => EngineHost.Instance.Engine.Update(InstallToken(key))); }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        var dlg = new ContentDialog
        {
            Title = "Uninstall", Content = $"Remove the {key} binaries from BHServe's bin folder?",
            PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Track($"Uninstalling {key}", () => EngineHost.Instance.Engine.Uninstall(key));
    }

    private async System.Threading.Tasks.Task Track(string name, Action action)
    {
        await EngineHost.Instance.RunTracked(name, action);
        Refresh();
    }

    private void EditIni_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        try
        {
            var ver = key == "php" ? Config.Load().DefaultPhp : key[(key.IndexOf('@') + 1)..];
            var ini = EngineHost.Instance.Engine.PhpIniPath(ver);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ini, UseShellExecute = true });
        }
        catch { }
    }

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Refresh();
    }
}
