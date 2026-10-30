using Microsoft.UI.Xaml.Controls;
using RigShift.App.Services;

namespace RigShift.UI;

/// <summary>Navigation and page frame inside the main window; built again when the language changes.</summary>
public sealed partial class Shell : UserControl
{
    public Shell()
    {
        InitializeComponent();
    }

    /// <summary>The page on screen; the overview until something else was chosen.</summary>
    public AppPage Page { get; private set; } = AppPage.Overview;

    public void Navigate(AppPage page)
    {
        // Selecting the item navigates (SelectionChanged), whether the user clicked it or the app chose it; selecting
        // the selected one again does nothing.
        Navigation.SelectedItem = Items().First(i => (string)i.Tag == page.ToString());
    }

    private IEnumerable<NavigationViewItem> Items() =>
        Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>();

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem { Tag: string tag } item || !Enum.TryParse(tag, out AppPage page))
        {
            return;
        }

        Page = page;
        if (page == AppPage.Overview)
        {
            ContentFrame.Navigate(typeof(Pages.OverviewPage));
            return;
        }

        if (page == AppPage.Profiles)
        {
            ContentFrame.Navigate(typeof(Pages.ProfilesPage));
            return;
        }

        if (page == AppPage.Games)
        {
            // shortcut: two layout prototypes until one is chosen (#93); RIGSHIFT_PREVIEW_GAMES=B shows the library.
            ContentFrame.Navigate(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_GAMES") == "B" ? typeof(Pages.GamesLibraryPage) : typeof(Pages.GamesFlowPage));
            return;
        }

        // shortcut: one empty page with the item's name until the real pages exist (#93, #94).
        ContentFrame.Navigate(typeof(PlaceholderPage), item.Content);
    }
}
