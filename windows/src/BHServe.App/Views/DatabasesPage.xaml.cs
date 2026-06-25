using System;
using System.Diagnostics;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed partial class DatabasesPage : Page
{
    public DatabasesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private async void Refresh()
    {
        var (running, dbs) = await System.Threading.Tasks.Task.Run(() =>
            (DbServer.Running(), DbServer.Running() ? Database.List().ToList() : new System.Collections.Generic.List<string>()));
        ServerStatus.Text = running
            ? $"Running on 127.0.0.1:3306 (root · no password) · {dbs.Count} database(s)"
            : "Stopped — start the server to manage databases";
        StartDbBtn.IsEnabled = !running;
        StopDbBtn.IsEnabled = running;
        DbList.ItemsSource = dbs;
    }

    private async void StartDb_Click(object s, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.Start("mariadb"));
    private async void StopDb_Click(object s, RoutedEventArgs e)  => await Op(() => EngineHost.Instance.Engine.Stop("mariadb"));

    private async void Create_Click(object s, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        var pw = PassBox.Text;
        await Op(() => EngineHost.Instance.Engine.Db("create", pw.Length > 0 ? new[] { name, pw } : new[] { name }));
        NameBox.Text = ""; PassBox.Text = "";
    }

    private async void Drop_Click(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not string name) return;
        var dlg = new ContentDialog
        {
            Title = "Drop database",
            Content = $"Permanently drop '{name}'? This cannot be undone.",
            PrimaryButtonText = "Drop", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Op(() => EngineHost.Instance.Engine.Db("drop", name));
    }

    private async void Pma_Click(object s, RoutedEventArgs e)     => await Tool(() => EngineHost.Instance.Engine.PhpMyAdmin(), "http://phpmyadmin." + Tld());
    private async void Adminer_Click(object s, RoutedEventArgs e) => await Tool(() => EngineHost.Instance.Engine.Adminer(),   "http://adminer." + Tld());
    private async void Mailpit_Click(object s, RoutedEventArgs e) => await Tool(() => EngineHost.Instance.Engine.Mailpit(),   "http://127.0.0.1:8025");

    private static string Tld() => Config.Load().Tld;

    private async System.Threading.Tasks.Task Tool(Action action, string url)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Refresh();
    }
}
