using System;
using System.Collections.Generic;
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

public sealed class NodeVerRow
{
    public required string Version { get; init; }
    public required bool IsDefault { get; init; }
    public string Display => "v" + Version;
    public bool NotDefault => !IsDefault;
    public Visibility DefaultVis => IsDefault ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class NodePage : Page
{
    private static readonly string[] PageSizes = { "10", "15", "20", "50", "100", "All" };
    private List<NodeAppRow> _allApps = new();
    private int _appPage;
    private bool _pagingReady;

    public NodePage()
    {
        InitializeComponent();
        foreach (var p in PageSizes) AppPageSizeBox.Items.Add(new ComboBoxItem { Content = p });
        var saved = Config.Load().AppsPageSize;
        var idx = Array.IndexOf(PageSizes, saved >= 100000 ? "All" : saved.ToString());
        AppPageSizeBox.SelectedIndex = idx >= 0 ? idx : 1;   // default 15
        _pagingReady = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { RefreshVersions(); RefreshApps(); }

    // ── fnm versions ────────────────────────────────────────────────────────────
    private async void RefreshVersions()
    {
        var vers = await Task.Run(() =>
        {
            try { return EngineHost.Instance.Engine.NodeVersions(); }
            catch { return (IReadOnlyList<(string version, bool isDefault)>)Array.Empty<(string, bool)>(); }
        });
        var rows = vers.Select(v => new NodeVerRow { Version = v.version, IsDefault = v.isDefault }).ToList();
        VersList.ItemsSource = rows;
        EmptyVers.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Install_Click(object s, RoutedEventArgs e) => await VerRun("install", VerBox.Text.Trim());
    private async void Quick_Click(object s, RoutedEventArgs e)       { if ((s as FrameworkElement)?.Tag is string v) await VerRun("install", v); }
    private async void Use_Click(object s, RoutedEventArgs e)         { if ((s as FrameworkElement)?.Tag is string v) await VerRun("use", v); }
    private async void UninstallVer_Click(object s, RoutedEventArgs e){ if ((s as FrameworkElement)?.Tag is string v) await VerRun("uninstall", v); }

    private async Task VerRun(string sub, string v)
    {
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
        _allApps = NodeSite.List().Select(n =>
        {
            var c = NodeSite.Load(n);
            var detail = c is null ? "" :
                $"frontend :{c.Frontend.Port}" + (c.Backend is not null ? $"  ·  backend :{c.Backend.Port} ({c.ApiPath})" : "");
            return new NodeAppRow { Name = n, Detail = detail, Running = NodeSite.Running(n), HasBackend = c?.Backend is not null, Url = $"http://{n}.{tld}" };
        }).ToList();
        RenderApps();
    }

    // ── search + pagination (shared pattern with Sites/Databases) ─────────────────
    private int AppPageSize()
    {
        var v = (AppPageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "15";
        return v == "All" ? int.MaxValue : (int.TryParse(v, out var n) ? n : 15);
    }

    private void RenderApps()
    {
        var q = (AppSearchBox.Text ?? "").Trim();
        var filtered = q.Length == 0 ? _allApps
            : _allApps.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                               || r.Detail.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var size = AppPageSize();
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)size));
        _appPage = Math.Clamp(_appPage, 0, pages - 1);
        var page = filtered.Skip(_appPage * size).Take(size).ToList();
        AppsList.ItemsSource = page;
        EmptyApps.Text = _allApps.Count == 0 ? "No Node apps yet." : "No matches.";
        EmptyApps.Visibility = page.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AppPager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        AppPageLabel.Text = $"Page {_appPage + 1} of {pages}";
        AppPrevBtn.IsEnabled = _appPage > 0;
        AppNextBtn.IsEnabled = _appPage < pages - 1;
    }

    private void AppSearch_Changed(object s, TextChangedEventArgs e) { _appPage = 0; if (_pagingReady) RenderApps(); }
    private void AppPageSize_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_pagingReady) return;
        var cfg = Config.Load();
        var v = (AppPageSizeBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
        cfg.AppsPageSize = v == "All" ? 100000 : (int.TryParse(v, out var n) ? n : 15);
        cfg.Save();
        _appPage = 0; RenderApps();
    }
    private void AppPrev_Click(object s, RoutedEventArgs e) { _appPage--; RenderApps(); }
    private void AppNext_Click(object s, RoutedEventArgs e) { _appPage++; RenderApps(); }

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

    private static Grid WithBrowse(TextBox box)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(box);
        Grid.SetColumn(box, 0);
        var btn = new Button { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
        btn.Click += async (_, _) => {
            var path = await Picker.FolderAsync();
            if (!string.IsNullOrEmpty(path)) box.Text = path;
        };
        grid.Children.Add(btn);
        Grid.SetColumn(btn, 1);
        return grid;
    }

    private async void AddApp_Click(object s, RoutedEventArgs e)
    {
        var name  = new TextBox { Header = "Name", PlaceholderText = "myapp" };
        var feDir = new TextBox { Header = "Frontend folder", PlaceholderText = @"C:\path\to\frontend" };
        var feCmd = new TextBox { Header = "Frontend command", Text = "npm run dev" };
        var fePort = new NumberBox { Header = "Frontend port", Value = 3000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var beDir = new TextBox { Header = "Backend folder (optional)", PlaceholderText = @"C:\path\to\backend" };
        var beCmd = new TextBox { Header = "Backend command (optional)", PlaceholderText = "npm start" };
        var bePort = new NumberBox { Header = "Backend port (optional)", Value = double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var api   = new TextBox { Header = "API path → backend", Text = "/api" };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in new FrameworkElement[] { name, WithBrowse(feDir), feCmd, fePort, WithBrowse(beDir), beCmd, bePort, api }) panel.Children.Add(c);

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
