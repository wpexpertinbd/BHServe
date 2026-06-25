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
            Manageable = s.Role is ServiceRole.Php || s.Key == "nginx",
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
        if ((sender as Button)?.Tag is string key)
            await Op(() => EngineHost.Instance.Engine.Stop(key));
    }

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        Refresh();
    }
}
