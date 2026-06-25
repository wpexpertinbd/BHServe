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
    public required string Url { get; init; }
    public Brush DotBrush => new SolidColorBrush(Running ? Colors.SeaGreen : Colors.Gray);
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
            return new NodeAppRow { Name = n, Detail = detail, Running = NodeSite.Running(n), Url = $"http://{n}.{tld}" };
        }).ToList();
    }

    private void OpenApp_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is string url)
            try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }
    private async void StartApp_Click(object s, RoutedEventArgs e)  => await AppOp(() => EngineHost.Instance.Engine.NodeSiteStart(Tag(s)));
    private async void StopApp_Click(object s, RoutedEventArgs e)   => await AppOp(() => EngineHost.Instance.Engine.NodeSiteStop(Tag(s)));
    private async void RemoveApp_Click(object s, RoutedEventArgs e) => await AppOp(() => EngineHost.Instance.Engine.NodeSiteRemove(Tag(s)));

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

        var n = name.Text.Trim();
        if (n.Length == 0 || feDir.Text.Trim().Length == 0) return;
        var bp = double.IsNaN(bePort.Value) ? 0 : (int)bePort.Value;
        await AppOp(() => EngineHost.Instance.Engine.NodeSiteAdd(
            n, feDir.Text.Trim(), feCmd.Text.Trim(), (int)fePort.Value,
            beDir.Text.Trim(), beCmd.Text.Trim(), bp, api.Text.Trim()));
    }

    private async Task AppOp(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        RefreshApps();
    }
}
