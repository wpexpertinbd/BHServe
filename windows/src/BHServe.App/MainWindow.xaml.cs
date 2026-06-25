using BHServe.App.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BHServe.App;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = Nav.MenuItems[0];   // Dashboard
        ContentFrame.Navigate(typeof(DashboardPage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { ContentFrame.Navigate(typeof(SettingsPage)); return; }
        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag })
            ContentFrame.Navigate(tag switch
            {
                "sites"    => typeof(SitesPage),
                "services" => typeof(ServicesPage),
                _          => typeof(DashboardPage),
            });
    }

}
