using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace BHServe.App.Views;

/// <summary>One row in a website list.</summary>
public sealed class SiteRow
{
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public required string Php { get; init; }
    public required string Root { get; init; }
    public required bool Secure { get; init; }
    public required bool Enabled { get; init; }
    public required string Server { get; init; }
    public bool NotSecure => !Secure;
    public string Url => (Secure ? "https://" : "http://") + Domain;
    public Uri Uri => new(Url);
    public string Badge => !string.IsNullOrEmpty(Php) && Php != "-" ? $"{Server} · php {Php}" : Server;
    public Brush DotBrush => new SolidColorBrush(Enabled ? Colors.SeaGreen : Colors.Gray);
}

/// <summary>Reusable website list: Show-count + search + per-site actions + pagination/jump.
/// Used on both the Dashboard and the Sites page. Raises <see cref="Changed"/> after any action
/// so the host re-pulls data and calls <see cref="SetData"/> again.</summary>
public sealed partial class SiteListControl : UserControl
{
    private static readonly Color Red = Color.FromArgb(255, 0xDC, 0x26, 0x26);

    private List<SiteRow> _all = new();
    private int _page = 1;
    private bool _ready;

    public event EventHandler? Changed;

    public SiteListControl() => InitializeComponent();

    /// <summary>Apply the configured default page size to the Show box. If the value isn't one of the
    /// standard options (e.g. 5), insert it so the list actually defaults to exactly that number.</summary>
    public void SetDefaultPageSize(int size)
    {
        if (size < 1) size = 1;
        var match = ShowBox.Items.OfType<ComboBoxItem>()
                          .FirstOrDefault(i => i.Content?.ToString() == size.ToString());
        if (match is null)
        {
            match = new ComboBoxItem { Content = size.ToString() };
            ShowBox.Items.Insert(0, match);   // custom value at the top, before the standard options
        }
        ShowBox.SelectedItem = match;
        _ready = true;
    }

    private int PageSize =>
        (ShowBox.SelectedItem as ComboBoxItem)?.Content?.ToString() is "All" ? int.MaxValue :
        int.TryParse((ShowBox.SelectedItem as ComboBoxItem)?.Content?.ToString(), out var n) ? n : 15;

    /// <summary>Replace the data; preserves the current search + page where possible.</summary>
    public void SetData(IEnumerable<SiteRow> sites)
    {
        if (!_ready) _ready = true;
        _all = sites.ToList();
        Render();
    }

    private List<SiteRow> Filtered()
    {
        var q = SearchBox.Text.Trim();
        return q.Length == 0
            ? _all
            : _all.Where(s => s.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                           || s.Domain.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Render()
    {
        var filtered = Filtered();
        var size = PageSize;
        var pages = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)size));
        if (_page > pages) _page = pages;
        if (_page < 1) _page = 1;

        var slice = size == int.MaxValue ? filtered : filtered.Skip((_page - 1) * size).Take(size).ToList();
        // only reassign when changed (avoid the 2s-tick flicker on the dashboard)
        var sig = string.Join(";", slice.Select(r => $"{r.Name}|{r.Domain}|{r.Enabled}|{r.Secure}|{r.Php}|{r.Server}")) + $"#{_page}/{pages}";
        if (sig != _lastSig) { List.ItemsSource = slice; _lastSig = sig; }

