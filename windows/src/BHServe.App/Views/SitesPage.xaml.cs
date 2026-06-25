using System;
using System.Collections.Generic;
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

/// <summary>Row shown in the sites list.</summary>
public sealed class SiteRow
{
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public required string Php { get; init; }
    public required string Root { get; init; }
    public required bool Secure { get; init; }
    public required bool Enabled { get; init; }
    public bool NotSecure => !Secure;
    public string Url => (Secure ? "https://" : "http://") + Domain;
    public Uri Uri => new(Url);
    public Brush DotBrush => new SolidColorBrush(Enabled ? Colors.SeaGreen : Colors.Gray);
}

public sealed partial class SitesPage : Page
{
    private List<SiteRow> _all = new();

    public SitesPage()
    {
        InitializeComponent();
        var cfg = Config.Load();
        foreach (var v in BHServe.Core.Services.PhpVersions) PhpBox.Items.Add(new ComboBoxItem { Content = v });
        PhpBox.SelectedIndex = Math.Max(0, Array.IndexOf(BHServe.Core.Services.PhpVersions, cfg.DefaultPhp));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private async void Refresh()
    {
        Snapshot snap;
        try { snap = await EngineHost.Instance.Snapshot(); } catch { return; }
        _all = snap.Sites
            .Where(s => !Engine.IsTool(s.Name))   // phpMyAdmin/Adminer/Mailpit live under Web Tools, not here
            .Select(s => new SiteRow
            {
                Name = s.Name, Domain = s.Domain, Php = s.Php, Root = s.Root, Secure = s.Secure, Enabled = s.Enabled,
            }).OrderBy(s => s.Name).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        SitesList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || s.Domain.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Search_Changed(object s, TextChangedEventArgs e) => ApplyFilter();

    private string Tag(object s) => (s as FrameworkElement)?.Tag as string ?? "";
    private SiteRow? Row(string name) => _all.FirstOrDefault(r => r.Name == name);

    private string SelectedPhp => (PhpBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    private string SelectedType => (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "others";
    private string SelectedServer => (ServerBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "nginx";

    private async void Add_Click(object s, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { await Info("Add site", "Enter a site name first (lowercase letters, digits, hyphens)."); return; }

        // Read the UI controls HERE on the UI thread - the engine runs on a background
        // thread and touching XAML controls from there throws (RPC_E_WRONG_THREAD).
        string php = SelectedPhp, server = SelectedServer, type = SelectedType;
        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var (ok, output) = await EngineHost.Instance.RunCaptured(
            () => EngineHost.Instance.Engine.SiteAdd(name, php: php, server: server, type: type));
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();

        await ShowResult(name, ok, output);
        if (ok) NameBox.Text = "";
    }

    private void Open_Click(object s, RoutedEventArgs e)   { if (Row(Tag(s)) is { } r) Launch(r.Url); }
    private void Folder_Click(object s, RoutedEventArgs e) { if (Row(Tag(s)) is { } r && r.Root.Length > 0) Launch(r.Root); }
    private void Logs_Click(object s, RoutedEventArgs e)
    {
        var p = System.IO.Path.Combine(Paths.Logs, $"{Tag(s)}-error.log");
        if (System.IO.File.Exists(p)) Launch(p);
    }

    private async void Toggle_Click(object s, RoutedEventArgs e)
    {
        if (Row(Tag(s)) is { } r) await Op(() => EngineHost.Instance.Engine.SiteEnable(r.Name, !r.Enabled));
    }

    private async void Secure_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is string domain) await Op(() => EngineHost.Instance.Engine.Secure(domain));
    }

    private async void Share_Click(object s, RoutedEventArgs e)
    {
        var name = Tag(s);
        Busy.IsActive = true;
        await EngineHost.Instance.Run(() => EngineHost.Instance.Engine.Tunnel("start", name));
        Busy.IsActive = false;
        var url = BHServe.Core.Tunnel.Url(name);
        await Info("Public URL", url is null ? "Tunnel did not return a URL yet - check the activity log." :
            $"{name} is now public at:\n\n{url}\n\n(Cloudflare quick tunnel - stops when you close it or run: bhserve tunnel stop {name})");
    }

    private async void ChangePhp_Click(object s, RoutedEventArgs e)
    {
        var name = Tag(s);
        var combo = new ComboBox { Width = 140 };
        foreach (var v in BHServe.Core.Services.PhpVersions) combo.Items.Add(v);
        combo.SelectedIndex = 0;
        var dlg = new ContentDialog
        {
            Title = $"PHP version for {name}", Content = combo,
            PrimaryButtonText = "Apply", CloseButtonText = "Cancel", XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && combo.SelectedItem is string ver)
            await Op(() => EngineHost.Instance.Engine.SitePhp(name, ver));
    }

    private async void Nginx_Click(object s, RoutedEventArgs e)  { var n = Tag(s); await Op(() => EngineHost.Instance.Engine.SiteServer(n, "nginx")); }
    private async void Apache_Click(object s, RoutedEventArgs e) { var n = Tag(s); await Op(() => EngineHost.Instance.Engine.SiteServer(n, "apache")); }

    private async void Remove_Click(object s, RoutedEventArgs e)
    {
        var name = Tag(s);
        (string root, string db) t;
        try { t = EngineHost.Instance.Engine.SiteTargets(name); } catch { t = ("", name); }

        var warn = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Red), Visibility = Visibility.Collapsed,
            Text = $"Permanently deletes (cannot be undone):\n• Files:  {t.root}\n• Database:  {t.db}",
        };
        var purge = new CheckBox { Content = "Also delete the site files and drop its database" };
        purge.Checked   += (_, _) => warn.Visibility = Visibility.Visible;
        purge.Unchecked += (_, _) => warn.Visibility = Visibility.Collapsed;

        var panel = new StackPanel { Spacing = 6, MinWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = $"Remove '{name}'? By default this removes only the site mapping (vhost + hosts entry) and keeps your files and database.",
        });
        panel.Children.Add(purge);
        panel.Children.Add(warn);

        var dlg = new ContentDialog
        {
            Title = "Remove site", Content = panel,
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var doPurge = purge.IsChecked == true;   // read on the UI thread before the background op
        await Op(() => EngineHost.Instance.Engine.SiteRemove(name, doPurge, doPurge));
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
    }

    private System.Threading.Tasks.Task Info(string title, string body) =>
        new ContentDialog { Title = title, Content = body, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync().AsTask();

    // ---- pretty add-site result dialog ----
    private static readonly Color Green = Color.FromArgb(255, 0x16, 0xA3, 0x4A);
    private static readonly Color Amber = Color.FromArgb(255, 0xD9, 0x77, 0x06);
    private static readonly Color Red   = Color.FromArgb(255, 0xDC, 0x26, 0x26);
    private static readonly Color Muted = Color.FromArgb(255, 0x9C, 0xA3, 0xAF);

    // Segoe Fluent / MDL2 glyphs (escaped so they always survive editing).
    private static readonly string GCheck = ((char)0xE73E).ToString();   // CheckMark
    private static readonly string GWarn  = ((char)0xE7BA).ToString();   // Warning
    private static readonly string GError = ((char)0xE711).ToString();   // Cancel (x)

    // The engine's stdout markers it prepends to each line.
    private const char Tick = (char)0x2713;
    private const char Cross = (char)0x2717;

    private static FontIcon Marker(string g, Color c) => new()
    {
        Glyph = g, FontSize = 13, Foreground = new SolidColorBrush(c),
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
    };

    /// <summary>Pretty add-site result: colored status header, the URL with an Open button,
    /// and the engine's steps rendered as a checklist (instead of a raw monospace dump).</summary>
    private async Task ShowResult(string name, bool ok, string output)
    {
        var warned = ok && (output.IndexOf(Cross) >= 0 || output.Contains(" ! ") || output.Contains("failed") || output.Contains("not installed"));
        var (glyph, color, heading) =
            !ok    ? (GError, Red,   "Couldn't add site")
          : warned ? (GWarn,  Amber, $"'{name}' added with warnings")
          :          (GCheck, Green, $"Site '{name}' added");

        var root = new StackPanel { Spacing = 14, MinWidth = 400 };

        // header: round icon chip + heading
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        head.Children.Add(new Border
        {
            Width = 40, Height = 40, VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(28, color.R, color.G, color.B)),
            Child = new FontIcon { Glyph = glyph, FontSize = 20, Foreground = new SolidColorBrush(color) },
        });
        head.Children.Add(new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(head);

        // the site url, in the accent color
        var url = Regex.Match(output, @"https?://[^\s]+").Value;
        if (url.Length > 0)
            root.Children.Add(new TextBlock { Text = url, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });

        // steps checklist
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

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
            row.Children.Add(leading);
            row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85, MaxWidth = 360 });
            steps.Children.Add(row);
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

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var err = await EngineHost.Instance.Run(action);
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();
        if (err is not null) await Info("Couldn't complete that", err);
    }
}
