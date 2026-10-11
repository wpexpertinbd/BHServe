using System.Linq;
using BHServe.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed partial class LogsPage : Page
{
    public LogsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var current = FilePicker.SelectedItem as string;
        FilePicker.ItemsSource = EngineHost.Instance.Engine.LogFiles();
        if (current is not null && EngineHost.Instance.Engine.LogFiles().Contains(current))
            FilePicker.SelectedItem = current;
        else if (FilePicker.Items.Count > 0)
            FilePicker.SelectedIndex = System.Math.Max(0,
                EngineHost.Instance.Engine.LogFiles().ToList().FindIndex(f => f.Contains("nginx-error")));
    }

    private void File_Changed(object sender, SelectionChangedEventArgs e) => Load();
    private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private async void Load()
    {
        if (FilePicker.SelectedItem is not string name) return;
        var text = await System.Threading.Tasks.Task.Run(() => EngineHost.Instance.Engine.LogText(name, 500));
        LogText.Text = string.IsNullOrWhiteSpace(text) ? "(empty)" : text;
        Scroll.ChangeView(null, Scroll.ScrollableHeight, null);
    }
}
