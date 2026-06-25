using System;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

/// <summary>Row shown in the sites list.</summary>
public sealed class SiteRow
{
    public required string Name { get; init; }
    public required string Domain { get; init; }
    public required string Php { get; init; }
    public required bool Secure { get; init; }
    public bool NotSecure => !Secure;
    public string Url => (Secure ? "https://" : "http://") + Domain;
    public Uri Uri => new(Url);
}

public sealed partial class SitesPage : Page
{
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
        SitesList.ItemsSource = snap.Sites.Select(s => new SiteRow
        {
            Name = s.Name, Domain = s.Domain, Php = s.Php, Secure = s.Secure,
        }).OrderBy(s => s.Name).ToList();
    }

    private string SelectedPhp => (PhpBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    private string SelectedType => (TypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "others";

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        await Op(() => EngineHost.Instance.Engine.SiteAdd(name, php: SelectedPhp, type: SelectedType));
        NameBox.Text = "";
    }

    private async void Secure_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string domain)
            await Op(() => EngineHost.Instance.Engine.Secure(domain));
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string name) return;
        var dlg = new ContentDialog
        {
            Title = "Remove site",
            Content = $"Remove the vhost for '{name}'? (site files stay on disk)",
            PrimaryButtonText = "Remove", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Op(() => EngineHost.Instance.Engine.SiteRemove(name));
    }

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true; AddBtn.IsEnabled = false;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false; AddBtn.IsEnabled = true;
        Refresh();
    }
}
