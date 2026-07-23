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

public sealed class PyAppRow
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required bool Running { get; init; }
    public required string Url { get; init; }
    public Brush DotBrush => new SolidColorBrush(Running ? Colors.SeaGreen : Colors.Gray);
}

public sealed partial class PythonPage : Page
{
    private static readonly string[] PageSizes = { "10", "15", "20", "50", "100", "All" };
    private List<PyAppRow> _allApps = new();
    private int _appPage;
    private bool _pagingReady;

    public PythonPage()
    {
        InitializeComponent();
        foreach (var p in PageSizes) AppPageSizeBox.Items.Add(new ComboBoxItem { Content = p });
        var saved = Config.Load().AppsPageSize;
        var idx = Array.IndexOf(PageSizes, saved >= 100000 ? "All" : saved.ToString());
        AppPageSizeBox.SelectedIndex = idx >= 0 ? idx : 1;   // default 15
        _pagingReady = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { RefreshInterp(); RefreshApps(); }

    private void RefreshInterp()
    {
        var installed = Tools.PythonInstalled;
        var ver = Tools.PythonVersion();
        InterpStatus.Text = installed
            ? $"Python {ver} installed"
            : "Not installed — install a portable Python to run Python apps.";
        InstallPyBtn.Content = installed ? "Reinstall / update" : "Install Python";
    }

    private async void InstallPy_Click(object s, RoutedEventArgs e)
    {
        Busy.IsActive = true; InstallPyBtn.IsEnabled = false;
        var (_, output) = await EngineHost.Instance.RunCaptured(() => EngineHost.Instance.Engine.Install("python"));
        Busy.IsActive = false; InstallPyBtn.IsEnabled = true;
        RefreshInterp();
        if (!Tools.PythonInstalled)
            await Info("Couldn't install Python", string.IsNullOrWhiteSpace(output)
                ? "The download didn't complete. Check Logs and try again."
                : output.Trim());
    }

    private void RefreshApps()
    {
        var tld = Config.Load().Tld;
        _allApps = PySite.List().Select(n =>
        {
            var c = PySite.Load(n);
            var detail = $":{c?.Port}  ·  {c?.Cmd}";
            return new PyAppRow { Name = n, Detail = detail, Running = PySite.Running(n), Url = $"https://{n}.{tld}" };
        }).ToList();
        RenderApps();
    }

    // ── search + pagination (shared pattern with Sites/Databases/Node) ────────────
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
        EmptyApps.Text = _allApps.Count == 0 ? "No Python apps yet. Click “Add Python app”." : "No matches.";
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

    private static string Tag(object s) => (s as FrameworkElement)?.Tag as string ?? "";
    private static void Launch(string t) { try { Process.Start(new ProcessStartInfo { FileName = t, UseShellExecute = true }); } catch { } }

    private async Task AppOp(Action a) { await EngineHost.Instance.Run(a); RefreshApps(); }

    private void OpenApp_Click(object s, RoutedEventArgs e)    { var u = Tag(s); if (u.Length > 0) Launch(u); }
    private async void StartApp_Click(object s, RoutedEventArgs e)   { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.PySiteStart(n)); }
    private async void StopApp_Click(object s, RoutedEventArgs e)    { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.PySiteStop(n)); }
    private async void RestartApp_Click(object s, RoutedEventArgs e) { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.PySiteRestart(n)); }
    private async void RemoveApp_Click(object s, RoutedEventArgs e)  { var n = Tag(s); await AppOp(() => EngineHost.Instance.Engine.PySiteRemove(n)); }

    private async void PipApp_Click(object s, RoutedEventArgs e)
    {
        var n = Tag(s);
        Busy.IsActive = true;
        var (_, output) = await EngineHost.Instance.RunCaptured(() => EngineHost.Instance.Engine.PySitePip(n));
        Busy.IsActive = false;
        var body = string.IsNullOrWhiteSpace(output) ? "Done." : output.Trim();
        if (body.Length > 4000) body = body[^4000..];
        await new ContentDialog { Title = $"pip install · {n}", Content = new ScrollViewer { Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas") }, MaxHeight = 420 }, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync();
    }

    private void FolderApp_Click(object s, RoutedEventArgs e)   { var d = PySite.DirOf(Tag(s)); if (d.Length > 0) Launch(d); }
    private void LogsApp_Click(object s, RoutedEventArgs e)
    {
        var f = System.IO.Path.Combine(Paths.Logs, $"pysite-{Tag(s)}.log");
        if (System.IO.File.Exists(f)) Launch(f); else _ = Info("Logs", "No log yet — start the app first.");
    }

    private void EditorApp_Click(object s, RoutedEventArgs e)
    {
        var d = PySite.DirOf(Tag(s)); if (d.Length == 0) return;
        foreach (var ed in new[] { "code.cmd", "code.exe", "cursor.cmd", "cursor.exe", "subl.exe" })
        { try { Process.Start(new ProcessStartInfo { FileName = ed, Arguments = $"\"{d}\"", UseShellExecute = true }); return; } catch { } }
        Launch(d);   // no editor found — open the folder
    }

    private void TerminalApp_Click(object s, RoutedEventArgs e)
    {
        var d = PySite.DirOf(Tag(s)); if (d.Length == 0) return;
        try { Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{d}\"", UseShellExecute = true }); return; } catch { }
        try { Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoExit -Command \"Set-Location -LiteralPath '{d.Replace("'", "''")}'\"", UseShellExecute = true }); } catch { }
    }

    private Task Info(string title, string body) =>
        new ContentDialog { Title = title, Content = body, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync().AsTask();

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
        // Python must be installed for the app to run / venv to build — offer to install it first.
        if (!Tools.PythonInstalled)
        {
            var ask = new ContentDialog
            {
                Title = "Install Python first?",
                Content = "Adding a Python app needs the managed Python interpreter. Install it now? (one-time download)",
                PrimaryButtonText = "Install", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
            };
            if (await ask.ShowAsync() != ContentDialogResult.Primary) return;
            Busy.IsActive = true;
            await EngineHost.Instance.Run(() => EngineHost.Instance.Engine.Install("python"));
            Busy.IsActive = false; RefreshInterp();
            if (!Tools.PythonInstalled) { await Info("Python", "Python couldn't be installed — check the Logs and try again."); return; }
        }

        var name = new TextBox   { Header = "Site name", PlaceholderText = "e.g. myapi" };
        var dir  = new TextBox   { Header = "Project folder", PlaceholderText = @"C:\path\to\app" };
        var cmd  = new TextBox   { Header = "Run command", Text = "python app.py" };
        var port = new NumberBox { Header = "Port", Value = 8000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var venv = new ToggleSwitch { Header = "Create a virtualenv (.venv)", IsOn = true };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in new FrameworkElement[] { name, WithBrowse(dir), cmd, port, venv }) panel.Children.Add(c);

        var dlg = new ContentDialog
        {
            Title = "Add Python app", Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var nm = name.Text.Trim(); var d = dir.Text.Trim(); var cm = cmd.Text.Trim();
        int p = double.IsNaN(port.Value) ? 0 : (int)port.Value;
        if (nm.Length == 0 || d.Length == 0 || p <= 0) { await Info("Add Python app", "Name, project folder and a valid port are required."); return; }

        Busy.IsActive = true;
        await EngineHost.Instance.Run(() => EngineHost.Instance.Engine.PySiteAdd(nm, d, cm, p, venv.IsOn));
        Busy.IsActive = false;
        RefreshApps();
    }
}
