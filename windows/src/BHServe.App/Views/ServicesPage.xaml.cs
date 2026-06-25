using System;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

/// <summary>Row shown in the services list.</summary>
public sealed class SvcRow
{
    public required string Key { get; init; }
    public required bool Installed { get; init; }
    public required bool Running { get; init; }
    public required bool Manageable { get; init; }   // nginx + php have start/stop wired
    public bool NotInstalled => !Installed;
    public bool CanStart => Installed && !Running && Manageable;
    public bool IsPhp => Key.StartsWith("php");
    public Microsoft.UI.Xaml.Visibility PhpVis => IsPhp ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public string StatusText =>
        (Installed ? "installed" : "not installed") + (Running ? " · running" : "");
}

public sealed partial class ServicesPage : Page
{
    public ServicesPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private async void Refresh()
    {
        Snapshot snap;
        try { snap = await EngineHost.Instance.Snapshot(); } catch { return; }
        List.ItemsSource = snap.Services.Select(s => new SvcRow
        {
            Key = s.Key, Installed = s.Installed, Running = s.Running,
            Manageable = s.Role is ServiceRole.Php
                || s.Key is "nginx" or "apache" or "mariadb" or "redis" or "memcached" or "mailpit",
        }).ToList();
    }

    // Map a service key to the install token the Engine understands.
    private static string InstallToken(string key) => key switch
    {
        "nginx" or "mkcert" => key,
        _ when key.StartsWith("php") => key,   // "php" | "php@8.4"
        _ => key,
    };

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string key)
            await Op(() => EngineHost.Instance.Engine.Install(InstallToken(key)));
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string key)
            await Op(() => EngineHost.Instance.Engine.Start(key));
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key)
            await Op(() => EngineHost.Instance.Engine.Stop(key));
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string key)
            await Op(() => EngineHost.Instance.Engine.Update(InstallToken(key)));
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        var dlg = new ContentDialog
        {
            Title = "Uninstall", Content = $"Remove the {key} binaries from BHServe's bin folder?",
            PrimaryButtonText = "Uninstall", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close, XamlRoot = this.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            await Op(() => EngineHost.Instance.Engine.Uninstall(key));
    }

    private void EditIni_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        try
        {
            var ver = key == "php" ? Config.Load().DefaultPhp : key[(key.IndexOf('@') + 1)..];
            var ini = EngineHost.Instance.Engine.PhpIniPath(ver);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ini, UseShellExecute = true });
        }
        catch { }
    }

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Refresh();
    }
}
