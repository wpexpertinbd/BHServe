using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed class DbRow
{
    public required string Name { get; init; }
    public required bool IsMysql { get; init; }
    public string Engine => IsMysql ? "MySQL" : "PostgreSQL";
    public string Key => (IsMysql ? "my:" : "pg:") + Name;
    public Visibility MysqlVis => IsMysql ? Visibility.Visible : Visibility.Collapsed;
}

public sealed partial class DatabasesPage : Page
{
    public DatabasesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private async void Refresh()
    {
        var snap = await Task.Run(() => (
            myRunning: DbServer.Running(),
            myDbs:     DbServer.Running() ? Database.List().ToList() : new List<string>(),
            pgInstalled: Tools.PostgresExe() is not null,
            pgRunning: PgServer.Running(),
            pgDbs:     PgServer.Running() ? PgDatabase.List().ToList() : new List<string>()));

        var hasRootPw = Config.Load().RootPassword.Length > 0;
        RootPwStatus.Text = snap.myRunning ? (hasRootPw ? "running · root password set" : "running · no password") : "stopped";
        StartDbBtn.IsEnabled = !snap.myRunning;
        StopDbBtn.IsEnabled = snap.myRunning;

        PgStatus.Text = !snap.pgInstalled ? "not installed" : snap.pgRunning ? "running" : "stopped";
        InstallPgBtn.Visibility = snap.pgInstalled ? Visibility.Collapsed : Visibility.Visible;
        StartPgBtn.IsEnabled = snap.pgInstalled && !snap.pgRunning;
        StopPgBtn.IsEnabled = snap.pgRunning;

        var rows = snap.myDbs.Select(d => new DbRow { Name = d, IsMysql = true })
            .Concat(snap.pgDbs.Select(d => new DbRow { Name = d, IsMysql = false })).ToList();
        DbList.ItemsSource = rows;
        EmptyDbs.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartDb_Click(object s, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.Start("mariadb"));
    private async void StopDb_Click(object s, RoutedEventArgs e)  => await Op(() => EngineHost.Instance.Engine.Stop("mariadb"));
    private async void StartPg_Click(object s, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.Start("postgresql"));
    private async void StopPg_Click(object s, RoutedEventArgs e)  => await Op(() => EngineHost.Instance.Engine.Stop("postgresql"));
    private async void InstallPg_Click(object s, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.Install("postgresql"));

    private bool IsPgSelected => (EngineBox.SelectedItem as ComboBoxItem)?.Content?.ToString() == "PostgreSQL";

    private async void Create_Click(object s, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) return;
        if (IsPgSelected)
        {
            await Op(() => EngineHost.Instance.Engine.Pg("create", name));
        }
        else
        {
            var pw = PassBox.Text;
            await Op(() => EngineHost.Instance.Engine.Db("create", pw.Length > 0 ? new[] { name, pw } : new[] { name }));
        }
        NameBox.Text = ""; PassBox.Text = "";
    }

    private async void Drop_Click(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not string key) return;
        var isMy = key.StartsWith("my:");
        var name = key[3..];
        var dlg = new ContentDialog
        {
            Title = "Drop database", Content = $"Permanently drop '{name}' ({(isMy ? "MySQL" : "PostgreSQL")})? This cannot be undone.",
            PrimaryButtonText = "Drop", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Op(() => { if (isMy) EngineHost.Instance.Engine.Db("drop", name); else EngineHost.Instance.Engine.Pg("drop", name); });
    }

    private static string GenPassword(int len = 16)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(len);
        return new string(bytes.Select(b => chars[b % chars.Length]).ToArray());
    }

    private void GenPw_Click(object s, RoutedEventArgs e) => PassBox.Text = GenPassword();

    private async void RootPw_Click(object s, RoutedEventArgs e)
    {
        var box = new TextBox { Header = "New root password (leave blank to remove)", Width = 300, Text = Config.Load().RootPassword };
        var gen = new Button { Content = "Generate" };
        gen.Click += (_, _) => box.Text = GenPassword();
        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(box); panel.Children.Add(gen);
        panel.Children.Add(new TextBlock { Text = "Changes the MySQL root password BHServe uses everywhere (new WordPress sites + phpMyAdmin). Local-dev only.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var dlg = new ContentDialog { Title = "Root password", Content = panel, PrimaryButtonText = "Apply", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var pw = box.Text;
        await Op(() => EngineHost.Instance.Engine.Db("rootpw", pw));
    }

    private async void DbPassword_Click(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.Tag is not string name) return;
        var box = new TextBox { Header = $"Password for a dedicated user '{name}' (@localhost + @127.0.0.1)", Width = 320 };
        var gen = new Button { Content = "Generate" };
        gen.Click += (_, _) => box.Text = GenPassword();
        var panel = new StackPanel { Spacing = 8, MinWidth = 340 };
        panel.Children.Add(box); panel.Children.Add(gen);
        var dlg = new ContentDialog { Title = $"Set password · {name}", Content = panel, PrimaryButtonText = "Set", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = this.XamlRoot };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        var pw = box.Text;
        if (pw.Length == 0) return;
        await Op(() => EngineHost.Instance.Engine.Db("passwd", name, pw));
    }

    private async void Pma_Click(object s, RoutedEventArgs e)     => await Tool(() => EngineHost.Instance.Engine.PhpMyAdmin(), "http://phpmyadmin." + Tld());
    private async void Adminer_Click(object s, RoutedEventArgs e) => await Tool(() => EngineHost.Instance.Engine.Adminer(),   "http://adminer." + Tld());
    private async void Mailpit_Click(object s, RoutedEventArgs e) => await Tool(() => EngineHost.Instance.Engine.Mailpit(),   "http://127.0.0.1:8025");

    private static string Tld() => Config.Load().Tld;

    private async Task Tool(Action action, string url)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    private async Task Op(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Refresh();
    }
}
