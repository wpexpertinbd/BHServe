using System;
using System.Linq;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

/// <summary>Row shown in the dashboard service list.</summary>
public sealed class ServiceRow
{
    public required string Key { get; init; }
    public required string StatusText { get; init; }
    public required bool Running { get; init; }
    public required bool Installed { get; init; }
    public required bool AutoStart { get; init; }
    public string AutoText => AutoStart ? "auto-start" : "";
    public Brush DotBrush => new SolidColorBrush(
        Running ? Colors.SeaGreen : Installed ? Colors.Gray : Colors.DimGray);
}

public sealed partial class DashboardPage : Page
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };

    public DashboardPage()
    {
        InitializeComponent();
        EngineHost.Instance.LogAppended += OnLog;
        _timer.Tick += (_, _) => Refresh();
        LogBox.Text = EngineHost.Instance.LogText;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { Refresh(); _timer.Start(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) => _timer.Stop();

    private void OnLog(string line) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            LogBox.Text = EngineHost.Instance.LogText;
            LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null);
        });

    private async void Refresh()
    {
        Snapshot snap;
        try { snap = await EngineHost.Instance.Snapshot(); } catch { return; }
        var rows = snap.Services
            .Where(s => s.Installed || s.Running || s.AutoStart)
            .Select(s => new ServiceRow
            {
                Key = s.Key,
                Running = s.Running,
                Installed = s.Installed,
                AutoStart = s.AutoStart,
                StatusText = s.Running ? "running" : s.Installed ? "stopped" : "not installed",
            }).ToList();
        ServicesList.ItemsSource = rows;
        var up = rows.Count(r => r.Running);
        SubTitle.Text = $"{up} service(s) running · {snap.Sites.Count} site(s)";
    }

    private async void StartAll_Click(object sender, RoutedEventArgs e)   => await Op(() => EngineHost.Instance.Engine.Start("all"));
    private async void StopAll_Click(object sender, RoutedEventArgs e)    => await Op(() => EngineHost.Instance.Engine.Stop("all"));
    private async void RestartAll_Click(object sender, RoutedEventArgs e) => await Op(() => EngineHost.Instance.Engine.Restart("all"));

    private async System.Threading.Tasks.Task Op(Action action)
    {
        Busy.IsActive = true;
        StartBtn.IsEnabled = StopBtn.IsEnabled = RestartBtn.IsEnabled = false;
        await EngineHost.Instance.Run(action);
        Busy.IsActive = false;
        StartBtn.IsEnabled = StopBtn.IsEnabled = RestartBtn.IsEnabled = true;
        Refresh();
    }
}
