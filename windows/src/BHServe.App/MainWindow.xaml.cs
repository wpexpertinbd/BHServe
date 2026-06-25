using Microsoft.UI.Xaml.Controls;

namespace BHServe.App;

public sealed partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // TODO(windows): swap a Frame to the section's Page. Placeholder for now.
        if (args.IsSettingsSelected)
        {
            SectionLabel.Text = "Settings";
            return;
        }
        if (args.SelectedItemContainer is NavigationViewItem { Tag: string tag })
            SectionLabel.Text = char.ToUpper(tag[0]) + tag[1..];
    }
}
