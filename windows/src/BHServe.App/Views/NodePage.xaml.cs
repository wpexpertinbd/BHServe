using System;
using System.Text;
using System.Threading.Tasks;
using BHServe.App.Services;
using BHServe.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace BHServe.App.Views;

public sealed partial class NodePage : Page
{
    public NodePage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Refresh();

    private async void Refresh()
    {
        // Run `node list` with a private capture so only its output shows here.
        var text = await Task.Run(() =>
        {
            var sb = new StringBuilder();
            var eng = new Engine { Out = l => sb.AppendLine(l), Err = l => sb.AppendLine(l) };
            try { eng.Node("list"); } catch (Exception ex) { sb.AppendLine(ex.Message); }
            return sb.ToString().Trim();
        });
        OutputBox.Text = string.IsNullOrWhiteSpace(text) ? "No Node versions installed yet." : text;
    }

    private async void Install_Click(object s, RoutedEventArgs e)   => await Op("install");
    private async void Default_Click(object s, RoutedEventArgs e)   => await Op("default");
    private async void Uninstall_Click(object s, RoutedEventArgs e) => await Op("uninstall");

    private async Task Op(string sub)
    {
        var v = VerBox.Text.Trim();
        if (v.Length == 0) return;
        Busy.IsActive = true;
        await EngineHost.Instance.Run(() => EngineHost.Instance.Engine.Node(sub, v));
        Busy.IsActive = false;
        Refresh();
    }
}
