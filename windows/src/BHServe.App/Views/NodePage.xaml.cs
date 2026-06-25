using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed class NodeAppRow
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required bool Running { get; init; }
    public required bool HasBackend { get; init; }
    public required string Url { get; init; }
    public Brush DotBrush => new SolidColorBrush(Running ? Colors.SeaGreen : Colors.Gray);
    public Visibility HasBackendVis => HasBackend ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class NodePage : Page
{
    public NodePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) { RefreshVersions(); RefreshApps(); }

    // ── fnm versions ────────────────────────────────────────────────────────────
    private async void RefreshVersions()
    {
        var text = await Task.Run(() =>
        {
            var sb = new StringBuilder();
            var eng = new Engine { Out = l => sb.AppendLine(l), Err = l => sb.AppendLine(l) };
            try { eng.Node("list"); } catch (Exception ex) { sb.AppendLine(ex.Message); }
            return sb.ToString().Trim();
        });
        OutputBox.Text = string.IsNullOrWhiteSpace(text) ? "No Node versions installed yet." : text;
    }

    private async void Install_Click(object s, RoutedEventArgs e)   => await VerOp("install");
    private async void Default_Click(object s, RoutedEventArgs e)   => await VerOp("default");
    private async void Uninstall_Click(object s, RoutedEventArgs e) => await VerOp("uninstall");

    private async Task VerOp(string sub)
    {
        var v = VerBox.Text.Trim();
        if (v.Length == 0) return;
        Busy.IsActive = true;
        await EngineHost.Instance.Run(() => EngineHost.Instance.Engine.Node(sub, v));
        Busy.IsActive = false;
        RefreshVersions();
    }

    // ── node apps ────────────────────────────────────────────────────────────────
    private void RefreshApps()
    {
        var tld = Config.Load().Tld;
        AppsList.ItemsSource = NodeSite.List().Select(n =>
        {
            var c = NodeSite.Load(n);
            var detail = c is null ? "" :
                $"frontend :{c.Frontend.Port}" + (c.Backend is not null ? $"  ·  backend :{c.Backend.Port} ({c.ApiPath})" : "");
            return new NodeAppRow { Name = n, Detail = detail, Running = NodeSite.Running(n), HasBackend = c?.Backend is not null, Url = $"http://{n}.{tld}" };
        }).ToList();
    }

    private void OpenApp_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is string url)
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }
    // Tag(s) reads a UI element — capture it on the UI thread, never inside the engine lambda.
    private async void StartApp_Click(object s, RoutedEventArgs e)  { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.NodeSiteStart(n)); }
    private async void StopApp_Click(object s, RoutedEventArgs e)   { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.NodeSiteStop(n)); }
    private async void RemoveApp_Click(object s, RoutedEventArgs e) { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.NodeSiteRemove(n)); }

    private static string Tag(object s) => (s as FrameworkElement)?.Tag as string ?? "";

    private async void AddApp_Click(object s, RoutedEventArgs e)
    {
        var name  = new TextBox { Header = "Name", PlaceholderText = "myapp" };
        var feDir = new TextBox { Header = "Frontend folder", PlaceholderText = @"C:\path\to\frontend" };
        var feCmd = new TextBox { Header = "Frontend command", Text = "npm run dev" };
        var fePort = new NumberBox { Header = "Frontend port", Value = 3000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var beDir = new TextBox { Header = "Backend folder (optional)" };
        var beCmd = new TextBox { Header = "Backend command (optional)", PlaceholderText = "npm start" };
        var bePort = new NumberBox { Header = "Backend port (optional)", Value = double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var api   = new TextBox { Header = "API path → backend", Text = "/api" };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in new FrameworkElement[] { name, feDir, feCmd, fePort, beDir, beCmd, bePort, api }) panel.Children.Add(c);

        var dlg = new ContentDialog
        {
            Title = "Add Node app", Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        // Snapshot every control value on the UI thread before handing off to the engine thread.
        string n = name.Text.Trim(), feD = feDir.Text.Trim(), feC = feCmd.Text.Trim(),
               beD = beDir.Text.Trim(), beC = beCmd.Text.Trim(), apiP = api.Text.Trim();
        int feP = (int)fePort.Value, bp = double.IsNaN(bePort.Value) ? 0 : (int)bePort.Value;
        if (n.Length == 0 || feD.Length == 0) return;
        await AppOp(() => EngineHost.Instance.Engine.NodeSiteAdd(n, feD, feC, feP, beD, beC, bp, apiP));
    }

    // ── per-app extras (⋯ menu) ──────────────────────────────────────────────────
    private async void RestartApp_Click(object s, RoutedEventArgs e) { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.NodeSiteRestart(n)); }

    private void FolderApp_Click(object s, RoutedEventArgs e)
    {
        var dir = EngineHost.Instance.Engine.NodeSiteDir(Tag(s), "frontend");
        if (dir.Length > 0 && System.IO.Directory.Exists(dir))
            try { Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true }); } catch { }
    }

    private async void LogsApp_Click(object s, RoutedEventArgs e)
    {
        var n = Tag(s);
        var fe = EngineHost.Instance.Engine.NodeSiteLog(n, "frontend");
        var be = EngineHost.Instance.Engine.NodeSiteLog(n, "backend");
        var sb = new StringBuilder();
        sb.AppendLine("---- frontend ----").AppendLine(fe.Length > 0 ? fe : "(no output yet)");
        if (be.Length > 0) sb.AppendLine().AppendLine("---- backend ----").AppendLine(be);
        await ShowText($"Logs · {n}", sb.ToString().Trim());
    }

    private async void NpmFe_Click(object s, RoutedEventArgs e) => await NpmOp(Tag(s), "frontend");
    private async void NpmBe_Click(object s, RoutedEventArgs e) => await NpmOp(Tag(s), "backend");

    private async Task NpmOp(string name, string which)
    {
        Busy.IsActive = true;
        var (ok, output) = await Task.Run(() => EngineHost.Instance.Engine.NodeSiteNpm(name, which));
        Busy.IsActive = false;
        RefreshApps();
        await ShowText($"npm install ({which}) · {name}", output.Length > 0 ? output : (ok ? "done" : "failed"));
    }

    private async void EnvFe_Click(object s, RoutedEventArgs e) => await EnvOp(Tag(s), "frontend");
    private async void EnvBe_Click(object s, RoutedEventArgs e) => await EnvOp(Tag(s), "backend");

    private async Task EnvOp(string name, string which)
    {
        var path = EngineHost.Instance.Engine.NodeSiteEnvPath(name, which);
        if (path.Length == 0) { await ShowText("Edit .env", $"No {which} directory."); return; }
        string text = "";
        try { if (System.IO.File.Exists(path)) text = await System.IO.File.ReadAllTextAsync(path); } catch { }
        var box = new TextBox
        {
            Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"), Height = 320, PlaceholderText = "KEY=value",
        };
        var dlg = new ContentDialog
        {
            Title = $".env ({which}) · {name}", Content = new ScrollViewer { Content = box, MinWidth = 460 },
            PrimaryButtonText = "Save & restart", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var content = box.Text;
        try { await System.IO.File.WriteAllTextAsync(path, content); }
        catch (Exception ex) { await ShowText("Edit .env", "Could not save: " + ex.Message); return; }
        await AppOp(() => EngineHost.Instance.Engine.NodeSiteRestart(name));
    }

    private async void EditApp_Click(object s, RoutedEventArgs e)
    {
        var name = Tag(s);
        var nc = EngineHost.Instance.Engine.NodeSiteConfig(name);
        if (nc is null) return;
        var feCmd  = new TextBox  { Header = "Frontend command", Text = nc.Frontend.Cmd };
        var fePort = new NumberBox { Header = "Frontend port", Value = nc.Frontend.Port, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var beCmd  = new TextBox  { Header = "Backend command", Text = nc.Backend?.Cmd ?? "", IsEnabled = nc.Backend is not null };
        var bePort = new NumberBox { Header = "Backend port", Value = nc.Backend?.Port ?? double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, IsEnabled = nc.Backend is not null };
        var api    = new TextBox  { Header = "API path → backend", Text = nc.ApiPath, IsEnabled = nc.Backend is not null };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in new FrameworkElement[] { feCmd, fePort, beCmd, bePort, api }) panel.Children.Add(c);
        var dlg = new ContentDialog
        {
            Title = $"Edit · {name}", Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            PrimaryButtonText = "Save & restart", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        string fc = feCmd.Text.Trim(), bc = beCmd.Text.Trim(), ap = api.Text.Trim();
        int fp = (int)fePort.Value, bp = double.IsNaN(bePort.Value) ? 0 : (int)bePort.Value;
        await AppOp(() => EngineHost.Instance.Engine.NodeSiteEdit(name, fc, fp, bc, bp, ap));
    }

    private Task ShowText(string title, string body) =>
        new ContentDialog
        {
            Title = title, CloseButtonText = "Close", XamlRoot = this.XamlRoot,
            Content = new ScrollViewer { MaxHeight = 400, MinWidth = 460, Content = new TextBlock { Text = body, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap } },
        }.ShowAsync().AsTask();

    private async Task AppOp(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        RefreshApps();
    }
}