        Empty.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Pager.Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        PageLabel.Text = $"Page {_page} of {pages}";
        PrevBtn.IsEnabled = _page > 1;
        NextBtn.IsEnabled = _page < pages;
        JumpBox.Maximum = pages;
    }
    private string _lastSig = "";

    private void Show_Changed(object s, SelectionChangedEventArgs e) { _page = 1; if (_ready) Render(); }
    private void Search_Changed(object s, TextChangedEventArgs e) { _page = 1; Render(); }
    private void Prev_Click(object s, RoutedEventArgs e) { if (_page > 1) { _page--; Render(); } }
    private void Next_Click(object s, RoutedEventArgs e) { _page++; Render(); }
    private void Go_Click(object s, RoutedEventArgs e)
    {
        if (!double.IsNaN(JumpBox.Value) && JumpBox.Value >= 1) { _page = (int)JumpBox.Value; Render(); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────
    private static string Tag(object s) => (s as FrameworkElement)?.Tag as string ?? "";
    private SiteRow? Row(string name) => _all.FirstOrDefault(r => r.Name == name);
    private static void Launch(string t) { try { Process.Start(new ProcessStartInfo { FileName = t, UseShellExecute = true }); } catch { } }
    private Task Info(string title, string body) =>
        new ContentDialog { Title = title, Content = body, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync().AsTask();

    private async Task Op(Action action)
    {
        Busy.IsActive = true;
        var err = await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Changed?.Invoke(this, EventArgs.Empty);
        if (err is not null) await Info("Couldn't complete that", err);
    }

    // ── per-site actions ─────────────────────────────────────────────────────────
    private void Open_Click(object s, RoutedEventArgs e)   { if (Row(Tag(s)) is { } r) Launch(r.Url); }
    private void Folder_Click(object s, RoutedEventArgs e) { if (Row(Tag(s)) is { } r && r.Root.Length > 0) Launch(r.Root); }

    /// <summary>Open the site folder in the first code editor we can find (VS Code → Cursor → Sublime →
    /// Notepad++). Falls back to opening the folder in Explorer if none is installed.</summary>
    private async void CodeEditor_Click(object s, RoutedEventArgs e)
    {
        if (Row(Tag(s)) is not { } r || r.Root.Length == 0) return;
        var editor = FindEditor();
        if (editor is null)
        {
            Launch(r.Root);   // no editor — at least open the folder
            await Info("No code editor found", "Couldn't find VS Code, Cursor, Sublime Text or Notepad++. Opened the site folder instead — install one of those to use this.");
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = editor, Arguments = $"\"{r.Root}\"", UseShellExecute = true }); }
        catch { Launch(r.Root); }
    }

    /// <summary>Open a terminal at the site folder — Windows Terminal if present, else PowerShell, else cmd.</summary>
    private void Terminal_Click(object s, RoutedEventArgs e)
    {
        if (Row(Tag(s)) is not { } r || r.Root.Length == 0) return;
        try { Process.Start(new ProcessStartInfo { FileName = "wt.exe", Arguments = $"-d \"{r.Root}\"", UseShellExecute = true }); return; } catch { }
        try { Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoExit -Command \"Set-Location -LiteralPath '{r.Root.Replace("'", "''")}'\"", UseShellExecute = true }); return; } catch { }
        try { Process.Start(new ProcessStartInfo { FileName = "cmd.exe", Arguments = $"/K cd /d \"{r.Root}\"", UseShellExecute = true }); } catch { }
    }

    private static string Env(string v) => Environment.GetEnvironmentVariable(v) ?? "";

    /// <summary>Locate an installed code editor (known install paths first, then PATH).</summary>
    private static string? FindEditor()
    {
        var candidates = new (string[] paths, string[] cmds)[]
        {
            (new[] { System.IO.Path.Combine(Env("LOCALAPPDATA"), "Programs", "Microsoft VS Code", "Code.exe"),
                     System.IO.Path.Combine(Env("ProgramFiles"),  "Microsoft VS Code", "Code.exe") },          new[] { "code.cmd", "code.exe" }),
            (new[] { System.IO.Path.Combine(Env("LOCALAPPDATA"), "Programs", "cursor", "Cursor.exe") },         new[] { "cursor.cmd", "cursor.exe" }),
            (new[] { System.IO.Path.Combine(Env("ProgramFiles"), "Sublime Text", "sublime_text.exe"),
                     System.IO.Path.Combine(Env("ProgramFiles"), "Sublime Text 3", "sublime_text.exe") },      new[] { "subl.exe" }),
            (new[] { System.IO.Path.Combine(Env("ProgramFiles"),      "Notepad++", "notepad++.exe"),
                     System.IO.Path.Combine(Env("ProgramFiles(x86)"), "Notepad++", "notepad++.exe") },         new[] { "notepad++.exe" }),
        };
        foreach (var (paths, cmds) in candidates)
        {
            foreach (var p in paths) if (p.Length > 0 && System.IO.File.Exists(p)) return p;
            foreach (var c in cmds) if (OnPath(c) is { } hit) return hit;
        }
        return null;
    }

    private static string? OnPath(string exe)
    {
        foreach (var dir in Env("PATH").Split(System.IO.Path.PathSeparator))
        {
            try { var p = System.IO.Path.Combine(dir.Trim(), exe); if (System.IO.File.Exists(p)) return p; } catch { }
        }
        return null;
    }
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
        // Start the tunnel (if it isn't already live), showing the busy ring while cloudflared connects.
        // The FIRST share auto-downloads cloudflared (no command needed), so this can take a few extra
        // seconds; on failure we surface the engine's real output instead of a generic message.
        Busy.IsActive = true;
        var output = "";
        if (!BHServe.Core.Tunnel.Running(name))
            (_, output) = await EngineHost.Instance.RunCaptured(() => EngineHost.Instance.Engine.Tunnel("start", name));
        Busy.IsActive = false;

        var url = BHServe.Core.Tunnel.Url(name);
        if (url is null)
        {
            var detail = string.IsNullOrWhiteSpace(output)
                ? "The tunnel didn't return a public URL. Check Logs, then try again."
                : output.Trim();
            await Info("Couldn't share publicly", detail);
            return;
        }
        await ShowShareDialog(name, url);
    }

    /// <summary>Mac-parity "Share publicly" sheet: live status + copy + open-in-browser + stop sharing.</summary>
    private async Task ShowShareDialog(string name, string url)
    {
        var accent = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        var green  = new SolidColorBrush(Color.FromArgb(255, 0x16, 0xA3, 0x4A));

        // header: broadcast icon + title (ContentDialog.Title accepts any object)
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 18, Foreground = accent, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = $"Share “{name}” publicly", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        var root = new StackPanel { Spacing = 14, MinWidth = 420 };
        root.Children.Add(new TextBlock
        {
            Text = "Cloudflare Tunnel gives this site a temporary public https address — no account or port-forwarding. The link works while sharing is on.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8,
        });

        // live status
        var live = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        live.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9, Fill = green, VerticalAlignment = VerticalAlignment.Center });
        live.Children.Add(new TextBlock { Text = "Live — anyone with this link can reach your site.", Foreground = green, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        root.Children.Add(live);

        // url + copy + open
        var urlRow = new Grid { ColumnSpacing = 6 };
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var urlBox = new TextBox { Text = url, IsReadOnly = true, IsSpellCheckEnabled = false, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(urlBox, 0);

        var copyBtn = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTipService.SetToolTip(copyBtn, "Copy link");
        copyBtn.Click += async (_, _) =>
        {
            try
            {
                var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dp.SetText(url);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            }
            catch { }
            if (copyBtn.Content is FontIcon ic) { var old = ic.Glyph; ic.Glyph = ""; await Task.Delay(1200); ic.Glyph = old; }
        };
        Grid.SetColumn(copyBtn, 1);

        var openBtn = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTipService.SetToolTip(openBtn, "Open in browser");
        openBtn.Click += (_, _) => Launch(url);
        Grid.SetColumn(openBtn, 2);

        urlRow.Children.Add(urlBox);
        urlRow.Children.Add(copyBtn);
        urlRow.Children.Add(openBtn);
        root.Children.Add(urlRow);

        var dlg = new ContentDialog
        {
            Title = header,
            Content = root,
            PrimaryButtonText = "Stop sharing",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            Busy.IsActive = true;
            await EngineHost.Instance.Run(() => EngineHost.Instance.Engine.Tunnel("stop", name));
            Busy.IsActive = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
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

    private async void ChangeRoot_Click(object s, RoutedEventArgs e)
    {
        var name = Tag(s);
        var path = await Picker.FolderAsync();
        if (string.IsNullOrEmpty(path)) return;
        await Op(() => EngineHost.Instance.Engine.SiteRoot(name, path));
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
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = $"Remove '{name}'? By default this removes only the site mapping and keeps your files and database." });
        panel.Children.Add(purge);
        panel.Children.Add(warn);

        var dlg = new ContentDialog
        {
            Title = "Remove site", Content = panel,
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var doPurge = purge.IsChecked == true;
        await Op(() => EngineHost.Instance.Engine.SiteRemove(name, doPurge, doPurge));
    }
}
