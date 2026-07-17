using System;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace BHServe.App.Views;

public sealed partial class SitesPage : Page
{
    private bool _pageSizeSet;
    private string? _customRoot;   // optional custom site root chosen via the folder button

    public SitesPage()
    {
        InitializeComponent();
        var cfg = Config.Load();
        foreach (var v in BHServe.Core.Services.PhpVersions) PhpBox.Items.Add(new ComboBoxItem { Content = v });
        PhpBox.SelectedIndex = Math.Max(0, Array.IndexOf(BHServe.Core.Services.PhpVersions, cfg.DefaultPhp));
        SiteList.Changed += (_, _) => Refresh();   // re-pull after any per-site action
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Refresh();
        // Arriving via the Dashboard "Add site" button → put the cursor in the name box, ready to type.
        if (e.Parameter as string == "add")
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => { try { NameBox.Focus(FocusState.Programmatic); } catch { } });
    }

    private async void Refresh()
    {
        if (!_pageSizeSet) { SiteList.SetDefaultPageSize(Config.Load().SitesPageSize); _pageSizeSet = true; }
        Snapshot snap;
        try { snap = await EngineHost.Instance.Snapshot(); } catch { return; }
        SiteList.SetData(snap.Sites.Where(s => !Engine.IsTool(s.Name)).OrderBy(s => s.Name).Select(ToRow));
    }

    private static SiteRow ToRow(Site s) => new()
    { Name = s.Name, Domain = s.Domain, Php = s.Php, Root = s.Root, Secure = s.Secure, Enabled = s.Enabled, Server = s.Server };

    // ── add row ──────────────────────────────────────────────────────────────────
    private string SelectedPhp => (PhpBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    // Read the real server key from Tag (the Content is a descriptive label like "nginx (serves PHP)").
    private string SelectedServer => (ServerBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "nginx";
    private string SelectedType => ((TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "") switch
    {
        "WordPress"  => "wordpress",
        "PHP"        => "php",
        "Node app"   => "node",
        "Python app" => "python",
        _            => "others",
    };

    // PHP version + web server + HTTPS toggle don't apply to a reverse-proxied app (Node/Python) —
    // grey them out when one is selected (those types have their own add flow).
    private void Type_Changed(object s, SelectionChangedEventArgs e)
    {
        var isProc = SelectedType is "node" or "python";
        if (PhpBox != null)    PhpBox.IsEnabled = !isProc;
        if (ServerBox != null) ServerBox.IsEnabled = !isProc;
        if (SslBox != null)    SslBox.IsEnabled = !isProc;
    }

    private async void Add_Click(object s, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { await Info("Add site", "Enter a site name first (lowercase letters, digits, hyphens)."); return; }

        string php = SelectedPhp, server = SelectedServer, type = SelectedType;

        // Most users skip the readme and try to add a site with no servers installed -> a dead site.
        // Make sure the required stack (web server + PHP + DB) is installed AND running first.
        if (!await EnsureRequirements(type, php, server)) return;

        if (type == "node") { await AddNodeApp(name); return; }
        if (type == "python") { await AddPythonApp(name); return; }

        string? root = _customRoot;
        var wantSsl = SslBox.IsChecked == true;
        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var (ok, output) = await EngineHost.Instance.RunCaptured(
            () => EngineHost.Instance.Engine.SiteAdd(name, php: php, root: root, server: server, type: type));
        // Provision the cert in the same flow when HTTPS is ticked — best-effort: a cert failure (e.g.
        // mkcert missing) is shown as a warning but the site itself stays added (ok reflects SiteAdd).
        if (ok && wantSsl)
        {
            var (_, sslOut) = await EngineHost.Instance.RunCaptured(
                () => EngineHost.Instance.Engine.Secure($"{name}.{Config.Load().Tld}"));
            if (!string.IsNullOrWhiteSpace(sslOut)) output += "\n" + sslOut;
        }
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();
        await ShowResult(name, ok, output);
        if (ok) { NameBox.Text = ""; ClearCustomRoot(); }
    }

    /// <summary>Block "Add site" until the servers this site needs are installed + running. If any are
    /// missing, ask once to install them (one-time download), then start them. Returns false if the
    /// user cancels or a required component couldn't be installed.</summary>
    private async Task<bool> EnsureRequirements(string type, string php, string server)
    {
        var missing = EngineHost.Instance.Engine.MissingForSite(type, php, server);
        if (missing.Count > 0)
        {
            var list = string.Join("\n", missing.Select(m => "        •  " + m.label));
            var ask = new ContentDialog
            {
                Title = "Install required components first",
                Content = $"To create this site, BHServe needs to install:\n\n{list}\n\nInstall them now? This is a one-time download.",
                PrimaryButtonText = "Install & continue", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
            };
            if (await ask.ShowAsync() != ContentDialogResult.Primary) return false;
        }

        // Install anything missing + start the services this site needs (also covers an installed-but-
        // stopped stack — a site won't serve if nginx/PHP/DB aren't running).
        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var (_, output) = await EngineHost.Instance.RunCaptured(
            () => EngineHost.Instance.Engine.EnsureSiteServices(type, php, server));
        Busy.IsActive = false; AddBtn.IsEnabled = true;

        var still = EngineHost.Instance.Engine.MissingForSite(type, php, server);
        if (still.Count > 0)
        {
            await new ContentDialog
            {
                Title = "Setup didn't finish",
                Content = "These couldn't be installed:\n\n" + string.Join("\n", still.Select(m => "        •  " + m.label)) +
                          "\n\n" + (string.IsNullOrWhiteSpace(output) ? "Check the Services tab and try again." : output.Trim()),
                CloseButtonText = "OK", XamlRoot = this.XamlRoot,
            }.ShowAsync();
            return false;
        }
        return true;
    }

    /// <summary>Pick an optional custom root folder for the next Add (defaults to the Sites root).</summary>
    private async void PickRoot_Click(object s, RoutedEventArgs e)
    {
        var path = await Picker.FolderAsync();
        if (string.IsNullOrEmpty(path)) return;
        _customRoot = path;
        ToolTipService.SetToolTip(RootBtn, $"Site root: {path}  (click to change)");
        RootBtn.Style = (Style)Application.Current.Resources["AccentButtonStyle"];   // highlight = a custom root is set
    }

    private void ClearCustomRoot()
    {
        _customRoot = null;
        RootBtn.ClearValue(StyleProperty);
        ToolTipService.SetToolTip(RootBtn, "Site root folder (optional — defaults to the Sites root)");
    }

    /// <summary>Node-app setup sheet (revealed when Type = Node app), then create + show the result.</summary>
    private async Task AddNodeApp(string name)
    {
        var feDir = new TextBox  { Header = "Frontend folder", PlaceholderText = @"C:\path\to\frontend" };
        var feCmd = new TextBox  { Header = "Frontend command", Text = "npm run dev" };
        var fePort = new NumberBox { Header = "Frontend port", Value = 3000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var beDir = new TextBox  { Header = "Backend folder (optional)" };
        var beCmd = new TextBox  { Header = "Backend command (optional)", PlaceholderText = "npm start" };
        var bePort = new NumberBox { Header = "Backend port (optional)", Value = double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var api   = new TextBox  { Header = "API path → backend", Text = "/api" };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in new FrameworkElement[] { feDir, feCmd, fePort, beDir, beCmd, bePort, api }) panel.Children.Add(c);

        var dlg = new ContentDialog
        {
            Title = $"Node app · {name}", Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        string feD = feDir.Text.Trim(), feC = feCmd.Text.Trim(), beD = beDir.Text.Trim(),
               beC = beCmd.Text.Trim(), apiP = api.Text.Trim();
        int feP = (int)fePort.Value, bp = double.IsNaN(bePort.Value) ? 0 : (int)bePort.Value;
        if (feD.Length == 0) { await Info("Node app", "A frontend folder is required."); return; }

        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var (ok, output) = await EngineHost.Instance.RunCaptured(
            () => EngineHost.Instance.Engine.NodeSiteAdd(name, feD, feC, feP, beD, beC, bp, apiP));
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();
        await ShowResult(name, ok, output);
        if (ok) NameBox.Text = "";
    }

    /// <summary>Python-app setup sheet (Type = Python app), then create + show the result. nginx +
    /// python were already ensured by EnsureRequirements before this is called.</summary>
    private async Task AddPythonApp(string name)
    {
        var dir  = new TextBox   { Header = "Project folder", PlaceholderText = @"C:\path\to\app" };
        var cmd  = new TextBox   { Header = "Run command", Text = "python app.py" };
        var port = new NumberBox { Header = "Port", Value = 8000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
        var venv = new ToggleSwitch { Header = "Create a virtualenv (.venv)", IsOn = true };
        var hint = new TextBlock { Text = "Your app gets a PORT env var — read os.environ['PORT'] in code, or use %PORT% in the command (e.g. gunicorn app:app -b 127.0.0.1:%PORT%).", TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12 };
        var panel = new StackPanel { Spacing = 8 };
        foreach (var c in new FrameworkElement[] { dir, cmd, port, venv, hint }) panel.Children.Add(c);

        var dlg = new ContentDialog
        {
            Title = $"Python app · {name}", Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            PrimaryButtonText = "Create", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var d = dir.Text.Trim(); var cm = cmd.Text.Trim();
        int p = double.IsNaN(port.Value) ? 0 : (int)port.Value;
        if (d.Length == 0) { await Info("Python app", "A project folder is required."); return; }
        if (p <= 0)        { await Info("Python app", "A valid port is required."); return; }

        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var (ok, output) = await EngineHost.Instance.RunCaptured(
            () => EngineHost.Instance.Engine.PySiteAdd(name, d, cm, p, venv.IsOn));
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();
        await ShowResult(name, ok, output);
        if (ok) NameBox.Text = "";
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
    }

    private Task Info(string title, string body) =>
        new ContentDialog { Title = title, Content = body, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync().AsTask();

    // ── pretty add-site result dialog ────────────────────────────────────────────
    private static readonly Color Green = Color.FromArgb(255, 0x16, 0xA3, 0x4A);
    private static readonly Color Amber = Color.FromArgb(255, 0xD9, 0x77, 0x06);
    private static readonly Color Red   = Color.FromArgb(255, 0xDC, 0x26, 0x26);
    private static readonly Color Muted = Color.FromArgb(255, 0x9C, 0xA3, 0xAF);
    private static readonly string GCheck = ((char)0xE73E).ToString();
    private static readonly string GWarn  = ((char)0xE7BA).ToString();
    private static readonly string GError = ((char)0xE711).ToString();
    private const char Tick = (char)0x2713;
    private const char Cross = (char)0x2717;

    private static FontIcon Marker(string g, Color c) => new()
    {
        Glyph = g, FontSize = 13, Foreground = new SolidColorBrush(c),
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
    };

    private async Task ShowResult(string name, bool ok, string output)
    {
        var warned = ok && (output.IndexOf(Cross) >= 0 || output.Contains(" ! ") || output.Contains("failed") || output.Contains("not installed"));
        var (glyph, color, heading) =
            !ok    ? (GError, Red,   "Couldn't add site")
          : warned ? (GWarn,  Amber, $"'{name}' added with warnings")
          :          (GCheck, Green, $"Site '{name}' added");

        var root = new StackPanel { Spacing = 14, MinWidth = 400 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new Border
        {
            Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center, CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)),
            Child = new FontIcon { Glyph = glyph, FontSize = 20, Foreground = new SolidColorBrush(color) },
        });
        head.Children.Add(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(head);

        // Prefer the https URL (printed by Secure) when SSL was enabled; else fall back to http.
        var url = Regex.Match(output, @"https://[^\s]+").Value;
        if (url.Length == 0) url = Regex.Match(output, @"https?://[^\s]+").Value;
        if (url.Length > 0)
            root.Children.Add(new TextBlock { Text = url, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });

        var steps = new StackPanel { Spacing = 7 };
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.StartsWith("Site '") || t.StartsWith("url ") || t.StartsWith("root ") || t.StartsWith("php ")) continue;

            FrameworkElement leading; string text;
            if      (t[0] == Tick)  { leading = Marker(GCheck, Green); text = t[1..].Trim(); }
            else if (t[0] == '!')   { leading = Marker(GWarn,  Amber); text = t[1..].Trim(); }
            else if (t[0] == Cross) { leading = Marker(GError, Red);   text = t[1..].Trim(); }
            else { leading = new TextBlock { Text = ((char)0x2022).ToString(), FontSize = 15, Foreground = new SolidColorBrush(Muted), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 0, 0, 0) }; text = t; }

            var rowp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            rowp.Children.Add(leading);
            rowp.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, MaxWidth = 360 });
            steps.Children.Add(rowp);
        }
        root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            Child = steps,
        });

        var dlg = new ContentDialog { Content = root, CloseButtonText = "Done", XamlRoot = this.XamlRoot };
        if (ok && url.Length > 0) { dlg.PrimaryButtonText = "Open site"; dlg.DefaultButton = ContentDialogButton.Primary; }
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && url.Length > 0) Launch(url);
    }
}
