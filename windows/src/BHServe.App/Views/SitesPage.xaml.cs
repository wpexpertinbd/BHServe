using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

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
        _all = snap.Sites.Select(s => new SiteRow
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

        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var (ok, output) = await EngineHost.Instance.RunCaptured(
            () => EngineHost.Instance.Engine.SiteAdd(name, php: SelectedPhp, server: SelectedServer, type: SelectedType));
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();

        // Always show what happened — success, warnings (e.g. "no database server"), or the error.
        var title = !ok ? "Couldn't add site"
                  : output.Contains("✗") || output.Contains("failed") || output.Contains("not installed") ? "Added with warnings"
                  : $"Site '{name}' added";
        await Info(title, output.Length > 0 ? output : "Done.");
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
        await Info("Public URL", url is null ? "Tunnel did not return a URL yet â€” check the activity log." :
            $"{name} is now public at:\n\n{url}\n\n(Cloudflare quick tunnel â€” stops when you close it or run: bhserve tunnel stop {name})");
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

    private async void Nginx_Click(object s, RoutedEventArgs e)  => await Op(() => EngineHost.Instance.Engine.SiteServer(Tag(s), "nginx"));
    private async void Apache_Click(object s, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.SiteServer(Tag(s), "apache"));

    private async void Remove_Click(object s, RoutedEventArgs e)
    {
        var name = Tag(s);
        var dlg = new ContentDialog
        {
            Title = "Remove site", Content = $"Remove the vhost for '{name}'? (site files stay on disk)",
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Op(() => EngineHost.Instance.Engine.SiteRemove(name));
    }

    private static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
    }

    private System.Threading.Tasks.Task Info(string title, string body) =>
        new ContentDialog { Title = title, Content = body, CloseButtonText = "OK", XamlRoot = this.XamlRoot }.ShowAsync().AsTask();

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true; AddBtn.IsEnabled = false;
        var err = await EngineHost.Instance.Run(action);
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();
        if (err is not null) await Info("Couldn't complete that", err);
    }
}
